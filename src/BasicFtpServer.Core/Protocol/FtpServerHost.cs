using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BasicFtpServer.Core.Auth;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using BasicFtpServer.Core.Net;

namespace BasicFtpServer.Core.Protocol;

public sealed record SessionSnapshot(
    int Id,
    string RemoteAddress,
    string? User,
    DateTimeOffset ConnectedAt,
    string LastCommand,
    long BytesReceived,
    long BytesSent);

/// <summary>Thrown when the control port cannot be bound, with an explanation worth showing a user.</summary>
public sealed class FtpBindException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Owns the control listener and the set of live sessions. Deliberately free of any service
/// or UI dependency so it can be started inside a test on a random port.
/// </summary>
public sealed class FtpServerHost : IAsyncDisposable
{
    private readonly ServerConfig _config;
    private readonly LogRing _log;
    private readonly FtpServerContext _context;
    private readonly IpAccessList _allowList;
    private readonly ConcurrentDictionary<int, FtpSession> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextSessionId;

    public FtpServerHost(ServerConfig config, ISecretProtector protector, LogRing log)
    {
        _config = config;
        _log = log;
        _allowList = new IpAccessList(config.Server.AllowedClientIps);

        var users = new UserStore(config.Users, protector);
        var allocator = new PassivePortAllocator(config.Server.PassivePortMin, config.Server.PassivePortMax);
        _context = new FtpServerContext(config, users, log, allocator);
    }

    public bool IsRunning => _listener is not null;

    /// <summary>The port actually bound. Differs from the configured port when 0 was requested.</summary>
    public int Port { get; private set; }

    public UserStore Users => _context.Users;

    public IReadOnlyList<SessionSnapshot> Sessions => [.. _sessions.Values.Select(s => new SessionSnapshot(
        s.Id,
        s.RemoteEndPoint.Address.ToString(),
        s.UserName,
        s.ConnectedAt,
        s.LastCommand,
        s.BytesReceived,
        s.BytesSent))];

    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        var address = ParseListenAddress(_config.Server.ListenAddress);
        _listener = CreateListener(address, _config.Server.Port);

        try
        {
            _listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _listener = null;
            throw new FtpBindException(
                $"Port {_config.Server.Port} is already in use. Another FTP server (often the IIS FTP " +
                "service) is running. Stop it, or change the port in Settings.", ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            _listener = null;
            throw new FtpBindException(
                $"Cannot bind to {_config.Server.ListenAddress}. The address does not exist on this machine " +
                "yet — this is normal shortly after boot, before the network adapter is ready.", ex);
        }
        catch (SocketException ex)
        {
            _listener = null;
            throw new FtpBindException($"Could not bind to port {_config.Server.Port}: {ex.Message}", ex);
        }

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);

        _log.Add(LogKind.Server, $"Listening on {_config.Server.ListenAddress}:{Port}");

        if (!_allowList.IsEmpty)
        {
            _log.Add(LogKind.Server, "Client IP allow-list is active.");
        }
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // Already torn down.
        }

        _listener = null;

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;

        _log.Add(LogKind.Server, "Stopped listening.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener!;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Add(LogKind.Warning, $"Accept failed: {ex.Message}");
                continue;
            }

            // Each session runs detached; one slow copier must never block the next.
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;

        if (!_allowList.IsAllowed(remote.Address))
        {
            _log.Add(LogKind.Warning, $"Rejected connection from {remote.Address}: not in the allow-list.");
            client.Dispose();
            return;
        }

        if (_sessions.Count >= _config.Server.MaxConnections)
        {
            _log.Add(LogKind.Warning, $"Rejected connection from {remote.Address}: connection limit reached.");
            await TryWriteRefusalAsync(client).ConfigureAwait(false);
            client.Dispose();
            return;
        }

        var id = Interlocked.Increment(ref _nextSessionId);
        var session = new FtpSession(id, client, _context);
        _sessions[id] = session;

        try
        {
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Error, $"Unhandled session failure: {ex.Message}", id);
        }
        finally
        {
            _sessions.TryRemove(id, out _);
        }
    }

    private static async Task TryWriteRefusalAsync(TcpClient client)
    {
        try
        {
            var payload = "421 Too many connections.\r\n"u8.ToArray();
            await client.GetStream().WriteAsync(payload).ConfigureAwait(false);
        }
        catch
        {
            // Nothing useful to do if we cannot even send the refusal.
        }
    }

    private static IPAddress ParseListenAddress(string value) =>
        IPAddress.TryParse(value, out var parsed) ? parsed : IPAddress.Any;

    /// <summary>
    /// Binds dual-stack when listening on all interfaces so both IPv4 and IPv6 clients
    /// work, falling back to IPv4-only where IPv6 is disabled.
    /// </summary>
    private static TcpListener CreateListener(IPAddress address, int port)
    {
        if (!address.Equals(IPAddress.Any))
        {
            return new TcpListener(address, port);
        }

        try
        {
            var dualStack = new TcpListener(IPAddress.IPv6Any, port);
            dualStack.Server.DualMode = true;
            return dualStack;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            return new TcpListener(IPAddress.Any, port);
        }
    }

    /// <summary>
    /// Reports how much of the configured passive range can actually be bound. A low count
    /// nearly always means the range overlaps a Windows reserved block.
    /// </summary>
    public (int Available, int Checked) ProbePassiveRange() =>
        _context.PassivePorts.ProbeAvailability(IPAddress.Loopback);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
