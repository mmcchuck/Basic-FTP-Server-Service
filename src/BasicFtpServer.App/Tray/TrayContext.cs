using System.Diagnostics;
using BasicFtpServer.App.Ipc;
using BasicFtpServer.App.Setup;

namespace BasicFtpServer.App.Tray;

/// <summary>
/// The tray icon. This process exists purely as UI for the service — it holds no server
/// state of its own and talks to the service exclusively over the control pipe.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _addressItem;
    private readonly ToolStripMenuItem _startStopItem;
    private readonly ToolStripMenuItem _openFolderItem;

    private readonly EventWaitHandle _showUiRequested;
    private readonly RegisteredWaitHandle _showUiRegistration;

    /// <summary>
    /// A never-shown window used purely to marshal onto the UI thread. ApplicationContext is
    /// not a Control, so there is otherwise nothing to Invoke against from the thread-pool
    /// callback that services the show-UI event.
    /// </summary>
    private readonly Form _uiThreadMarshaller;

    /// <summary>Runs only until a taskbar exists to hold the icon. Null once it has.</summary>
    private System.Windows.Forms.Timer? _iconWatchdog;

    private SettingsForm? _settings;
    private LogForm? _log;
    private ServerStatusDto? _status;

    public TrayContext(bool showSettingsOnStart = false)
    {
        // Before the NotifyIcon, so no TaskbarCreated broadcast can arrive in the gap.
        TaskbarIconRecovery.AllowTaskbarCreatedBroadcast();

        _statusItem = new ToolStripMenuItem("Checking…") { Enabled = false };
        _addressItem = new ToolStripMenuItem("") { Enabled = false };
        _startStopItem = new ToolStripMenuItem("Stop Server", null, (_, _) => _ = ToggleServerAsync());
        _openFolderItem = new ToolStripMenuItem("Open Scan Folder");

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Basic FTP Server Service") { Enabled = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(_addressItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Live Log…", null, (_, _) => ShowLog()));
        menu.Items.Add(_openFolderItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new ToolStripMenuItem("Restart Windows Service", null, (_, _) => _ = RestartServiceAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit Tray (server keeps running)", null, (_, _) => ExitTray()));

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(TrayState.Warning),
            Text = "Basic FTP Server Service",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        // The logon task starts this process while the shell is still coming up, so the
        // add above routinely has no taskbar to add to. AllowTaskbarCreatedBroadcast should
        // cover that on its own; this is the belt to its braces, because a logon is also
        // when Shell_NotifyIcon is documented to fail under load, and a missing icon is
        // invisible to whoever is waiting for it.
        if (!TaskbarIconRecovery.TaskbarExists())
        {
            StartIconWatchdog();
        }

        _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        _poll.Tick += (_, _) => _ = RefreshAsync();
        _poll.Start();

        _uiThreadMarshaller = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Minimized,
        };
        _ = _uiThreadMarshaller.Handle; // Force handle creation so BeginInvoke is usable.

        _showUiRequested = new EventWaitHandle(false, EventResetMode.AutoReset, TraySignals.ShowUiEvent);
        _showUiRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showUiRequested,
            (_, _) => OnShowUiRequested(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        if (showSettingsOnStart)
        {
            // Queued rather than called directly: the message loop does not exist until
            // Application.Run, so showing a window from here would not work.
            _uiThreadMarshaller.BeginInvoke(ShowSettings);
        }

        _ = RefreshAsync();
    }

    /// <summary>Runs on a thread-pool thread when another launch asks us to surface the UI.</summary>
    private void OnShowUiRequested()
    {
        try
        {
            _uiThreadMarshaller.BeginInvoke(ShowSettings);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Shutting down; the other process simply gets no window.
        }
    }

    private async Task RefreshAsync()
    {
        var serviceState = ServiceControl.GetState();
        _status = await ControlPipeClient.GetStatusAsync().ConfigureAwait(true);

        if (serviceState == ServiceState.NotInstalled)
        {
            Apply(TrayState.Stopped, "Service is not installed", "Run the installer, or BasicFtpServer.exe --install-service");
            _startStopItem.Enabled = false;
        }
        else if (_status is null)
        {
            Apply(TrayState.Stopped, $"Service is {serviceState.ToString().ToLowerInvariant()}", "Cannot reach the service");
            _startStopItem.Enabled = false;
        }
        else if (_status.Running)
        {
            var addresses = _status.LocalAddresses.Length > 0
                ? string.Join(", ", _status.LocalAddresses.Select(a => $"{a}:{_status.Port}"))
                : $"port {_status.Port}";

            var sessions = _status.Sessions.Length;
            var suffix = sessions == 1 ? "1 connection" : $"{sessions} connections";

            var passiveTrouble = _status.PassiveChecked > 0 && _status.PassiveAvailable == 0;
            Apply(
                passiveTrouble ? TrayState.Warning : TrayState.Running,
                passiveTrouble ? "Running — passive port range unusable" : $"Running — {suffix}",
                addresses);

            _startStopItem.Text = "Stop Server";
            _startStopItem.Enabled = true;
        }
        else
        {
            var reason = string.IsNullOrEmpty(_status.LastError)
                ? "Server is stopped"
                : _status.LastError;

            Apply(_status.Retrying ? TrayState.Warning : TrayState.Stopped,
                _status.Retrying ? "Retrying…" : "Server is stopped",
                reason);

            _startStopItem.Text = "Start Server";
            _startStopItem.Enabled = true;
        }

        RebuildOpenFolderMenu();
    }

    private void Apply(TrayState state, string status, string detail)
    {
        _icon.Icon = TrayIcons.For(state);
        _statusItem.Text = status;
        _addressItem.Text = detail;
        _addressItem.Visible = !string.IsNullOrWhiteSpace(detail);

        // NotifyIcon.Text is capped at 63 characters; longer values throw.
        var tooltip = $"Basic FTP Server — {status}";
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private void RebuildOpenFolderMenu()
    {
        _openFolderItem.DropDownItems.Clear();

        var response = ControlPipeClient
            .SendAsync(ControlCommands.GetConfig)
            .GetAwaiter()
            .GetResult();

        var transfer = response.Ok ? ControlJson.Deserialize<ConfigTransfer>(response.Payload) : null;
        var users = transfer?.Config.Users ?? [];

        if (users.Count == 0)
        {
            _openFolderItem.DropDownItems.Add(new ToolStripMenuItem("No accounts configured") { Enabled = false });
            return;
        }

        foreach (var user in users)
        {
            var path = user.HomeDirectory;
            _openFolderItem.DropDownItems.Add(new ToolStripMenuItem($"{user.Name} — {path}", null, (_, _) => OpenFolder(path)));
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open '{path}':\n{ex.Message}", "Basic FTP Server Service",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ToggleServerAsync()
    {
        var command = _status?.Running == true ? ControlCommands.StopServer : ControlCommands.StartServer;
        var response = await ControlPipeClient.SendAsync(command).ConfigureAwait(true);

        if (!response.Ok)
        {
            MessageBox.Show(response.Error, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RestartServiceAsync()
    {
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            if (!ServiceControl.Restart())
            {
                MessageBox.Show("Could not restart the Windows service. Check services.msc.",
                    "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            // Restore first: Activate alone does nothing useful on a minimized window.
            if (_settings.WindowState == FormWindowState.Minimized)
            {
                _settings.WindowState = FormWindowState.Normal;
            }

            _settings.Activate();
            _settings.BringToFront();
            return;
        }

        _settings = new SettingsForm();
        _settings.FormClosed += (_, _) => { _settings = null; _ = RefreshAsync(); };
        _settings.Show();
        _settings.Activate();
    }

    private void ShowLog()
    {
        if (_log is { IsDisposed: false })
        {
            _log.Activate();
            return;
        }

        _log = new LogForm();
        _log.FormClosed += (_, _) => _log = null;
        _log.Show();
        _log.Activate();
    }

    /// <summary>
    /// Re-adds the icon once a taskbar turns up. Gives up after two minutes: a session with
    /// no shell by then — a kiosk, a stripped-down server — is never going to grow one,
    /// and a timer ticking for the life of the process would be the worse outcome.
    /// </summary>
    private void StartIconWatchdog()
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);

        _iconWatchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        _iconWatchdog.Tick += (_, _) =>
        {
            if (!TaskbarIconRecovery.TaskbarExists())
            {
                if (DateTime.UtcNow > deadline)
                {
                    StopIconWatchdog();
                }

                return;
            }

            // Off and on again to force the add. Nothing flickers: the whole reason this
            // timer is running is that the icon is not on the taskbar.
            _icon.Visible = false;
            _icon.Visible = true;
            StopIconWatchdog();
        };

        _iconWatchdog.Start();
    }

    private void StopIconWatchdog()
    {
        _iconWatchdog?.Stop();
        _iconWatchdog?.Dispose();
        _iconWatchdog = null;
    }

    /// <summary>
    /// Closes only the UI. The service keeps serving — the whole point of this design is
    /// that scanning does not depend on anyone being logged in.
    /// </summary>
    private void ExitTray()
    {
        StopIconWatchdog();
        _poll.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unregister before disposing the handle it is waiting on.
            _showUiRegistration.Unregister(null);
            _showUiRequested.Dispose();
            _uiThreadMarshaller.Dispose();
            StopIconWatchdog();
            _poll.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
