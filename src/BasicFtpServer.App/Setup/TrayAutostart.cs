using System.Security;
using System.Text;

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
    /// account would never start a tray for the real user.
    ///
    /// Registration is attempted from an XML definition first. That is not gold-plating —
    /// the plain `schtasks /Create` command line cannot express three things this task needs
    /// and silently applies bad defaults for all of them:
    ///
    ///   * ExecutionTimeLimit defaults to PT72H, so the tray is killed after three days.
    ///     On a scan-receiving PC that stays logged in, it just disappears.
    ///   * DisallowStartIfOnBatteries and StopIfGoingOnBatteries default to true, so on a
    ///     laptop the tray never starts on battery and is stopped when unplugged.
    ///   * /RU cannot set the account without a password prompt, so the logon trigger ends
    ///     up scoped to "any user" rather than the intended one.
    ///   * MultipleInstancesPolicy cannot be expressed at all and defaults to IgnoreNew,
    ///     which breaks the Start Menu shortcut: it runs this task, and the second instance
    ///     exists only to ask the running tray to show its settings window and exit.
    ///     IgnoreNew refuses to start that instance (0x800710E0), so the click does nothing.
    ///     Parallel is safe here — Program.RunTray holds a single-instance mutex, so a
    ///     second tray icon cannot appear.
    ///
    /// The command-line forms remain as fallbacks so this can never do worse than before.
    /// </summary>
    public static ProcessResult Register(string executablePath, string? runAsUser = null)
    {
        Unregister();

        var account = string.IsNullOrWhiteSpace(runAsUser)
            ? $@"{Environment.UserDomainName}\{Environment.UserName}"
            : runAsUser.Trim();

        var fromXml = RegisterFromXml(executablePath, account);
        if (fromXml.Success)
        {
            return fromXml;
        }

        // /RL HIGHEST is what avoids the per-logon UAC prompt; /F overwrites any stale task.
        var baseArguments =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F";

        // /IT should let an elevated task be registered for another account without storing
        // a password. schtasks documents that it may still prompt; ProcessRunner closes stdin
        // so such a prompt fails fast instead of hanging, and we drop to the last form below.
        var scoped = SchTasks($"{baseArguments} /RU \"{account}\" /IT");
        return scoped.Success ? scoped : SchTasks(baseArguments);
    }

    private static ProcessResult RegisterFromXml(string executablePath, string account)
    {
        // UTF-16 is what the task schema declares and what schtasks has historically
        // required. Current Windows builds also accept UTF-8, but writing UTF-16 costs
        // nothing and avoids depending on that.
        var path = Path.Combine(Path.GetTempPath(), $"{TaskName}-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(path, BuildTaskXml(executablePath, account), new UnicodeEncoding(false, true));
            return SchTasks($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A stray temp file is not worth failing registration over.
            }
        }
    }

    private static string BuildTaskXml(string executablePath, string account)
    {
        var command = SecurityElement.Escape(executablePath);
        var user = SecurityElement.Escape(account);

        // InteractiveToken is what lets an elevated task be registered for a named account
        // without storing that account's password.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts the Basic FTP Server Service tray icon at logon.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static ProcessResult Unregister() => SchTasks($"/Delete /TN \"{TaskName}\" /F");

    public static bool IsRegistered() => SchTasks($"/Query /TN \"{TaskName}\"").Success;

    private static ProcessResult SchTasks(string arguments) => ProcessRunner.Run("schtasks.exe", arguments);
}
