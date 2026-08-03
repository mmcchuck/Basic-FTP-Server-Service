using System.IO.Pipes;
using System.Text;

namespace BasicFtpServer.App.Ipc;

/// <summary>Tray-side client for the service's control pipe.</summary>
public static class ControlPipeClient
{
    private const int DefaultTimeoutMs = 5000;

    public static async Task<ControlResponse> SendAsync(
        string command,
        string? payload = null,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", ControlPipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);

            await writer.WriteLineAsync(ControlJson.Serialize(new ControlRequest(command, payload)))
                .ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                return new ControlResponse(false, Error: "The service closed the connection without replying.");
            }

            return ControlJson.Deserialize<ControlResponse>(line)
                   ?? new ControlResponse(false, Error: "Malformed reply from the service.");
        }
        catch (OperationCanceledException)
        {
            return new ControlResponse(false, Error: ServiceUnavailableMessage);
        }
        catch (TimeoutException)
        {
            return new ControlResponse(false, Error: ServiceUnavailableMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return new ControlResponse(false,
                Error: "Access to the service was denied. The tray must run elevated.");
        }
        catch (IOException ex)
        {
            return new ControlResponse(false, Error: $"Could not talk to the service: {ex.Message}");
        }
    }

    private const string ServiceUnavailableMessage =
        "The Basic FTP Server service is not responding. Check that it is running in services.msc.";

    public static async Task<ServerStatusDto?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ControlCommands.GetStatus, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Ok ? ControlJson.Deserialize<ServerStatusDto>(response.Payload) : null;
    }
}
