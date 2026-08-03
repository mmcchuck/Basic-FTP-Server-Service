using BasicFtpServer.App.Ipc;
using BasicFtpServer.Core.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BasicFtpServer.App.Service;

public sealed class FtpWorker(
    ServerSupervisor supervisor,
    ControlPipeServer pipeServer,
    LogRing log,
    ILogger<FtpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Basic FTP Server Service starting.");

        // Mirror the in-memory ring (which feeds the tray's live view) into the rolling
        // file log, so a problem reported after the fact is still diagnosable.
        log.EntryAdded += WriteToFileLog;

        // Begin returns immediately; the supervisor keeps retrying the bind in the
        // background so a not-yet-ready network adapter never becomes a fatal start failure.
        supervisor.Begin(stoppingToken);

        await pipeServer.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Protocol chatter goes to Debug so a busy day of scanning does not bury the events
    /// that actually matter; everything else keeps its own severity.
    /// </summary>
    private void WriteToFileLog(LogEntry entry)
    {
        var level = entry.Kind switch
        {
            LogKind.Error => LogLevel.Error,
            LogKind.Warning => LogLevel.Warning,
            LogKind.Command or LogKind.Reply => LogLevel.Debug,
            _ => LogLevel.Information,
        };

        var scope = entry.SessionId > 0 ? $"[session {entry.SessionId}] " : "";
        logger.Log(level, "{Scope}{Message}", scope, entry.Message);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Basic FTP Server Service stopping.");
        log.EntryAdded -= WriteToFileLog;
        await supervisor.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
