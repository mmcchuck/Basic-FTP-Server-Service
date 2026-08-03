namespace BasicFtpServer.App.Tray;

/// <summary>
/// Named kernel objects used to coordinate tray instances within a single logon session.
/// Both names are "Local\", so each session gets its own tray — correct on a machine with
/// fast user switching or multiple RDP sessions.
/// </summary>
public static class TraySignals
{
    public const string SingleInstanceMutex = @"Local\BasicFtpServerServiceTray";

    /// <summary>
    /// Set by a second launch to ask the tray that is already running to show its settings
    /// window. Without this, clicking the Start Menu shortcut while the tray is running
    /// appears to do nothing at all.
    /// </summary>
    public const string ShowUiEvent = @"Local\BasicFtpServerServiceTray.ShowUi";
}
