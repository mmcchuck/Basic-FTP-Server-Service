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

    /// <summary>
    /// Registers the logon task.
    ///
    /// <paramref name="runAsUser"/> should be the person who will actually be logged in,
    /// which is not necessarily whoever ran the installer: with over-the-shoulder UAC the
    /// installer runs as a separate admin account, and a task registered against that
    /// account would never start a tray for the real user. Falls back to the account-less
    /// form if the explicit registration is rejected, so this can never do worse than
    /// letting schtasks pick the default.
    /// </summary>
    public static ProcessResult Register(string executablePath, string? runAsUser = null)
    {
        Unregister();

        // /RL HIGHEST is what avoids the per-logon UAC prompt; /F overwrites any stale task.
        var baseArguments =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F";

        if (!string.IsNullOrWhiteSpace(runAsUser))
        {
            // /IT runs the task with the user's interactive token, which is what should let
            // an elevated task be registered for another account without storing a password.
            // /NP would also avoid a password but forces the task to run non-interactively,
            // which is useless for a tray icon.
            //
            // schtasks documents that it may still prompt for a password here. ProcessRunner
            // closes stdin so such a prompt fails immediately rather than hanging, and we
            // then fall through to the account-less form below — never worse than letting
            // schtasks pick the default.
            var scoped = SchTasks($"{baseArguments} /RU \"{runAsUser.Trim()}\" /IT");
            if (scoped.Success)
            {
                return scoped;
            }
        }

        return SchTasks(baseArguments);
    }

    public static ProcessResult Unregister() => SchTasks($"/Delete /TN \"{TaskName}\" /F");

    public static bool IsRegistered() => SchTasks($"/Query /TN \"{TaskName}\"").Success;

    private static ProcessResult SchTasks(string arguments) => ProcessRunner.Run("schtasks.exe", arguments);
}
