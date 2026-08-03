using System.Diagnostics;
using System.Text;

namespace BasicFtpServer.App.Setup;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

internal static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 30000)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return new ProcessResult(-1, $"Could not start {fileName}.");
        }

        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Nothing further to do.
            }

            return new ProcessResult(-1, $"{fileName} timed out.\n{output}");
        }

        return new ProcessResult(process.ExitCode, output.ToString().Trim());
    }
}
