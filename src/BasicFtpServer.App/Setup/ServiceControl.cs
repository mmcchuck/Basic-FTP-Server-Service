using System.Diagnostics;
using System.ServiceProcess;

namespace BasicFtpServer.App.Setup;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Pending,
    Unknown,
}

/// <summary>Registration and lifecycle control for the Windows service.</summary>
public static class ServiceControl
{
    public const string ServiceName = "BasicFtpServerService";
    public const string DisplayName = "Basic FTP Server Service";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static ProcessResult Install()
    {
        var binPath = $"\\\"{ExecutablePath}\\\" --service";

        // Re-point an existing registration rather than failing, so an upgrade that installs
        // to a new directory does not leave the service running the old binary.
        var alreadyInstalled = GetState() != ServiceState.NotInstalled;

        var create = alreadyInstalled
            ? Sc($"config {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"")
            : Sc($"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");

        if (!create.Success)
        {
            return create;
        }

        Sc($"description {ServiceName} \"Receives scan-to-FTP jobs from copiers and multifunction printers.\"");

        // Restart on failure. Without this a crash leaves scanning silently dead until
        // somebody notices, which is exactly the problem this project exists to solve.
        Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000");

        return create;
    }

    public static ProcessResult Uninstall()
    {
        Stop();
        return Sc($"delete {ServiceName}");
    }

    public static ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or
                    ServiceControllerStatus.StopPending or
                    ServiceControllerStatus.ContinuePending or
                    ServiceControllerStatus.PausePending => ServiceState.Pending,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    public static bool Start(TimeSpan? timeout = null)
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Running)
            {
                return true;
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout ?? TimeSpan.FromSeconds(30));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Stop(TimeSpan? timeout = null)
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return true;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout ?? TimeSpan.FromSeconds(30));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Restart()
    {
        Stop();
        return Start();
    }

    private static ProcessResult Sc(string arguments) => ProcessRunner.Run("sc.exe", arguments);
}
