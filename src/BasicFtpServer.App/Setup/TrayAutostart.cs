namespace BasicFtpServer.App.Setup;

/// <summary>
/// Registers the tray UI to start at logon.
///
/// A scheduled task is used rather than a Run-key shortcut because the tray is manifested
/// requireAdministrator: a Run-key entry would produce a UAC prompt at every single logon,
/// whereas a logon task set to run with highest privileges starts elevated silently.
/// </summary>
public static class TrayAutostart
{
    public const string TaskName = "BasicFtpServerServiceTray";

    public static ProcessResult Register(string executablePath)
    {
        Unregister();

        // /RL HIGHEST is what avoids the per-logon UAC prompt; /F overwrites any stale task.
        return SchTasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\" --tray\" " +
            "/SC ONLOGON /RL HIGHEST /F");
    }

    public static ProcessResult Unregister() => SchTasks($"/Delete /TN \"{TaskName}\" /F");

    public static bool IsRegistered() => SchTasks($"/Query /TN \"{TaskName}\"").Success;

    private static ProcessResult SchTasks(string arguments) => ProcessRunner.Run("schtasks.exe", arguments);
}
