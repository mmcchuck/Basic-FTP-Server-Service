using System.Diagnostics;
using BasicFtpServer.App.Ipc;
using BasicFtpServer.App.Setup;
using BasicFtpServer.Core.Config;

namespace BasicFtpServer.App.Tray;

/// <summary>
/// The whole settings surface. Every copier-compatibility toggle is exposed here with a
/// plain-English hint, because the point of those switches is that a technician standing at
/// a misbehaving copier can find and flip the right one without reading source code.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _listenAddress = new();
    private readonly NumericUpDown _port = Spin(1, 65535);
    private readonly NumericUpDown _passiveMin = Spin(1024, 65535);
    private readonly NumericUpDown _passiveMax = Spin(1024, 65535);
    private readonly TextBox _forcedPassiveIp = new();
    private readonly CheckBox _ignorePortAddress = new() { Text = "Ignore the address a device sends in PORT/EPRT", AutoSize = true };
    private readonly NumericUpDown _maxConnections = Spin(1, 500);
    private readonly NumericUpDown _idleTimeout = Spin(0, 86400);
    private readonly TextBox _allowedIps = new() { Multiline = true, Height = 60, ScrollBars = ScrollBars.Vertical };
    private readonly Label _passiveProbe = new() { AutoSize = true, ForeColor = Color.DimGray };

    private readonly CheckBox _autoCreate = new() { Text = "Create missing directories automatically", AutoSize = true };
    private readonly ComboBox _listingFormat = Choice("unix", "dos");
    private readonly ComboBox _fallbackEncoding = Choice("windows-1252", "utf-8", "iso-8859-1");
    private readonly CheckBox _minimalFeat = new() { Text = "Send a minimal FEAT reply", AutoSize = true };
    private readonly CheckBox _enableEpsv = new() { Text = "Enable EPSV (extended passive)", AutoSize = true };
    private readonly CheckBox _enableEprt = new() { Text = "Enable EPRT (extended active)", AutoSize = true };
    private readonly CheckBox _sanitize = new() { Text = "Replace characters that are illegal in Windows filenames", AutoSize = true };
    private readonly CheckBox _partFile = new() { Text = "Upload to a .part file and rename when complete", AutoSize = true };
    private readonly ComboBox _onDuplicate = Choice("rename", "overwrite", "reject");
    private readonly TextBox _greeting = new();

    private readonly ListView _users = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        Dock = DockStyle.Fill,
    };

    private readonly ComboBox _logLevel = Choice("Verbose", "Debug", "Information", "Warning", "Error");
    private readonly NumericUpDown _retainDays = Spin(1, 365);
    private readonly CheckBox _logCommands = new() { Text = "Log every FTP command and reply", AutoSize = true };

    private ServerConfig _config = new();
    private Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    public SettingsForm()
    {
        Text = "Basic FTP Server Service — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 620);
        Size = new Size(820, 680);
        Icon = TrayIcons.For(TrayState.Running);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildServerTab());
        tabs.TabPages.Add(BuildCompatibilityTab());
        tabs.TabPages.Add(BuildUsersTab());
        tabs.TabPages.Add(BuildLoggingTab());

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var apply = new Button { Text = "Apply", AutoSize = true };

        ok.Click += (_, _) => { if (Save()) { Close(); } };
        apply.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(10),
        };
        buttons.Controls.AddRange([cancel, apply, ok]);

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        Load += (_, _) => LoadConfig();
    }

    // ---- Tabs ------------------------------------------------------------------------

    private TabPage BuildServerTab()
    {
        var grid = NewGrid();

        Row(grid, "Listen address", _listenAddress, "0.0.0.0 listens on every network adapter.");
        Row(grid, "Control port", _port, "21 is the standard FTP port and what copiers default to.");
        Row(grid, "Passive port range", PassiveRangePanel(), "Avoid 49152-51000 — Windows reserves blocks in there for Hyper-V and WSL.");
        Row(grid, "", _passiveProbe, "");
        Row(grid, "Advertise this IP", _forcedPassiveIp, "Leave blank unless passive transfers fail. Set it when Hyper-V, VPN, or Docker adapters make the server advertise an unreachable address.");
        Row(grid, "", _ignorePortAddress, "Recommended. Many copiers send an address they cannot actually be reached on.");
        Row(grid, "Max connections", _maxConnections, "");
        Row(grid, "Idle timeout (seconds)", _idleTimeout, "0 disables the timeout.");
        Row(grid, "Allowed client IPs", _allowedIps, "One per line, plain IP or CIDR (192.168.1.0/24). Blank allows any client. Worth setting — FTP passwords cross the network in the clear.");

        var firewall = new Button { Text = "Update Windows Firewall Rules", AutoSize = true };
        firewall.Click += (_, _) => UpdateFirewall();
        Row(grid, "", firewall, "Re-creates the inbound rules for the control port and passive range. Run this after changing either.");

        return new TabPage("Server") { Controls = { Wrap(grid) } };
    }

    private Control PassiveRangePanel()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        panel.Controls.Add(_passiveMin);
        panel.Controls.Add(new Label { Text = "to", AutoSize = true, Margin = new Padding(6, 6, 6, 0) });
        panel.Controls.Add(_passiveMax);
        return panel;
    }

    private TabPage BuildCompatibilityTab()
    {
        var grid = NewGrid();

        Row(grid, "", _autoCreate, "Several copiers upload into a dated folder they never create. Leave this on.");
        Row(grid, "Directory listing style", _listingFormat, "Unix. Most embedded FTP clients cannot parse DOS-style listings.");
        Row(grid, "Fallback text encoding", _fallbackEncoding, "Used for filenames that are not valid UTF-8, which older devices still send.");
        Row(grid, "", _minimalFeat, "Try this if a device disconnects immediately after connecting — some old firmware chokes on a long feature list.");
        Row(grid, "", _enableEpsv, "Turn off if a device sends EPSV and then fails to transfer.");
        Row(grid, "", _enableEprt, "");
        Row(grid, "", _sanitize, "Scanner job names often contain ':' or '?', which Windows cannot store.");
        Row(grid, "", _partFile, "Stops anything watching the folder from picking up a half-written scan.");
        Row(grid, "If the file already exists", _onDuplicate, "Rename keeps both copies as 'scan (1).pdf'.");
        Row(grid, "Login banner", _greeting, "Shown to the client on connect.");

        return new TabPage("Copier Compatibility") { Controls = { Wrap(grid) } };
    }

    private TabPage BuildUsersTab()
    {
        _users.Columns.Add("Account", 130);
        _users.Columns.Add("Scan folder", 330);
        _users.Columns.Add("Permissions", 150);
        _users.Columns.Add("Enabled", 70);
        _users.DoubleClick += (_, _) => EditSelectedUser();

        var add = new Button { Text = "Add…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };

        add.Click += (_, _) => AddUser();
        edit.Click += (_, _) => EditSelectedUser();
        remove.Click += (_, _) => RemoveSelectedUser();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([add, edit, remove]);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 46,
            ForeColor = Color.DimGray,
            Text = "These are accounts for this server only — they are not Windows user accounts. " +
                   "FTP sends the password in the clear and copiers store it readably in their own web page, " +
                   "so a device should never be given a real Windows credential.",
        };

        var page = new TabPage("Users") { Padding = new Padding(10) };
        page.Controls.Add(_users);
        page.Controls.Add(buttons);
        page.Controls.Add(hint);
        return page;
    }

    private TabPage BuildLoggingTab()
    {
        var grid = NewGrid();

        Row(grid, "Log level", _logLevel, "");
        Row(grid, "Keep log files for (days)", _retainDays, "");
        Row(grid, "", _logCommands, "The single most useful setting when a copier will not scan — it records exactly what the device said.");

        var open = new Button { Text = "Open Log Folder", AutoSize = true };
        open.Click += (_, _) => OpenLogFolder();
        Row(grid, "", open, new ConfigStore().LogDirectory);

        return new TabPage("Logging") { Controls = { Wrap(grid) } };
    }

    // ---- Load / save -----------------------------------------------------------------

    private void LoadConfig()
    {
        var response = ControlPipeClient.SendAsync(ControlCommands.GetConfig).GetAwaiter().GetResult();
        if (!response.Ok)
        {
            MessageBox.Show(response.Error, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var transfer = ControlJson.Deserialize<ConfigTransfer>(response.Payload);
        if (transfer is null)
        {
            return;
        }

        _config = transfer.Config;
        _passwords = new Dictionary<string, string>(transfer.Passwords, StringComparer.OrdinalIgnoreCase);

        var server = _config.Server;
        _listenAddress.Text = server.ListenAddress;
        _port.Value = Clamp(_port, server.Port);
        _passiveMin.Value = Clamp(_passiveMin, server.PassivePortMin);
        _passiveMax.Value = Clamp(_passiveMax, server.PassivePortMax);
        _forcedPassiveIp.Text = server.ForcedPassiveIp ?? "";
        _ignorePortAddress.Checked = server.IgnorePortCommandAddress;
        _maxConnections.Value = Clamp(_maxConnections, server.MaxConnections);
        _idleTimeout.Value = Clamp(_idleTimeout, server.IdleTimeoutSeconds);
        _allowedIps.Text = string.Join(Environment.NewLine, server.AllowedClientIps);

        var compat = _config.Compatibility;
        _autoCreate.Checked = compat.AutoCreateDirectories;
        _listingFormat.SelectedItem = compat.ListingFormat?.ToLowerInvariant() == "dos" ? "dos" : "unix";
        _fallbackEncoding.Text = compat.FallbackEncoding;
        _minimalFeat.Checked = compat.MinimalFeat;
        _enableEpsv.Checked = compat.EnableEpsv;
        _enableEprt.Checked = compat.EnableEprt;
        _sanitize.Checked = compat.SanitizeFilenames;
        _partFile.Checked = compat.WriteToPartFile;
        _onDuplicate.SelectedItem = compat.OnDuplicate?.ToLowerInvariant() switch
        {
            "overwrite" => "overwrite",
            "reject" => "reject",
            _ => "rename",
        };
        _greeting.Text = compat.Greeting;

        _logLevel.SelectedItem = _config.Logging.Level;
        _retainDays.Value = Clamp(_retainDays, _config.Logging.RetainDays);
        _logCommands.Checked = _config.Logging.LogProtocolCommands;

        RefreshUserList();
        ShowPassiveProbe();
    }

    private void ShowPassiveProbe()
    {
        var status = ControlPipeClient.GetStatusAsync().GetAwaiter().GetResult();
        if (status is null || status.PassiveChecked == 0)
        {
            _passiveProbe.Text = "";
            return;
        }

        if (status.PassiveAvailable == 0)
        {
            _passiveProbe.ForeColor = Color.Firebrick;
            _passiveProbe.Text = "None of the passive ports in this range can be bound — passive transfers will fail. " +
                                 "Check: netsh int ipv4 show excludedportrange protocol=tcp";
        }
        else
        {
            _passiveProbe.ForeColor = Color.DimGray;
            _passiveProbe.Text = $"{status.PassiveAvailable} of the first {status.PassiveChecked} ports in this range are bindable.";
        }
    }

    private bool Save()
    {
        if (_passiveMax.Value < _passiveMin.Value)
        {
            MessageBox.Show("The passive port range ends before it starts.", "Basic FTP Server Service",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_forcedPassiveIp.Text) &&
            !System.Net.IPAddress.TryParse(_forcedPassiveIp.Text.Trim(), out _))
        {
            MessageBox.Show("'Advertise this IP' is not a valid IP address.", "Basic FTP Server Service",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var server = _config.Server;
        server.ListenAddress = string.IsNullOrWhiteSpace(_listenAddress.Text) ? "0.0.0.0" : _listenAddress.Text.Trim();
        server.Port = (int)_port.Value;
        server.PassivePortMin = (int)_passiveMin.Value;
        server.PassivePortMax = (int)_passiveMax.Value;
        server.ForcedPassiveIp = string.IsNullOrWhiteSpace(_forcedPassiveIp.Text) ? null : _forcedPassiveIp.Text.Trim();
        server.IgnorePortCommandAddress = _ignorePortAddress.Checked;
        server.MaxConnections = (int)_maxConnections.Value;
        server.IdleTimeoutSeconds = (int)_idleTimeout.Value;
        server.AllowedClientIps =
        [
            .. _allowedIps.Lines.Select(l => l.Trim()).Where(l => l.Length > 0),
        ];

        var compat = _config.Compatibility;
        compat.AutoCreateDirectories = _autoCreate.Checked;
        compat.ListingFormat = (string)(_listingFormat.SelectedItem ?? "unix");
        compat.FallbackEncoding = _fallbackEncoding.Text.Trim();
        compat.MinimalFeat = _minimalFeat.Checked;
        compat.EnableEpsv = _enableEpsv.Checked;
        compat.EnableEprt = _enableEprt.Checked;
        compat.SanitizeFilenames = _sanitize.Checked;
        compat.WriteToPartFile = _partFile.Checked;
        compat.OnDuplicate = (string)(_onDuplicate.SelectedItem ?? "rename");
        compat.Greeting = _greeting.Text.Trim();

        _config.Logging.Level = (string)(_logLevel.SelectedItem ?? "Information");
        _config.Logging.RetainDays = (int)_retainDays.Value;
        _config.Logging.LogProtocolCommands = _logCommands.Checked;

        var payload = ControlJson.Serialize(new ConfigTransfer(_config, _passwords));
        var response = ControlPipeClient.SendAsync(ControlCommands.SetConfig, payload, timeoutMs: 20000)
            .GetAwaiter().GetResult();

        if (!response.Ok)
        {
            MessageBox.Show(response.Error, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        ShowPassiveProbe();
        return true;
    }

    private void UpdateFirewall()
    {
        var result = FirewallRules.Add(
            ServiceControl.ExecutablePath,
            (int)_port.Value,
            (int)_passiveMin.Value,
            (int)_passiveMax.Value);

        MessageBox.Show(
            result.Success
                ? $"Firewall rules updated for port {(int)_port.Value} and {(int)_passiveMin.Value}-{(int)_passiveMax.Value}."
                : $"Could not update the firewall rules:\n{result.Output}",
            "Basic FTP Server Service",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static void OpenLogFolder()
    {
        var directory = new ConfigStore().LogDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- Users -----------------------------------------------------------------------

    private void RefreshUserList()
    {
        _users.Items.Clear();

        foreach (var user in _config.Users)
        {
            var permissions = string.Join(", ", new[]
            {
                user.Permissions.Write ? "write" : null,
                user.Permissions.Read ? "read" : null,
                user.Permissions.Delete ? "delete" : null,
                user.Permissions.List ? "list" : null,
            }.Where(p => p is not null));

            var item = new ListViewItem(user.Name);
            item.SubItems.Add(user.HomeDirectory);
            item.SubItems.Add(permissions);
            item.SubItems.Add(user.Enabled ? "Yes" : "No");
            item.Tag = user;
            _users.Items.Add(item);
        }
    }

    private void AddUser()
    {
        var user = new FtpUser { Name = "", HomeDirectory = @"C:\Scans\" };
        using var dialog = new UserEditForm(user, "");

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (_config.Users.Any(u => string.Equals(u.Name, dialog.User.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"An account named '{dialog.User.Name}' already exists.", "Basic FTP Server Service",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.Users.Add(dialog.User);
        _passwords[dialog.User.Name] = dialog.Password;
        RefreshUserList();
    }

    private void EditSelectedUser()
    {
        if (_users.SelectedItems.Count == 0 || _users.SelectedItems[0].Tag is not FtpUser existing)
        {
            return;
        }

        var originalName = existing.Name;
        _passwords.TryGetValue(originalName, out var password);

        using var dialog = new UserEditForm(existing, password ?? "");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!string.Equals(originalName, dialog.User.Name, StringComparison.OrdinalIgnoreCase))
        {
            _passwords.Remove(originalName);
        }

        _passwords[dialog.User.Name] = dialog.Password;
        RefreshUserList();
    }

    private void RemoveSelectedUser()
    {
        if (_users.SelectedItems.Count == 0 || _users.SelectedItems[0].Tag is not FtpUser user)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove the account '{user.Name}'?\n\nThe scan folder and its contents are left untouched.",
            "Basic FTP Server Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _config.Users.Remove(user);
        _passwords.Remove(user.Name);
        RefreshUserList();
    }

    // ---- Layout helpers ---------------------------------------------------------------

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Padding = new Padding(12),
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void Row(TableLayoutPanel grid, string label, Control control, string hint)
    {
        control.Margin = new Padding(3, 4, 3, 4);
        if (control is TextBox { Multiline: false } or ComboBox)
        {
            control.Width = 220;
        }

        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 4) }, 0, grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.Controls.Add(new Label
        {
            Text = hint,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(10, 6, 3, 4),
        }, 2, grid.RowCount);

        grid.RowCount++;
    }

    private static Control Wrap(Control inner) =>
        new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { inner } };

    private static NumericUpDown Spin(int min, int max) =>
        new() { Minimum = min, Maximum = max, Width = 100, ThousandsSeparator = false };

    private static ComboBox Choice(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        combo.Items.AddRange([.. items]);
        return combo;
    }

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Clamp(value, control.Minimum, control.Maximum);
}
