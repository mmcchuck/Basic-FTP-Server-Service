namespace BasicFtpServer.Core.Files;

public sealed record ResolvedPath(string VirtualPath, string PhysicalPath);

/// <summary>
/// Maps the client's virtual path space onto a physical directory, and guarantees the
/// session can never escape its home directory.
///
/// Handles the two shapes copiers actually send: an absolute path from the device's "path"
/// field, and a bare filename when that field is left blank (which is what Kyocera's own
/// documentation instructs).
/// </summary>
public sealed class VirtualPathResolver
{
    private readonly string _rootPhysical;
    private readonly bool _sanitize;

    public VirtualPathResolver(string rootPhysical, bool sanitize)
    {
        // TrimEnd so the containment check below can rely on an exact prefix.
        _rootPhysical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPhysical));
        _sanitize = sanitize;
    }

    public string RootPhysical => _rootPhysical;

    /// <summary>Current working directory in virtual space, always starting with '/'.</summary>
    public string CurrentDirectory { get; set; } = "/";

    public bool TryResolve(string input, out ResolvedPath resolved, out string error)
    {
        resolved = null!;
        error = "";

        var segments = BuildSegments(input);
        if (segments is null)
        {
            error = "Invalid path.";
            return false;
        }

        var virtualPath = "/" + string.Join('/', segments);
        var physical = segments.Count == 0
            ? _rootPhysical
            : Path.GetFullPath(Path.Combine(_rootPhysical, Path.Combine([.. segments])));

        // Belt and braces: the ".." clamping below should make escape impossible, but a
        // containment check is cheap and this is the boundary that matters most.
        if (!IsInsideRoot(physical))
        {
            error = "Path is outside the home directory.";
            return false;
        }

        resolved = new ResolvedPath(virtualPath, physical);
        return true;
    }

    /// <summary>Resolves relative to the current directory and returns segments, or null if unusable.</summary>
    private List<string>? BuildSegments(string input)
    {
        input ??= "";

        // Some clients send Windows separators even though the protocol specifies '/'.
        input = input.Replace('\\', '/').Trim();

        // A few devices wrap the path in quotes.
        if (input.Length >= 2 && input.StartsWith('"') && input.EndsWith('"'))
        {
            input = input[1..^1];
        }

        var baseSegments = input.StartsWith('/')
            ? []
            : Split(CurrentDirectory);

        var segments = new List<string>(baseSegments);

        foreach (var raw in input.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                // Clamp at the root rather than erroring — clients routinely CDUP from '/'.
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            var segment = _sanitize ? FilenameSanitizer.SanitizeSegment(raw) : raw;
            if (segment.Length == 0)
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments;
    }

    private static List<string> Split(string virtualPath) =>
        [.. virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries)];

    private bool IsInsideRoot(string physical)
    {
        if (string.Equals(physical, _rootPhysical, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = _rootPhysical + Path.DirectorySeparatorChar;
        return physical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Virtual path of the parent of the current directory.</summary>
    public string ParentOfCurrent()
    {
        var segments = Split(CurrentDirectory);
        if (segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return "/" + string.Join('/', segments);
    }
}
