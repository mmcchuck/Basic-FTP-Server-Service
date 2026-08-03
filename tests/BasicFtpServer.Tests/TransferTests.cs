using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using Xunit;

namespace BasicFtpServer.Tests;

/// <summary>End-to-end transfers driven by a real FTP client library.</summary>
public class TransferTests
{
    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    [Fact]
    public async Task PassiveUploadLandsIntactOnDisk()
    {
        await using var server = FtpTestServer.Start();
        var data = Payload(64 * 1024);

        await using var client = server.CreateClient(passive: true);
        await client.Connect();
        var status = await client.UploadBytes(data, "scan.pdf", FtpRemoteExists.NoCheck);

        Assert.Equal(FtpStatus.Success, status);
        Assert.Equal(data, await File.ReadAllBytesAsync(server.PathOf("scan.pdf")));
    }

    [Fact]
    public async Task ActiveUploadLandsIntactOnDisk()
    {
        // Active mode is not optional: plenty of copiers only ever speak PORT.
        await using var server = FtpTestServer.Start();
        var data = Payload(64 * 1024);

        await using var client = server.CreateClient(passive: false);
        await client.Connect();
        var status = await client.UploadBytes(data, "active.pdf", FtpRemoteExists.NoCheck);

        Assert.Equal(FtpStatus.Success, status);
        Assert.Equal(data, await File.ReadAllBytesAsync(server.PathOf("active.pdf")));
    }

    [Fact]
    public async Task LargeUploadIsByteExact()
    {
        await using var server = FtpTestServer.Start();
        var data = Payload(4 * 1024 * 1024);

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(data, "large.bin", FtpRemoteExists.NoCheck);

        var written = await File.ReadAllBytesAsync(server.PathOf("large.bin"));
        Assert.Equal(data.Length, written.Length);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(written));
    }

    [Fact]
    public async Task StagingFileIsRemovedAfterASuccessfulUpload()
    {
        await using var server = FtpTestServer.Start();

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Payload(4096), "scan.pdf", FtpRemoteExists.NoCheck);

        Assert.True(File.Exists(server.PathOf("scan.pdf")));
        Assert.Empty(Directory.GetFiles(server.Root, "*.part"));
    }

    [Fact]
    public async Task DuplicateNamesAreRenamedByDefault()
    {
        await using var server = FtpTestServer.Start();

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Encoding.UTF8.GetBytes("first"), "scan.pdf", FtpRemoteExists.NoCheck);
        await client.UploadBytes(Encoding.UTF8.GetBytes("second"), "scan.pdf", FtpRemoteExists.NoCheck);

        Assert.Equal("first", await File.ReadAllTextAsync(server.PathOf("scan.pdf")));
        Assert.Equal("second", await File.ReadAllTextAsync(server.PathOf("scan (1).pdf")));
    }

    [Fact]
    public async Task OverwritePolicyReplacesTheExistingFile()
    {
        await using var server = FtpTestServer.Start(c => c.Compatibility.OnDuplicate = "overwrite");

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Encoding.UTF8.GetBytes("first"), "scan.pdf", FtpRemoteExists.NoCheck);
        await client.UploadBytes(Encoding.UTF8.GetBytes("second"), "scan.pdf", FtpRemoteExists.NoCheck);

        Assert.Equal("second", await File.ReadAllTextAsync(server.PathOf("scan.pdf")));
        Assert.False(File.Exists(server.PathOf("scan (1).pdf")));
    }

    [Fact]
    public async Task UploadCreatesMissingDirectories()
    {
        await using var server = FtpTestServer.Start();

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Payload(1024), "/2026/august/scan.pdf", FtpRemoteExists.NoCheck);

        Assert.True(File.Exists(server.PathOf("2026", "august", "scan.pdf")));
    }

    [Fact]
    public async Task NonAsciiFilenamesSurviveTheRoundTrip()
    {
        await using var server = FtpTestServer.Start();
        const string name = "Rapport_Février_£20.pdf";

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Encoding.UTF8.GetBytes("content"), name, FtpRemoteExists.NoCheck);

        Assert.True(File.Exists(server.PathOf(name)));

        var listing = await client.GetListing();
        Assert.Contains(listing, item => item.Name == name);
    }

    [Fact]
    public async Task ListingIsParsedByAStandardClient()
    {
        await using var server = FtpTestServer.Start();
        await File.WriteAllTextAsync(server.PathOf("one.txt"), "12345");
        Directory.CreateDirectory(server.PathOf("subfolder"));

        await using var client = server.CreateClient();
        await client.Connect();
        var listing = await client.GetListing();

        var file = Assert.Single(listing, item => item.Name == "one.txt");
        Assert.Equal(FtpObjectType.File, file.Type);
        Assert.Equal(5, file.Size);

        var directory = Assert.Single(listing, item => item.Name == "subfolder");
        Assert.Equal(FtpObjectType.Directory, directory.Type);
    }

    [Fact]
    public async Task InFlightUploadsAreNotVisibleInListings()
    {
        // The .part staging file must stay hidden, otherwise a folder watcher (or a client
        // polling the directory) can pick up a scan that is still being written.
        await using var server = FtpTestServer.Start();
        await File.WriteAllTextAsync(server.PathOf("inflight.pdf.part"), "half a scan");

        await using var client = server.CreateClient();
        await client.Connect();
        var listing = await client.GetListing();

        Assert.DoesNotContain(listing, item => item.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadReturnsWhatWasUploaded()
    {
        await using var server = FtpTestServer.Start();
        var data = Payload(32 * 1024);

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(data, "round-trip.bin", FtpRemoteExists.NoCheck);

        var downloaded = await client.DownloadBytes("round-trip.bin", 0);
        Assert.Equal(data, downloaded);
    }

    [Fact]
    public async Task ConcurrentUploadsFromSeparateClientsAllSucceed()
    {
        await using var server = FtpTestServer.Start();
        var payloads = Enumerable.Range(0, 5).Select(_ => Payload(128 * 1024)).ToArray();

        await Task.WhenAll(payloads.Select(async (data, index) =>
        {
            await using var client = server.CreateClient();
            await client.Connect();
            var status = await client.UploadBytes(data, $"copier{index}.pdf", FtpRemoteExists.NoCheck);
            Assert.Equal(FtpStatus.Success, status);
        }));

        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(payloads[i], await File.ReadAllBytesAsync(server.PathOf($"copier{i}.pdf")));
        }
    }

    [Fact]
    public async Task RenameAndDeleteWork()
    {
        await using var server = FtpTestServer.Start();

        await using var client = server.CreateClient();
        await client.Connect();
        await client.UploadBytes(Encoding.UTF8.GetBytes("x"), "before.txt", FtpRemoteExists.NoCheck);

        await client.Rename("before.txt", "after.txt");
        Assert.True(File.Exists(server.PathOf("after.txt")));

        await client.DeleteFile("after.txt");
        Assert.False(File.Exists(server.PathOf("after.txt")));
    }

    [Fact]
    public async Task ConnectionsOutsideTheAllowListAreRefused()
    {
        await using var server = FtpTestServer.Start(c => c.Server.AllowedClientIps = ["10.99.99.0/24"]);

        await using var client = server.CreateClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.Connect());
    }
}
