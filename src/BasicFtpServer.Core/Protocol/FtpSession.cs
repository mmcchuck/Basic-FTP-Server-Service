using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BasicFtpServer.Core.Auth;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using BasicFtpServer.Core.Files;

namespace BasicFtpServer.Core.Protocol;

/// <summary>
/// One client connection. Owns the control channel, authentication state, current
/// directory, and the pending data channel.
/// </summary>
public sealed class FtpSession : IDisposable
{
    private static readonly TimeSpan DataConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpClient _control;
    private readonly FtpServerContext _ctx;
    private readonly ControlChannel _channel;
    private readonly DataChannel _data = new();

    private FtpUser? _user;
    private string _pendingUser = "";
    private VirtualPathResolver? _paths;
    private long _restartOffset;
    private string? _renameFrom;
    private bool _quit;

    public FtpSession(int id, TcpClient control, FtpServerContext context)
    {
        Id = id;
        _control = control;
        _ctx = context;
        _channel = new ControlChannel(control.GetStream(), context.FallbackEncoding);

        RemoteEndPoint = (IPEndPoint)control.Client.RemoteEndPoint!;
        LocalEndPoint = (IPEndPoint)control.Client.LocalEndPoint!;
        ConnectedAt = DateTimeOffset.Now;
    }

