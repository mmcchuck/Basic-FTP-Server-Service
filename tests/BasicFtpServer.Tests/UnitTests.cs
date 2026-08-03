using System.Net;
using BasicFtpServer.Core.Files;
using BasicFtpServer.Core.Net;
using BasicFtpServer.Core.Protocol;
using Xunit;

namespace BasicFtpServer.Tests;

public class VirtualPathResolverTests
{
    private static VirtualPathResolver Create(bool sanitize = true) =>
        new(Path.Combine(Path.GetTempPath(), "bftps-root"), sanitize);

    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/scans", "/scans")]
    [InlineData("scans", "/scans")]
    [InlineData("/scans/", "/scans")]
    [InlineData("/scans/./2026", "/scans/2026")]
    [InlineData("\\scans\\2026", "/scans/2026")]
    public void ResolvesToExpectedVirtualPath(string input, string expected)
    {
        var resolver = Create();
        Assert.True(resolver.TryResolve(input, out var resolved, out _));
        Assert.Equal(expected, resolved.VirtualPath);
    }

    [Theory]
    [InlineData("../../../../Windows/System32")]
    [InlineData("/../../secrets.txt")]
    [InlineData("..")]
    [InlineData("/scans/../../../etc")]
    public void CannotEscapeTheHomeDirectory(string input)
    {
        var resolver = Create();
        Assert.True(resolver.TryResolve(input, out var resolved, out _));

        // '..' is clamped at the root rather than rejected, because clients routinely CDUP
        // from '/'. What matters is that the physical path stays inside the home directory.
        Assert.StartsWith(resolver.RootPhysical, resolved.PhysicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvesRelativeToTheCurrentDirectory()
    {
        var resolver = Create();
        resolver.CurrentDirectory = "/scans/2026";

        // A bare filename is what a Kyocera sends when its path field is left blank.
        Assert.True(resolver.TryResolve("scan001.pdf", out var resolved, out _));
        Assert.Equal("/scans/2026/scan001.pdf", resolved.VirtualPath);
    }

    [Fact]
    public void SanitizesIllegalCharactersInPathSegments()
    {
        var resolver = Create(sanitize: true);
        Assert.True(resolver.TryResolve("scan:12?.pdf", out var resolved, out _));
        Assert.Equal("/scan_12_.pdf", resolved.VirtualPath);
    }

    [Fact]
    public void StripsSurroundingQuotes()
    {
        var resolver = Create();
        Assert.True(resolver.TryResolve("\"/scans\"", out var resolved, out _));
        Assert.Equal("/scans", resolved.VirtualPath);
    }
}

public class FilenameSanitizerTests
{
    [Theory]
    [InlineData("scan.pdf", "scan.pdf")]
    [InlineData("scan:2026.pdf", "scan_2026.pdf")]
    [InlineData("a?b*c|d.pdf", "a_b_c_d.pdf")]
    [InlineData("trailing.", "trailing")]
    [InlineData("trailing ", "trailing")]
    public void SanitizesAsExpected(string input, string expected) =>
        Assert.Equal(expected, FilenameSanitizer.SanitizeSegment(input));

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1.pdf")]
    public void EscapesWindowsReservedDeviceNames(string input)
    {
        var result = FilenameSanitizer.SanitizeSegment(input);
        Assert.StartsWith("_", result);
    }

    [Fact]
    public void NeverReturnsAnEmptyName() =>
        Assert.False(string.IsNullOrEmpty(FilenameSanitizer.SanitizeSegment("...")));
}

public class ListingFormatterTests
{
    [Fact]
    public void UnixEntryUsesRecentClockFormat()
    {
        var now = new DateTime(2026, 8, 3, 12, 0, 0);
        var line = ListingFormatter.FormatUnixEntry("scan.pdf", 1234, now.AddDays(-2), isDirectory: false, now);

        Assert.StartsWith("-rw-r--r--", line);
        Assert.EndsWith(" scan.pdf", line);
        Assert.Contains("Aug", line);
        Assert.Contains("1234", line);
        Assert.Contains("12:00", line);
    }

    [Fact]
    public void UnixEntryShowsYearForOlderFiles()
    {
        var now = new DateTime(2026, 8, 3, 12, 0, 0);
        var line = ListingFormatter.FormatUnixEntry("old.pdf", 10, now.AddDays(-300), isDirectory: false, now);

        Assert.Contains("2025", line);
        Assert.DoesNotContain("12:00", line);
    }

    [Fact]
    public void DirectoriesAreMarkedWithALeadingD()
    {
        var now = new DateTime(2026, 8, 3, 12, 0, 0);
        var line = ListingFormatter.FormatUnixEntry("folder", 0, now, isDirectory: true, now);
        Assert.StartsWith("d", line);
    }
}

public class IpAccessListTests
{
    [Fact]
    public void EmptyListAllowsEverything()
    {
        var list = new IpAccessList([]);
        Assert.True(list.IsEmpty);
        Assert.True(list.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.168.1.255", true)]
    [InlineData("192.168.2.1", false)]
    public void MatchesCidrRanges(string candidate, bool expected)
    {
        var list = new IpAccessList(["192.168.1.0/24"]);
        Assert.Equal(expected, list.IsAllowed(IPAddress.Parse(candidate)));
    }

    [Fact]
    public void MatchesSingleAddresses()
    {
        var list = new IpAccessList(["10.0.0.5"]);
        Assert.True(list.IsAllowed(IPAddress.Parse("10.0.0.5")));
        Assert.False(list.IsAllowed(IPAddress.Parse("10.0.0.6")));
    }

    [Fact]
    public void UnwrapsIPv4MappedAddressesFromDualStackListeners()
    {
        var list = new IpAccessList(["192.168.1.0/24"]);
        Assert.True(list.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10")));
    }
}

public class UploadWriterNamingTests
{
    [Fact]
    public void GeneratesNumberedSuffixForCollisions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bftps-naming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "scan.pdf");
            File.WriteAllText(original, "x");

            Assert.Equal(Path.Combine(directory, "scan (1).pdf"), UploadWriter.NextAvailableName(original));

            File.WriteAllText(Path.Combine(directory, "scan (1).pdf"), "x");
            Assert.Equal(Path.Combine(directory, "scan (2).pdf"), UploadWriter.NextAvailableName(original));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
