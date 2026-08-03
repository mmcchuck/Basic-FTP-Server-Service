using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BasicFtpServer.App.Service;
using BasicFtpServer.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BasicFtpServer.App.Ipc;

/// <summary>
/// The service's control endpoint.
///
/// Session 0 isolation means the service can never draw its own UI, so everything the tray
/// needs — status, config, live log, start/stop — comes through here. The pipe ACL admits
/// only SYSTEM and Administrators, and mutating commands additionally verify that the
/// caller really holds an elevated token.
/// </summary>
public sealed class ControlPipeServer(
    ServerSupervisor supervisor,
    LogRing log,
    ILogger<ControlPipeServer> logger)
{
    public const string PipeName = "BasicFtpServerService";
    private const int MaxInstances = 8;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream stream;
            try
            {
                stream = CreateStream();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not create the control pipe. The tray UI will not be able to connect.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Control pipe accept failed.");
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // Handle detached so a slow client cannot stall the next connection.
            _ = HandleConnectionAsync(stream, cancellationToken);
        }
    }

    private static NamedPipeServerStream CreateStream()
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            var request = ControlJson.Deserialize<ControlRequest>(line);
            var response = request is null
                ? new ControlResponse(false, Error: "Malformed request.")
                : await ExecuteAsync(request, stream).ConfigureAwait(false);

            await writer.WriteLineAsync(ControlJson.Serialize(response)).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                stream.WaitForPipeDrain();
            }
            catch
            {
                // Client already gone.
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client disconnected mid-exchange; nothing actionable.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control pipe request failed.");
        }
        finally
        {
            try
            {
                if (stream.IsConnected)
                {
                    stream.Disconnect();
                }
            }
            catch
            {
                // Already torn down.
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ControlResponse> ExecuteAsync(ControlRequest request, NamedPipeServerStream stream)
    {
        var mutating = request.Command is
            ControlCommands.SetConfig or
            ControlCommands.StartServer or
            ControlCommands.StopServer or
            ControlCommands.ClearLog;

        // Defence in depth: the ACL should already have excluded non-administrators.
        if (mutating && !CallerIsAdministrator(stream))
        {
            logger.LogWarning("Rejected {Command} from a non-elevated caller.", request.Command);
            return new ControlResponse(false, Error: "Administrator rights are required for this action.");
        }

        switch (request.Command)
        {
            case ControlCommands.GetStatus:
                return new ControlResponse(true, ControlJson.Serialize(supervisor.GetStatus()));

            case ControlCommands.GetConfig:
                return new ControlResponse(true, ControlJson.Serialize(supervisor.GetConfigForEditing()));

            case ControlCommands.SetConfig:
            {
                var transfer = ControlJson.Deserialize<ConfigTransfer>(request.Payload);
                if (transfer?.Config is null)
                {
                    return new ControlResponse(false, Error: "No configuration supplied.");
                }

                await supervisor.ApplyConfigAsync(transfer).ConfigureAwait(false);
                return new ControlResponse(true, ControlJson.Serialize(supervisor.GetStatus()));
            }

            case ControlCommands.StartServer:
                await supervisor.StartServerAsync().ConfigureAwait(false);
                return new ControlResponse(true, ControlJson.Serialize(supervisor.GetStatus()));

            case ControlCommands.StopServer:
                await supervisor.StopServerAsync().ConfigureAwait(false);
                return new ControlResponse(true, ControlJson.Serialize(supervisor.GetStatus()));

            case ControlCommands.GetLog:
            {
                _ = long.TryParse(request.Payload, out var since);
                var entries = log.SnapshotSince(since);
                var lines = entries
                    .Select(e => new LogLineDto(e.Sequence, e.Kind.ToString(), e.ToString()))
                    .ToArray();
                var last = lines.Length > 0 ? lines[^1].Sequence : since;
                return new ControlResponse(true, ControlJson.Serialize(new LogPageDto(last, lines)));
            }

            case ControlCommands.ClearLog:
                log.Clear();
                return new ControlResponse(true);

            default:
                return new ControlResponse(false, Error: $"Unknown command '{request.Command}'.");
        }
    }

    private static bool CallerIsAdministrator(NamedPipeServerStream stream)
    {
        try
        {
            var isAdmin = false;
            stream.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
            return isAdmin;
        }
        catch
        {
            return false;
        }
    }
}
