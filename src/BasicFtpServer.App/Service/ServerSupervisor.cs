using System.Net.NetworkInformation;
using System.Net.Sockets;
using BasicFtpServer.App.Ipc;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using BasicFtpServer.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace BasicFtpServer.App.Service;

/// <summary>
/// Owns the FTP listener's lifecycle inside the service.
///
/// The retry loop is the important part. A service set to start automatically routinely
/// comes up before the network adapter has an address, and a one-shot bind would leave
/// scanning dead until somebody noticed and restarted the service by hand.
/// </summary>
public sealed class ServerSupervisor(ConfigStore store, LogRing log, ILogger<ServerSupervisor> logger)
    : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DpapiSecretProtector _protector = new();

    private FtpServerHost? _host;
    private ServerConfig _config = new();
    private Task? _supervisorTask;
    private CancellationTokenSource? _cts;

    private volatile bool _desiredRunning = true;
    private string? _lastError;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(2);
    private (int Available, int Checked) _passiveProbe;
    private int _disposed;

    public bool IsRunning => _host?.IsRunning == true;

    public bool IsRetrying => _desiredRunning && !IsRunning;

    public void Begin(CancellationToken cancellationToken)
    {
        _config = store.LoadOrDefault(out var configError);
        if (configError is not null)
        {
            logger.LogWarning("{Message}", configError);
            log.Add(LogKind.Warning, configError);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _supervisorTask = SuperviseAsync(_cts.Token);
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_desiredRunning && !IsRunning && DateTimeOffset.UtcNow >= _nextAttempt)
            {
                await TryStartAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TryStartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var host = new FtpServerHost(_config, _protector, log);
            host.Start();

            _host = host;
            _startedAt = DateTimeOffset.Now;
            _lastError = null;
            _retryDelay = TimeSpan.FromSeconds(2);

            logger.LogInformation("FTP server listening on port {Port}.", host.Port);
            WarnIfPassiveRangeUnusable(host);
        }
        catch (FtpBindException ex)
        {
            _lastError = ex.Message;
            _nextAttempt = DateTimeOffset.UtcNow + _retryDelay;

            logger.LogWarning("Could not start listener: {Message} Retrying in {Seconds}s.",
                ex.Message, (int)_retryDelay.TotalSeconds);
            log.Add(LogKind.Warning, $"{ex.Message} Retrying in {(int)_retryDelay.TotalSeconds}s.");

            _retryDelay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, _retryDelay.TotalSeconds * 2));
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _nextAttempt = DateTimeOffset.UtcNow + MaxRetryDelay;
            logger.LogError(ex, "Unexpected failure starting the FTP listener.");
            log.Add(LogKind.Error, $"Unexpected failure starting the listener: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Surfaces a passive range that overlaps a Windows reserved block. Left undetected this
    /// presents as passive transfers failing intermittently with no useful error anywhere.
    /// </summary>
    private void WarnIfPassiveRangeUnusable(FtpServerHost host)
    {
        _passiveProbe = host.ProbePassiveRange();

        if (_passiveProbe.Checked > 0 && _passiveProbe.Available == 0)
        {
            var message =
                $"None of the passive ports {_config.Server.PassivePortMin}-{_config.Server.PassivePortMax} could be bound. " +
                "The range almost certainly overlaps a Windows reserved block " +
                "(check with: netsh int ipv4 show excludedportrange protocol=tcp). Passive transfers will fail.";
            logger.LogError("{Message}", message);
            log.Add(LogKind.Error, message);
        }
        else if (_passiveProbe.Checked > 0 && _passiveProbe.Available < _passiveProbe.Checked / 2)
        {
            var message =
                $"Only {_passiveProbe.Available} of the first {_passiveProbe.Checked} passive ports are bindable. " +
                "Part of the configured range is reserved or in use.";
            logger.LogWarning("{Message}", message);
            log.Add(LogKind.Warning, message);
        }
    }

    public async Task StartServerAsync()
    {
        _desiredRunning = true;
        _nextAttempt = DateTimeOffset.MinValue;
        _retryDelay = TimeSpan.FromSeconds(2);
        await TryStartAsync().ConfigureAwait(false);
    }

    public async Task StopServerAsync()
    {
        _desiredRunning = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync().ConfigureAwait(false);
                _host = null;
                _startedAt = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ServerStatusDto GetStatus()
    {
        var host = _host;
        var sessions = host?.Sessions ?? [];

        return new ServerStatusDto(
            Running: host?.IsRunning == true,
            Retrying: IsRetrying,
            LastError: _lastError,
            Port: host?.Port ?? _config.Server.Port,
            LocalAddresses: LocalAddresses(),
            PassivePortMin: _config.Server.PassivePortMin,
            PassivePortMax: _config.Server.PassivePortMax,
            PassiveAvailable: _passiveProbe.Available,
            PassiveChecked: _passiveProbe.Checked,
            StartedAt: _startedAt,
            Sessions: [.. sessions.Select(s => new SessionDto(
                s.Id, s.RemoteAddress, s.User, s.ConnectedAt, s.LastCommand, s.BytesReceived, s.BytesSent))]);
    }

    /// <summary>
    /// The machine's usable IPv4 addresses. The tray shows these prominently because copiers
    /// are configured with a hardcoded address — if DHCP moves this machine, every device
    /// stops scanning at once.
    /// </summary>
    private static string[] LocalAddresses()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .Distinct()
                    .Order(),
            ];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Returns config with passwords decrypted for display in the settings UI.</summary>
    public ConfigTransfer GetConfigForEditing()
    {
        var copy = _config.Clone();
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in copy.Users)
        {
            passwords[user.Name] = _protector.TryUnprotect(user.PasswordProtected, out var plain) ? plain : "";
            user.PasswordProtected = "";
        }

        return new ConfigTransfer(copy, passwords);
    }

    /// <summary>Re-protects passwords, persists, and restarts the listener with the new settings.</summary>
    public async Task ApplyConfigAsync(ConfigTransfer transfer)
    {
        var incoming = transfer.Config;

        foreach (var user in incoming.Users)
        {
            var plain = transfer.Passwords.TryGetValue(user.Name, out var value) ? value : "";
            user.PasswordProtected = _protector.Protect(plain);
        }

        store.EnsureDirectories();
        store.Save(incoming);
        _config = incoming;

        log.Add(LogKind.Server, "Configuration updated; restarting listener.");
        logger.LogInformation("Configuration updated; restarting listener.");

        var shouldRun = _desiredRunning;
        await StopServerAsync().ConfigureAwait(false);

        if (shouldRun)
        {
            await StartServerAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes at most once, however many times it is called.
    ///
    /// It is called twice on every shutdown: FtpWorker.StopAsync disposes the supervisor so
    /// the listener closes before the rest of the host goes, and then the container disposes
    /// the same singleton again. The second pass used to reach CancelAsync on a disposed
    /// CancellationTokenSource and throw out of host disposal, which Program.RunService
    /// caught, logged as "The service terminated unexpectedly", and turned into exit code 1.
    /// A deliberate stop ending in a non-zero exit is not cosmetic: the service is
    /// registered with RESTART failure actions, so the SCM can read it as a crash and start
    /// the service again underneath whoever just stopped it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Interlocked rather than a plain bool: the two calls come from different points in
        // the host's teardown, and nothing guarantees they are on the same thread.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_supervisorTask is not null)
        {
            try
            {
                await _supervisorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await StopServerAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _gate.Dispose();
    }
}
