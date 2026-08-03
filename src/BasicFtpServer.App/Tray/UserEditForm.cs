using System.Security.Cryptography;
using BasicFtpServer.Core.Config;

namespace BasicFtpServer.App.Tray;

/// <summary>Add or edit one virtual account.</summary>
public sealed class UserEditForm : Form
{
    private readonly TextBox _name = new() { Width = 240 };
    private readonly TextBox _password = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly CheckBox _showPassword = new() { Text = "Show", AutoSize = true };
    private readonly TextBox _home = new() { Width = 320 };
    private readonly CheckBox _enabled = new() { Text = "Account is enabled", AutoSize = true, Checked = true };

    private readonly CheckBox _write = new() { Text = "Upload files (required for scanning)", AutoSize = true };
    private readonly CheckBox _list = new() { Text = "List the folder", AutoSize = true };
    private readonly CheckBox _createDirectory = new() { Text = "Create folders", AutoSize = true };
    private readonly CheckBox _read = new() { Text = "Download files", AutoSize = true };
    private readonly CheckBox _delete = new() { Text = "Delete files and folders", AutoSize = true };

    public FtpUser User { get; }
    public string Password => _password.Text;

    public UserEditForm(FtpUser user, string password)
    {
        User = user;

        Text = string.IsNullOrEmpty(user.Name) ? "Add Account" : $"Edit Account — {user.Name}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 430);

        _name.Text = user.Name;
        _password.Text = password;
        _home.Text = user.HomeDirectory;
        _enabled.Checked = user.Enabled;
        _write.Checked = user.Permissions.Write;
        _list.Checked = user.Permissions.List;
        _createDirectory.Checked = user.Permissions.CreateDirectory;
        _read.Checked = user.Permissions.Read;
        _delete.Checked = user.Permissions.Delete;

        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;

        var generate = new Button { Text = "Generate", AutoSize = true };
        generate.Click += (_, _) =>
        {
            _password.Text = GeneratePassword();
            _showPassword.Checked = true;
        };

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseForFolder();

        var passwordRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        passwordRow.Controls.AddRange([_password, _showPassword, generate]);

        var homeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        homeRow.Controls.AddRange([_home, browse]);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(grid, "Account name", _name);
        AddRow(grid, "Password", passwordRow);
        AddRow(grid, "Scan folder", homeRow);
        AddRow(grid, "", _enabled);

        var permissions = new GroupBox
        {
            Text = "Permissions",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(12, 6, 12, 6),
        };

        var permissionList = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        permissionList.Controls.AddRange([_write, _list, _createDirectory, _read, _delete]);
        permissions.Controls.Add(permissionList);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            ForeColor = Color.DimGray,
            Padding = new Padding(12, 4, 12, 4),
            Text = "A copier only needs upload rights. Leaving download and delete off means a device with " +
                   "leaked credentials cannot read back or destroy what has already been scanned.",
        };

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Commit();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(10),
        };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(hint);
        Controls.Add(permissions);
        Controls.Add(grid);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder scans should be saved to",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_home.Text) ? _home.Text : @"C:\",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _home.Text = dialog.SelectedPath;
        }
    }

    private void Commit()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Warn("Enter an account name.");
            return;
        }

        // The account name becomes an FTP username; spaces and colons cause trouble on
        // devices that parse the login field loosely.
        if (name.Any(c => char.IsWhiteSpace(c) || c == ':'))
        {
            Warn("The account name cannot contain spaces or colons.");
            return;
        }

        var home = _home.Text.Trim();
        if (home.Length == 0)
        {
            Warn("Choose a scan folder.");
            return;
        }

        if (!Path.IsPathFullyQualified(home))
        {
            Warn("The scan folder must be a full path, for example C:\\Scans\\Copier1.");
            return;
        }

        if (home.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var proceed = MessageBox.Show(
                "That is a network path. The service runs as LocalSystem by default, which authenticates " +
                "to other machines as the computer account and will usually be denied.\n\n" +
                "To use a network folder, set the service to log on as a user with rights to that share " +
                "(services.msc → Basic FTP Server Service → Log On).\n\nUse this path anyway?",
                "Network scan folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (proceed != DialogResult.Yes)
            {
                return;
            }
        }

        if (_password.Text.Length == 0)
        {
            var proceed = MessageBox.Show(
                "This account has no password, so anyone who can reach the server may upload to it.\n\n" +
                "Leave it blank?", "No password set", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (proceed != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(home);
        }
        catch (Exception ex)
        {
            Warn($"Could not create '{home}':\n{ex.Message}");
            return;
        }

        User.Name = name;
        User.HomeDirectory = home;
        User.Enabled = _enabled.Checked;
        User.Permissions.Write = _write.Checked;
        User.Permissions.List = _list.Checked;
        User.Permissions.CreateDirectory = _createDirectory.Checked;
        User.Permissions.Read = _read.Checked;
        User.Permissions.Delete = _delete.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>
    /// Avoids characters that copier keypads and web forms mangle, so the password can
    /// actually be typed into the device.
    /// </summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, 14);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 4) }, 0, grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.RowCount++;
    }
}
