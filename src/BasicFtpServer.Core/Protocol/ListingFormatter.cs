using System.Globalization;
using System.Text;

namespace BasicFtpServer.Core.Protocol;

/// <summary>
/// Renders directory listings.
///
/// Unix `ls -l` is the default and should stay that way: most embedded FTP clients only
/// ever learned to parse that one shape, which is also why the server answers SYST with
/// "UNIX Type: L8". Month names and the column layout are fixed and culture-invariant,
/// because clients pattern-match on them.
/// </summary>
public static class ListingFormatter
{
    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static string Format(DirectoryInfo directory, bool dos, DateTime now)
    {
        var builder = new StringBuilder();

        foreach (var dir in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(dos
                ? FormatDosEntry(dir.Name, 0, dir.LastWriteTime, isDirectory: true)
                : FormatUnixEntry(dir.Name, 0, dir.LastWriteTime, isDirectory: true, now));
            builder.Append("\r\n");
        }

        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Staging files are an implementation detail; showing them would let a client
            // discover, and try to fetch, a transfer that is still in flight.
            if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(dos
                ? FormatDosEntry(file.Name, file.Length, file.LastWriteTime, isDirectory: false)
                : FormatUnixEntry(file.Name, file.Length, file.LastWriteTime, isDirectory: false, now));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    public static string FormatNames(DirectoryInfo directory)
    {
        var builder = new StringBuilder();

        foreach (var name in directory.EnumerateFileSystemInfos()
                     .Where(e => !e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                     .Select(e => e.Name)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append("\r\n");
        }

        return builder.ToString();
    }

    public static string FormatUnixEntry(string name, long size, DateTime modified, bool isDirectory, DateTime now)
    {
        var permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        var links = isDirectory ? 2 : 1;

        // `ls` shows the year instead of the clock for anything older than ~6 months, and
        // clients use that distinction when parsing the date column.
        var age = now - modified;
        var stamp = age.TotalDays is > 180 or < -1
            ? modified.Year.ToString("D4", CultureInfo.InvariantCulture).PadLeft(5)
            : modified.ToString("HH:mm", CultureInfo.InvariantCulture).PadLeft(5);

        var month = Months[modified.Month - 1];
        var day = modified.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);

        return string.Create(CultureInfo.InvariantCulture,
            $"{permissions} {links,3} ftp      ftp      {size,13} {month} {day} {stamp} {name}");
    }

    public static string FormatDosEntry(string name, long size, DateTime modified, bool isDirectory)
    {
        var date = modified.ToString("MM-dd-yy  hh:mmtt", CultureInfo.InvariantCulture).ToUpperInvariant();
        var sizeColumn = isDirectory
            ? "       <DIR>         "
            : size.ToString(CultureInfo.InvariantCulture).PadLeft(20) + " ";

        return $"{date}{sizeColumn}{name}";
    }
}
