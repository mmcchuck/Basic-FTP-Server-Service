using System.Drawing.Drawing2D;

namespace BasicFtpServer.App.Tray;

public enum TrayState
{
    Running,
    Warning,
    Stopped,
}

/// <summary>
/// Tray icons drawn at runtime rather than shipped as .ico files, so the repository stays
/// free of binary assets and the icon scales cleanly on high-DPI displays.
/// </summary>
public static class TrayIcons
{
    private static readonly Dictionary<TrayState, Icon> Cache = [];

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var colour = state switch
        {
            TrayState.Running => Color.FromArgb(46, 160, 67),
            TrayState.Warning => Color.FromArgb(219, 154, 4),
            _ => Color.FromArgb(200, 62, 62),
        };

        var icon = Draw(colour);
        Cache[state] = icon;
        return icon;
    }

    private static Icon Draw(Color colour)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var fill = new SolidBrush(colour);
            graphics.FillEllipse(fill, 2, 2, 27, 27);

            using var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 2f);
            graphics.DrawEllipse(edge, 2, 2, 27, 27);

            // A small "F" so the icon is identifiable among other status indicators.
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("F", font, text, new RectangleF(2, 2, 28, 29), format);
        }

        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);

        // Clone so the icon survives destroying the GDI handle we just created.
        return (Icon)temporary.Clone();
    }
}
