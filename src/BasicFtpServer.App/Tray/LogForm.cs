using BasicFtpServer.App.Ipc;

namespace BasicFtpServer.App.Tray;

/// <summary>
/// Live session log.
///
/// This is the troubleshooting tool. When a copier refuses to scan, the answer is almost
/// always visible in the exchange between the device and the server — which reply it
/// disliked, whether the data connection ever opened, what filename it actually sent.
/// </summary>
public sealed class LogForm : Form
{
    private readonly RichTextBox _output = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(24, 24, 24),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9.5f),
        WordWrap = false,
        ScrollBars = RichTextBoxScrollBars.Both,
        DetectUrls = false,
    };

    private readonly CheckBox _follow = new() { Text = "Follow", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 8, 4) };
    private readonly CheckBox _paused = new() { Text = "Pause", AutoSize = true, Margin = new Padding(8, 6, 8, 4) };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(12, 7, 8, 4), ForeColor = Color.DimGray };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };

    private long _lastSequence;

    public LogForm()
    {
        Text = "Basic FTP Server Service — Live Log";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        Icon = TrayIcons.For(TrayState.Running);

        var clear = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(4) };
        var copy = new Button { Text = "Copy All", AutoSize = true, Margin = new Padding(4) };
        var save = new Button { Text = "Save…", AutoSize = true, Margin = new Padding(4) };

        clear.Click += (_, _) => ClearLog();
        copy.Click += (_, _) => CopyAll();
        save.Click += (_, _) => SaveAs();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(6, 4, 6, 4),
        };
        toolbar.Controls.AddRange([_follow, _paused, clear, copy, save, _summary]);

        Controls.Add(_output);
        Controls.Add(toolbar);

        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    private void Poll()
    {
        if (_paused.Checked)
        {
            return;
        }

        var response = ControlPipeClient
            .SendAsync(ControlCommands.GetLog, _lastSequence.ToString())
            .GetAwaiter()
            .GetResult();

        if (!response.Ok)
        {
            _summary.Text = response.Error ?? "Service unreachable";
            return;
        }

        var page = ControlJson.Deserialize<LogPageDto>(response.Payload);
        if (page is null)
        {
            return;
        }

        // The service restarting resets the sequence; without this the view would go silent.
        if (page.LastSequence < _lastSequence)
        {
            _output.Clear();
        }

        _lastSequence = page.LastSequence;

        foreach (var line in page.Lines)
        {
            Append(line);
        }

        var status = ControlPipeClient.GetStatusAsync().GetAwaiter().GetResult();
        _summary.Text = status is null
            ? "Service unreachable"
            : status.Running
                ? $"Listening on port {status.Port} — {status.Sessions.Length} active"
                : "Server stopped";
    }

    private void Append(LogLineDto line)
    {
        var colour = line.Kind switch
        {
            "Error" => Color.FromArgb(255, 120, 110),
            "Warning" => Color.FromArgb(240, 190, 90),
            "Command" => Color.FromArgb(130, 200, 255),
            "Reply" => Color.FromArgb(150, 220, 150),
            "Transfer" => Color.FromArgb(200, 170, 255),
            _ => Color.Gainsboro,
        };

        _output.SelectionStart = _output.TextLength;
        _output.SelectionLength = 0;
        _output.SelectionColor = colour;
        _output.AppendText(line.Text + Environment.NewLine);
        _output.SelectionColor = _output.ForeColor;

        if (_follow.Checked)
        {
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        }
    }

    private void ClearLog()
    {
        var response = ControlPipeClient.SendAsync(ControlCommands.ClearLog).GetAwaiter().GetResult();
        if (!response.Ok)
        {
            MessageBox.Show(response.Error, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _output.Clear();
        _lastSequence = 0;
    }

    private void CopyAll()
    {
        if (_output.TextLength > 0)
        {
            Clipboard.SetText(_output.Text);
        }
    }

    private void SaveAs()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"ftp-session-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _output.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Basic FTP Server Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        base.OnFormClosed(e);
    }
}
