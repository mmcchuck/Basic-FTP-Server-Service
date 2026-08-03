using System.Text;

namespace BasicFtpServer.Core.Files;

/// <summary>
/// Scanner job names routinely contain characters that are perfectly legal to send over FTP
/// and illegal on NTFS — colons from timestamps, question marks, asterisks. Without this the
/// upload fails with an opaque error at the last moment, after the copier has already sent
/// the whole scan.
/// </summary>
public static class FilenameSanitizer
{
    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>Names that are reserved devices on Windows regardless of extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Sanitizes a single path segment. Never returns an empty string.</summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return "_";
        }

        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            // Control characters would also be rejected by the filesystem.
            builder.Append(c < 32 || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        }

        // Windows silently strips trailing dots and spaces, which would make the name the
        // server reports back differ from the name on disk.
        var result = builder.ToString().TrimEnd(' ', '.');
        if (result.Length == 0)
        {
            return "_";
        }

        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    public static bool NeedsSanitizing(string segment) => SanitizeSegment(segment) != segment;
}