    public int Id { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public IPEndPoint LocalEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; }
    public string? UserName => _user?.Name;
    public string LastCommand { get; private set; } = "";
    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }

    /// <summary>True once TYPE I is in effect. ASCII mode is accepted but never transforms data — see HandleType.</summary>
    public bool BinaryMode { get; private set; } = true;

    private CompatibilitySettings Compat => _ctx.Config.Compatibility;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log(LogKind.Server, $"Connected from {RemoteEndPoint.Address}");

        try
        {
            await ReplyAsync(220, Compat.Greeting, cancellationToken).ConfigureAwait(false);

            while (!_quit && !cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadWithIdleTimeoutAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await TryReplyAsync(421, "Idle timeout, closing control connection.").ConfigureAwait(false);
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            // Embedded FTP clients routinely drop the control connection without sending
            // QUIT once they have their 226. Logging that as a warning would bury the
            // failures that actually matter.
            if (IsConnectionReset(ex))
            {
                Log(LogKind.Server, "Client closed the connection without sending QUIT.");
            }
            else
            {
                Log(LogKind.Warning, $"Connection dropped: {ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            Log(LogKind.Error, $"Session error: {ex}");
        }
        finally
        {
            Log(LogKind.Server, "Disconnected");
            Dispose();
        }
    }

    private async Task<string?> ReadWithIdleTimeoutAsync(CancellationToken cancellationToken)
    {
        var idle = _ctx.Config.Server.IdleTimeoutSeconds;
        if (idle <= 0)
        {
            return await _channel.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(idle));
        return await _channel.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        var argument = space < 0 ? "" : line[(space + 1)..];

        LastCommand = verb;

        if (_ctx.Config.Logging.LogProtocolCommands)
        {
            // Never let a password reach the log or the live tray view.
            var shown = verb == "PASS" ? "PASS ****" : line;
            Log(LogKind.Command, shown);
        }

        switch (verb)
        {
            case "USER": await HandleUserAsync(argument, cancellationToken); break;
            case "PASS": await HandlePassAsync(argument, cancellationToken); break;
            case "QUIT":
                await ReplyAsync(221, "Goodbye.", cancellationToken);
                _quit = true;
                break;

            case "SYST":
                // Claiming UNIX is what makes clients expect the `ls -l` listing we emit.
                await ReplyAsync(215, "UNIX Type: L8", cancellationToken);
                break;

            case "FEAT": await HandleFeatAsync(cancellationToken); break;
            case "OPTS": await HandleOptsAsync(argument, cancellationToken); break;
            case "NOOP": await ReplyAsync(200, "OK.", cancellationToken); break;
            case "CLNT": await ReplyAsync(200, "Noted.", cancellationToken); break;
            case "ALLO": await ReplyAsync(200, "OK.", cancellationToken); break;

            case "AUTH":
                // Some devices probe for TLS first. A clean rejection makes them fall back
                // to plaintext instead of hanging or erroring out.
                await ReplyAsync(534, "TLS is not supported by this server.", cancellationToken);
                break;

            case "PBSZ":
            case "PROT":
                await ReplyAsync(503, "Use AUTH first.", cancellationToken);
                break;

            case "TYPE": await HandleTypeAsync(argument, cancellationToken); break;

            case "MODE":
                await (argument.Trim().ToUpperInvariant() == "S"
                    ? ReplyAsync(200, "Mode set to S.", cancellationToken)
                    : ReplyAsync(504, "Only stream mode is supported.", cancellationToken));
                break;

            case "STRU":
                await (argument.Trim().ToUpperInvariant() == "F"
                    ? ReplyAsync(200, "Structure set to F.", cancellationToken)
                    : ReplyAsync(504, "Only file structure is supported.", cancellationToken));
                break;

            case "PWD":
            case "XPWD": await HandlePwdAsync(cancellationToken); break;

            case "CWD":
            case "XCWD": await HandleCwdAsync(argument, cancellationToken); break;

            case "CDUP":
            case "XCUP": await HandleCwdAsync("..", cancellationToken); break;

            case "MKD":
            case "XMKD": await HandleMkdAsync(argument, cancellationToken); break;

            case "RMD":
            case "XRMD": await HandleRmdAsync(argument, cancellationToken); break;

            case "DELE": await HandleDeleAsync(argument, cancellationToken); break;
            case "RNFR": await HandleRnfrAsync(argument, cancellationToken); break;
            case "RNTO": await HandleRntoAsync(argument, cancellationToken); break;
            case "SIZE": await HandleSizeAsync(argument, cancellationToken); break;
            case "MDTM": await HandleMdtmAsync(argument, cancellationToken); break;
            case "REST": await HandleRestAsync(argument, cancellationToken); break;

            case "PASV": await HandlePasvAsync(cancellationToken); break;
            case "EPSV": await HandleEpsvAsync(argument, cancellationToken); break;
            case "PORT": await HandlePortAsync(argument, cancellationToken); break;
            case "EPRT": await HandleEprtAsync(argument, cancellationToken); break;

            case "LIST": await HandleListAsync(argument, names: false, cancellationToken); break;
            case "NLST": await HandleListAsync(argument, names: true, cancellationToken); break;

            case "RETR": await HandleRetrAsync(argument, cancellationToken); break;
            case "STOR": await HandleStorAsync(argument, append: false, cancellationToken); break;
            case "APPE": await HandleStorAsync(argument, append: true, cancellationToken); break;
            case "STOU": await HandleStouAsync(cancellationToken); break;

            case "ABOR":
                _data.Reset();
                await ReplyAsync(226, "No transfer in progress.", cancellationToken);
                break;

            case "STAT": await HandleStatAsync(cancellationToken); break;
            case "HELP": await ReplyAsync(214, "Basic FTP Server Service.", cancellationToken); break;
            case "SITE": await ReplyAsync(202, "No SITE commands are implemented.", cancellationToken); break;

            default:
                await ReplyAsync(500, $"Unknown command '{verb}'.", cancellationToken);
                break;
        }
    }

    // ---- Authentication -------------------------------------------------------------

    private async Task HandleUserAsync(string argument, CancellationToken cancellationToken)
    {
        _pendingUser = argument.Trim();
        _user = null;
        _paths = null;
        await ReplyAsync(331, $"Password required for {_pendingUser}.", cancellationToken);
    }

    private async Task HandlePassAsync(string argument, CancellationToken cancellationToken)
    {
        if (_pendingUser.Length == 0)
        {
            await ReplyAsync(503, "Send USER first.", cancellationToken);
            return;
        }

        var result = _ctx.Users.Authenticate(_pendingUser, argument, out var user);
        if (result != AuthResult.Success || user is null)
        {
            // The reply is deliberately identical for every failure so a probe cannot
            // enumerate valid account names; the specific reason goes to the log only.
            Log(LogKind.Warning, $"Login failed for '{_pendingUser}' ({result}) from {RemoteEndPoint.Address}");
            await ReplyAsync(530, "Login incorrect.", cancellationToken);
            return;
        }

        try
        {
            Directory.CreateDirectory(user.HomeDirectory);
        }
        catch (Exception ex)
        {
            Log(LogKind.Error, $"Home directory '{user.HomeDirectory}' unusable: {ex.Message}");
            await ReplyAsync(550, "Home directory is not accessible.", cancellationToken);
            return;
        }

        _user = user;
        _paths = new VirtualPathResolver(user.HomeDirectory, Compat.SanitizeFilenames);
        Log(LogKind.Server, $"User '{user.Name}' logged in, home '{user.HomeDirectory}'");
        await ReplyAsync(230, $"User {user.Name} logged in.", cancellationToken);
    }

    private bool RequireAuth() => _user is not null && _paths is not null;

    // ---- Capability negotiation -----------------------------------------------------

    private async Task HandleFeatAsync(CancellationToken cancellationToken)
    {
        var features = new List<string> { "UTF8", "SIZE" };

        if (!Compat.MinimalFeat)
        {
            features.Add("MDTM");
            features.Add("REST STREAM");
            features.Add("TVFS");
            if (Compat.EnableEpsv)
            {
                features.Add("EPSV");
            }

            if (Compat.EnableEprt)
            {
                features.Add("EPRT");
            }
        }

        var lines = new List<string> { "211-Features:" };
        lines.AddRange(features.Select(f => " " + f));
        lines.Add("211 End");

        foreach (var line in lines)
        {
            await _channel.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
        }

        if (_ctx.Config.Logging.LogProtocolCommands)
        {
            Log(LogKind.Reply, $"211 Features: {string.Join(", ", features)}");
        }
    }

    private async Task HandleOptsAsync(string argument, CancellationToken cancellationToken)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && parts[0].Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            var on = parts.Length < 2 || parts[1].Equals("ON", StringComparison.OrdinalIgnoreCase);
            if (on)
            {
                _channel.ForceUtf8();
            }

            await ReplyAsync(200, $"UTF8 {(on ? "enabled" : "left as-is")}.", cancellationToken);
            return;
        }

        await ReplyAsync(501, "Option not understood.", cancellationToken);
    }

    private async Task HandleTypeAsync(string argument, CancellationToken cancellationToken)
    {
        var code = argument.Trim().ToUpperInvariant();
        switch (code.Length > 0 ? code[0] : ' ')
        {
            case 'I':
            case 'L':
                BinaryMode = true;
                await ReplyAsync(200, "Type set to I.", cancellationToken);
                break;

            case 'A':
                // Accepted, but data is still transferred verbatim. Devices frequently
                // announce TYPE A and then send a binary PDF; doing real CRLF translation
                // would corrupt those scans, which is a far worse failure than the
                // theoretical line-ending mismatch on the rare genuine text upload.
                BinaryMode = false;
                Log(LogKind.Warning, "Client requested ASCII mode; data will be transferred unmodified.");
                await ReplyAsync(200, "Type set to A (transferred as binary).", cancellationToken);
                break;

            default:
                await ReplyAsync(504, "Unsupported type.", cancellationToken);
                break;
        }
    }

    // ---- Navigation -----------------------------------------------------------------

    private async Task HandlePwdAsync(CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        await ReplyAsync(257, $"{Quote(_paths!.CurrentDirectory)} is the current directory.", cancellationToken);
    }

    private async Task HandleCwdAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        if (!Directory.Exists(resolved.PhysicalPath))
        {
            // Several MFPs change into a dated subfolder they never create.
            if (!Compat.AutoCreateDirectories)
            {
                await ReplyAsync(550, "Directory not found.", cancellationToken);
                return;
            }

            try
            {
                Directory.CreateDirectory(resolved.PhysicalPath);
                Log(LogKind.Server, $"Auto-created directory '{resolved.VirtualPath}'");
            }
            catch (Exception ex)
            {
                await ReplyAsync(550, $"Could not create directory: {ex.Message}", cancellationToken);
                return;
            }
        }

        _paths.CurrentDirectory = resolved.VirtualPath;
        await ReplyAsync(250, $"Directory changed to {resolved.VirtualPath}.", cancellationToken);
    }

    private async Task HandleMkdAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.CreateDirectory, cancellationToken))
        {
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        try
        {
            Directory.CreateDirectory(resolved.PhysicalPath);
            await ReplyAsync(257, $"{Quote(resolved.VirtualPath)} created.", cancellationToken);
        }
        catch (Exception ex)
        {
            await ReplyAsync(550, $"Could not create directory: {ex.Message}", cancellationToken);
        }
    }

    private async Task HandleRmdAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Delete, cancellationToken))
        {
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        try
        {
            Directory.Delete(resolved.PhysicalPath, recursive: false);
            await ReplyAsync(250, "Directory removed.", cancellationToken);
        }
        catch (Exception ex)
        {
            await ReplyAsync(550, $"Could not remove directory: {ex.Message}", cancellationToken);
        }
    }

    private async Task HandleDeleAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Delete, cancellationToken))
        {
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        try
        {
            File.Delete(resolved.PhysicalPath);
            await ReplyAsync(250, "File deleted.", cancellationToken);
        }
        catch (Exception ex)
        {
            await ReplyAsync(550, $"Could not delete file: {ex.Message}", cancellationToken);
        }
    }

    private async Task HandleRnfrAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Write, cancellationToken))
        {
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        if (!File.Exists(resolved.PhysicalPath) && !Directory.Exists(resolved.PhysicalPath))
        {
            await ReplyAsync(550, "File not found.", cancellationToken);
            return;
        }

        _renameFrom = resolved.PhysicalPath;
        await ReplyAsync(350, "Ready for RNTO.", cancellationToken);
    }

    private async Task HandleRntoAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Write, cancellationToken))
        {
            return;
        }

        if (_renameFrom is null)
        {
            await ReplyAsync(503, "Send RNFR first.", cancellationToken);
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        try
        {
            if (Directory.Exists(_renameFrom))
            {
                Directory.Move(_renameFrom, resolved.PhysicalPath);
            }
            else
            {
                File.Move(_renameFrom, resolved.PhysicalPath, overwrite: false);
            }

            await ReplyAsync(250, "Rename successful.", cancellationToken);
        }
        catch (Exception ex)
        {
            await ReplyAsync(550, $"Rename failed: {ex.Message}", cancellationToken);
        }
        finally
        {
            _renameFrom = null;
        }
    }

    private async Task HandleSizeAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        var info = new FileInfo(resolved.PhysicalPath);
        await (info.Exists
            ? ReplyAsync(213, info.Length.ToString(CultureInfo.InvariantCulture), cancellationToken)
            : ReplyAsync(550, "File not found.", cancellationToken));
    }

    private async Task HandleMdtmAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        var info = new FileInfo(resolved.PhysicalPath);
        if (!info.Exists)
        {
            await ReplyAsync(550, "File not found.", cancellationToken);
            return;
        }

        // MDTM is defined as UTC; returning local time makes clients think every file is
        // hours old or in the future.
        var stamp = info.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        await ReplyAsync(213, stamp, cancellationToken);
    }

    private async Task HandleRestAsync(string argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            await ReplyAsync(501, "Invalid restart offset.", cancellationToken);
            return;
        }

        _restartOffset = offset;
        await ReplyAsync(350, $"Restarting at {offset}.", cancellationToken);
    }

    private async Task HandleStatAsync(CancellationToken cancellationToken)
    {
        await _channel.WriteLineAsync("211-Status:", cancellationToken);
        await _channel.WriteLineAsync($" Connected from {RemoteEndPoint.Address}", cancellationToken);
        await _channel.WriteLineAsync($" Logged in as {_user?.Name ?? "(none)"}", cancellationToken);
        await _channel.WriteLineAsync($" Type: {(BinaryMode ? "BINARY" : "ASCII")}", cancellationToken);
        await _channel.WriteLineAsync("211 End", cancellationToken);
    }

    // ---- Data channel setup ---------------------------------------------------------

    private async Task HandlePasvAsync(CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        var bindAddress = LocalIPv4();
        if (bindAddress is null)
        {
            await ReplyAsync(522, "PASV requires IPv4; use EPSV.", cancellationToken);
            return;
        }

        if (!_ctx.PassivePorts.TryAcquire(bindAddress, out var listener, out var port) || listener is null)
        {
            Log(LogKind.Error,
                $"No free passive port in {_ctx.PassivePorts.MinPort}-{_ctx.PassivePorts.MaxPort}. " +
                "The range may overlap a Windows reserved block (check: netsh int ipv4 show excludedportrange protocol=tcp).");
            await ReplyAsync(425, "No passive port available.", cancellationToken);
            return;
        }

        var advertised = AdvertisedAddress(bindAddress);
        _data.SetPassive(listener, new IPEndPoint(advertised, port));

        var bytes = advertised.GetAddressBytes();
        var reply = $"Entering Passive Mode ({bytes[0]},{bytes[1]},{bytes[2]},{bytes[3]},{port / 256},{port % 256})";
        await ReplyAsync(227, reply, cancellationToken);
    }

    private async Task HandleEpsvAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        if (!Compat.EnableEpsv)
        {
            await ReplyAsync(502, "EPSV is disabled.", cancellationToken);
            return;
        }

        if (argument.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(200, "EPSV ALL accepted.", cancellationToken);
            return;
        }

        var bindAddress = LocalIPv4() ?? LocalEndPoint.Address;
        if (!_ctx.PassivePorts.TryAcquire(bindAddress, out var listener, out var port) || listener is null)
        {
            await ReplyAsync(425, "No passive port available.", cancellationToken);
            return;
        }

        _data.SetPassive(listener, new IPEndPoint(bindAddress, port));
        await ReplyAsync(229, $"Entering Extended Passive Mode (|||{port}|)", cancellationToken);
    }

    private async Task HandlePortAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        var parts = argument.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 6 || !parts.All(p => byte.TryParse(p, out _)))
        {
            await ReplyAsync(501, "Malformed PORT command.", cancellationToken);
            return;
        }

        var values = parts.Select(byte.Parse).ToArray();
        var declared = new IPAddress(values[..4]);
        var port = (values[4] << 8) + values[5];

        var target = ResolveActiveTarget(declared);
        _data.SetActive(new IPEndPoint(target, port));
        await ReplyAsync(200, "PORT command successful.", cancellationToken);
    }

    private async Task HandleEprtAsync(string argument, CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return;
        }

        if (!Compat.EnableEprt)
        {
            await ReplyAsync(502, "EPRT is disabled.", cancellationToken);
            return;
        }

        // Format: |<af>|<address>|<port>|
        var trimmed = argument.Trim();
        if (trimmed.Length < 3)
        {
            await ReplyAsync(501, "Malformed EPRT command.", cancellationToken);
            return;
        }

        var delimiter = trimmed[0];
        var fields = trimmed.Split(delimiter);
        if (fields.Length < 4 ||
            !IPAddress.TryParse(fields[2], out var declared) ||
            !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            await ReplyAsync(501, "Malformed EPRT command.", cancellationToken);
            return;
        }

        var target = ResolveActiveTarget(declared);
        _data.SetActive(new IPEndPoint(target, port));
        await ReplyAsync(200, "EPRT command successful.", cancellationToken);
    }

    /// <summary>
    /// Decides where an active-mode data connection should actually go. Devices commonly
    /// advertise an address they cannot be reached on, so by default the control
    /// connection's peer address wins.
    /// </summary>
    private IPAddress ResolveActiveTarget(IPAddress declared)
    {
        var peer = RemoteEndPoint.Address;
        if (peer.IsIPv4MappedToIPv6)
        {
            peer = peer.MapToIPv4();
        }

        if (_ctx.Config.Server.IgnorePortCommandAddress)
        {
            if (!declared.Equals(peer))
            {
                Log(LogKind.Warning, $"Client advertised data address {declared}; using {peer} from the control connection instead.");
            }

            return peer;
        }

        return declared;
    }

    /// <summary>The IP to put in a 227 reply.</summary>
    private IPAddress AdvertisedAddress(IPAddress fallback)
    {
        var forced = _ctx.Config.Server.ForcedPassiveIp;
        if (!string.IsNullOrWhiteSpace(forced) && IPAddress.TryParse(forced, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    /// <summary>
    /// The IPv4 address this connection arrived on. With a dual-stack listener the local
    /// endpoint is an IPv4-mapped IPv6 address, which has to be unwrapped before it can go
    /// into a 227 reply or be used as a bind address.
    /// </summary>
    private IPAddress? LocalIPv4()
    {
        var local = LocalEndPoint.Address;
        if (local.AddressFamily == AddressFamily.InterNetwork)
        {
            return local;
        }

        return local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : null;
    }

    // ---- Transfers ------------------------------------------------------------------

    private async Task HandleListAsync(string argument, bool names, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.List, cancellationToken))
        {
            return;
        }

        // Clients send `ls` flags such as "-la"; they are not part of the path.
        var pathArgument = string.Join(' ', argument
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !token.StartsWith('-')));

        if (!_paths!.TryResolve(pathArgument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        var directory = new DirectoryInfo(resolved.PhysicalPath);
        if (!directory.Exists)
        {
            await ReplyAsync(550, "Directory not found.", cancellationToken);
            return;
        }

        string payload;
        try
        {
            payload = names
                ? ListingFormatter.FormatNames(directory)
                : ListingFormatter.Format(directory, Compat.UseDosListing, DateTime.Now);
        }
        catch (Exception ex)
        {
            await ReplyAsync(550, $"Could not list directory: {ex.Message}", cancellationToken);
            return;
        }

        await TransferOutAsync(_channel.CurrentEncoding.GetBytes(payload), "directory listing", cancellationToken);
    }

    private async Task HandleRetrAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Read, cancellationToken))
        {
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        if (!File.Exists(resolved.PhysicalPath))
        {
            await ReplyAsync(550, "File not found.", cancellationToken);
            return;
        }

        var offset = _restartOffset;
        _restartOffset = 0;

        TcpClient? data = null;
        try
        {
            await ReplyAsync(150, $"Opening data connection for {Path.GetFileName(resolved.PhysicalPath)}.", cancellationToken);
            data = await _data.OpenAsync(DataConnectTimeout, cancellationToken).ConfigureAwait(false);

            await using var file = new FileStream(
                resolved.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 81920, useAsync: true);

            if (offset > 0 && offset <= file.Length)
            {
                file.Seek(offset, SeekOrigin.Begin);
            }

            await using var stream = data.GetStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                BytesSent += read;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            data.Client.Shutdown(SocketShutdown.Send);

            await ReplyAsync(226, "Transfer complete.", cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleTransferFailureAsync(ex, cancellationToken);
        }
        finally
        {
            data?.Dispose();
            _data.Reset();
        }
    }

    private async Task HandleStorAsync(string argument, bool append, CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Write, cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            await ReplyAsync(501, "A filename is required.", cancellationToken);
            return;
        }

        if (!_paths!.TryResolve(argument, out var resolved, out var error))
        {
            await ReplyAsync(550, error, cancellationToken);
            return;
        }

        var offset = _restartOffset;
        _restartOffset = 0;

        TcpClient? data = null;
        try
        {
            await ReplyAsync(150, $"Opening data connection for {Path.GetFileName(resolved.PhysicalPath)}.", cancellationToken);
            data = await _data.OpenAsync(DataConnectTimeout, cancellationToken).ConfigureAwait(false);

            await using var stream = data.GetStream();
            var result = await UploadWriter
                .ReceiveAsync(stream, resolved.PhysicalPath, Compat, append, offset, cancellationToken)
                .ConfigureAwait(false);

            BytesReceived += result.BytesWritten;

            if (!result.Success)
            {
                Log(LogKind.Error, $"Upload of '{resolved.VirtualPath}' failed: {result.Error}");
                await ReplyAsync(550, result.Error ?? "Upload failed.", cancellationToken);
                return;
            }

            var landed = Path.GetFileName(result.FinalPath);
            var requested = Path.GetFileName(resolved.PhysicalPath);
            if (!string.Equals(landed, requested, StringComparison.Ordinal))
            {
                Log(LogKind.Transfer, $"'{requested}' already existed; saved as '{landed}'");
            }

            Log(LogKind.Transfer, $"Received {result.BytesWritten:N0} bytes -> {result.FinalPath}");
            await ReplyAsync(226, "Transfer complete.", cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleTransferFailureAsync(ex, cancellationToken);
        }
        finally
        {
            data?.Dispose();
            _data.Reset();
        }
    }

    private async Task HandleStouAsync(CancellationToken cancellationToken)
    {
        if (!await RequirePermissionAsync(p => p.Write, cancellationToken))
        {
            return;
        }

        var unique = $"upload-{DateTime.Now:yyyyMMdd-HHmmss-fff}.dat";
        await HandleStorAsync(unique, append: false, cancellationToken);
    }

    /// <summary>Sends an in-memory payload (a listing) over the data connection.</summary>
    private async Task TransferOutAsync(byte[] payload, string description, CancellationToken cancellationToken)
    {
        TcpClient? data = null;
        try
        {
            await ReplyAsync(150, $"Opening data connection for {description}.", cancellationToken);
            data = await _data.OpenAsync(DataConnectTimeout, cancellationToken).ConfigureAwait(false);

            await using var stream = data.GetStream();
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            BytesSent += payload.Length;

            data.Client.Shutdown(SocketShutdown.Send);
            await ReplyAsync(226, "Transfer complete.", cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleTransferFailureAsync(ex, cancellationToken);
        }
        finally
        {
            data?.Dispose();
            _data.Reset();
        }
    }

    private async Task HandleTransferFailureAsync(Exception ex, CancellationToken cancellationToken)
    {
        switch (ex)
        {
            case InvalidOperationException:
                await TryReplyAsync(425, "Use PORT or PASV first.").ConfigureAwait(false);
                break;

            case OperationCanceledException when !cancellationToken.IsCancellationRequested:
                Log(LogKind.Error, "Timed out waiting for the data connection. In active mode this usually means " +
                                   "the client is unreachable; in passive mode, that the passive port range is blocked.");
                await TryReplyAsync(425, "Could not open the data connection.").ConfigureAwait(false);
                break;

            case OperationCanceledException:
                break;

            case SocketException or IOException:
                Log(LogKind.Error, $"Data connection failed: {ex.Message}");
                await TryReplyAsync(426, "Data connection closed unexpectedly.").ConfigureAwait(false);
                break;

            default:
                Log(LogKind.Error, $"Transfer failed: {ex.Message}");
                await TryReplyAsync(451, "Local error during transfer.").ConfigureAwait(false);
                break;
        }
    }

    // ---- Plumbing -------------------------------------------------------------------

    private async Task<bool> RequirePermissionAsync(
        Func<FtpPermissions, bool> selector,
        CancellationToken cancellationToken)
    {
        if (!RequireAuth())
        {
            await ReplyAsync(530, "Not logged in.", cancellationToken);
            return false;
        }

        if (!selector(_user!.Permissions))
        {
            await ReplyAsync(550, "Permission denied.", cancellationToken);
            return false;
        }

        return true;
    }

    private async Task ReplyAsync(int code, string text, CancellationToken cancellationToken)
    {
        var line = $"{code} {text}";
        await _channel.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);

        if (_ctx.Config.Logging.LogProtocolCommands)
        {
            Log(LogKind.Reply, line);
        }
    }

    /// <summary>Best-effort reply used on teardown paths where the socket may already be gone.</summary>
    private async Task TryReplyAsync(int code, string text)
    {
        try
        {
            await _channel.WriteLineAsync($"{code} {text}", CancellationToken.None).ConfigureAwait(false);
            if (_ctx.Config.Logging.LogProtocolCommands)
            {
                Log(LogKind.Reply, $"{code} {text}");
            }
        }
        catch
        {
            // Peer already gone.
        }
    }

    private static bool IsConnectionReset(Exception ex)
    {
        var socketException = ex as SocketException ?? ex.InnerException as SocketException;
        return socketException?.SocketErrorCode
            is SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            or SocketError.Shutdown;
    }

    /// <summary>Escapes a path for a 257 reply, where embedded quotes must be doubled.</summary>
    private static string Quote(string path) => "\"" + path.Replace("\"", "\"\"") + "\"";

    private void Log(LogKind kind, string message) => _ctx.Log.Add(kind, message, Id);

    public void Dispose()
    {
        _data.Dispose();
        try
        {
            _control.Dispose();
        }
        catch
        {
            // Already closed.
        }
    }
}
