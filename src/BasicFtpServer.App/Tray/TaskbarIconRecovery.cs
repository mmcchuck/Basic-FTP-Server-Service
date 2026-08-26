using System.Runtime.InteropServices;

namespace BasicFtpServer.App.Tray;

/// <summary>
/// Keeps the notification-area icon from being lost when the taskbar is not there to
/// receive it.
///
/// Explorer broadcasts the registered message "TaskbarCreated" whenever it creates the
/// notification area — at logon, and again after every Explorer restart — and WinForms'
/// NotifyIcon re-adds a visible icon when it receives that message. That recovery does not
/// reach this process on its own: the tray is manifested requireAdministrator and so runs
/// at high integrity, Explorer runs at medium, and UIPI silently drops a broadcast sent
/// upwards unless the receiving process opts in.
///
/// Without the opt-in the failure is invisible and lasts the whole session. The logon task
/// starts the tray while the shell is still coming up, so the first Shell_NotifyIcon add
/// has no taskbar to add to; the broadcast that would fix that is dropped; and the process
/// then sits there healthy, serving its menu and its settings window, with no icon.
/// </summary>
internal static class TaskbarIconRecovery
{
    /// <summary>
    /// Lets Explorer's TaskbarCreated broadcast through UIPI. Call before creating the
    /// NotifyIcon, so no broadcast can arrive in the gap.
    /// </summary>
    public static void AllowTaskbarCreatedBroadcast()
    {
        var message = RegisterWindowMessage(TaskbarCreatedMessage);
        if (message == 0)
        {
            return;
        }

        // A null window means the process-wide filter, which is what is wanted here: the
        // window that receives the broadcast belongs to NotifyIcon and is not exposed.
        // Failure is not worth reporting — it only costs the recovery this class provides,
        // and the tray is still fully usable from the Start Menu shortcut.
        ChangeWindowMessageFilterEx(IntPtr.Zero, message, MessageFilterAllow, IntPtr.Zero);
    }

    /// <summary>
    /// Whether the notification area exists yet. False means an icon added now goes nowhere,
    /// which is the normal state for the first moments of a logon.
    /// </summary>
    public static bool TaskbarExists() => FindWindow(ShellTrayClass, null) != IntPtr.Zero;

    private const string TaskbarCreatedMessage = "TaskbarCreated";
    private const string ShellTrayClass = "Shell_TrayWnd";
    private const uint MessageFilterAllow = 1; // MSGFLT_ALLOW

    // Classic DllImport rather than LibraryImport, matching Program.cs: the source-generated
    // variant requires AllowUnsafeBlocks across the whole project.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
}
