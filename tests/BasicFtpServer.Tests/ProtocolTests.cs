using Xunit;

namespace BasicFtpServer.Tests;

/// <summary>
/// Wire-level tests. These use a raw client on purpose: a well-behaved library client would
/// smooth over exactly the behaviours copiers depend on.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public async Task GreetsWith220()
    {
        await using var server = FtpTestServer.Start();
        var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var reader = new StreamReader(tcp.GetStream());

        var greeting = await reader.ReadLineAsync();
        Assert.StartsWith("220 ", greeting);
        tcp.Dispose();
    }

    [Fact]
    public async Task SystReportsUnixSoClientsExpectUnixListings()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();

        var reply = await client.SendAsync("SYST");
        Assert.Equal("215 UNIX Type: L8", reply);
    }

    [Fact]
    public async Task FeatAdvertisesUtf8AndSize()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();

        var reply = await client.SendAsync("FEAT");
        Assert.Contains("UTF8", reply);
        Assert.Contains("SIZE", reply);
        Assert.Contains("MDTM", reply);
        Assert.EndsWith("211 End", reply);
    }

    [Fact]
    public async Task MinimalFeatOmitsTheExtendedFeatureList()
    {
        await using var server = FtpTestServer.Start(c => c.Compatibility.MinimalFeat = true);
        using var client = await server.ConnectRawAsync();

        var reply = await client.SendAsync("FEAT");
        Assert.Contains("UTF8", reply);
        Assert.DoesNotContain("MDTM", reply);
        Assert.DoesNotContain("EPSV", reply);
    }

    [Fact]
    public async Task AuthTlsIsRejectedCleanlySoClientsFallBackToPlaintext()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();

        var reply = await client.SendAsync("AUTH TLS");
        Assert.StartsWith("534 ", reply);
    }

    [Fact]
    public async Task AlloIsAcceptedBecauseSomeDevicesSendItBeforeStor()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        var reply = await client.SendAsync("ALLO 1048576");
        Assert.StartsWith("200 ", reply);
    }

    [Fact]
    public async Task LoginSucceedsWithCorrectCredentials()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();

        var reply = await client.LoginAsync();
        Assert.StartsWith("230 ", reply);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUserAreIndistinguishable()
    {
        await using var server = FtpTestServer.Start();

        using var badPassword = await server.ConnectRawAsync();
        var wrongPasswordReply = await badPassword.LoginAsync(password: "nope");

        using var unknownUser = await server.ConnectRawAsync();
        var unknownUserReply = await unknownUser.LoginAsync("ghost", "nope");

        Assert.StartsWith("530 ", wrongPasswordReply);
        Assert.Equal(wrongPasswordReply, unknownUserReply);
    }

    [Fact]
    public async Task FileCommandsRequireLogin()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();

        Assert.StartsWith("530 ", await client.SendAsync("PWD"));
        Assert.StartsWith("530 ", await client.SendAsync("LIST"));
        Assert.StartsWith("530 ", await client.SendAsync("PASV"));
    }

    [Fact]
    public async Task CwdAutoCreatesMissingDirectories()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        var reply = await client.SendAsync("CWD /2026/august");
        Assert.StartsWith("250 ", reply);
        Assert.True(Directory.Exists(server.PathOf("2026", "august")));

        var pwd = await client.SendAsync("PWD");
        Assert.Contains("\"/2026/august\"", pwd);
    }

    [Fact]
    public async Task CwdFailsWhenAutoCreateIsDisabled()
    {
        await using var server = FtpTestServer.Start(c => c.Compatibility.AutoCreateDirectories = false);
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.StartsWith("550 ", await client.SendAsync("CWD /nope"));
    }

    [Fact]
    public async Task CdupFromRootStaysAtRoot()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        await client.SendAsync("CDUP");
        await client.SendAsync("CDUP");

        var pwd = await client.SendAsync("PWD");
        Assert.Contains("\"/\"", pwd);
    }

    [Fact]
    public async Task TraversalOutsideTheHomeDirectoryIsContained()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        await client.SendAsync("CWD /../../..");

        var pwd = await client.SendAsync("PWD");
        Assert.Contains("\"/\"", pwd);

        // The directory created by MKD must land inside the home directory, not above it.
        await client.SendAsync("MKD ../../escaped");
        Assert.True(Directory.Exists(server.PathOf("escaped")));
        Assert.False(Directory.Exists(Path.Combine(server.Root, "..", "escaped")));
    }

    [Fact]
    public async Task AsciiTypeIsAcceptedButDataIsNotTransformed()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.StartsWith("200 ", await client.SendAsync("TYPE A"));
        Assert.StartsWith("200 ", await client.SendAsync("TYPE I"));
    }

    [Fact]
    public async Task UnknownCommandsGetA500RatherThanDroppingTheConnection()
    {
        await using var server = FtpTestServer.Start();
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.StartsWith("500 ", await client.SendAsync("FROBNICATE"));

        // The session must still be usable afterwards.
        Assert.StartsWith("200 ", await client.SendAsync("NOOP"));
    }

    [Fact]
    public async Task WriteOnlyUserCannotRetrieveOrDelete()
    {
        await using var server = FtpTestServer.Start(c =>
        {
            var permissions = c.Users[0].Permissions;
            permissions.Read = false;
            permissions.Delete = false;
        });

        File.WriteAllText(server.PathOf("existing.txt"), "data");

        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.StartsWith("550 ", await client.SendAsync("RETR existing.txt"));
        Assert.StartsWith("550 ", await client.SendAsync("DELE existing.txt"));
        Assert.True(File.Exists(server.PathOf("existing.txt")));
    }

    [Fact]
    public async Task DisabledUserCannotLogIn()
    {
        await using var server = FtpTestServer.Start(c => c.Users[0].Enabled = false);
        using var client = await server.ConnectRawAsync();

        Assert.StartsWith("530 ", await client.LoginAsync());
    }

    [Fact]
    public async Task PasvAdvertisesTheForcedAddressWhenConfigured()
    {
        await using var server = FtpTestServer.Start(c => c.Server.ForcedPassiveIp = "203.0.113.7");
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        var reply = await client.SendAsync("PASV");
        Assert.StartsWith("227 ", reply);
        Assert.Contains("(203,0,113,7,", reply);
    }

    [Fact]
    public async Task EpsvCanBeDisabledForDevicesThatMishandleIt()
    {
        await using var server = FtpTestServer.Start(c => c.Compatibility.EnableEpsv = false);
        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.StartsWith("502 ", await client.SendAsync("EPSV"));
    }

    [Fact]
    public async Task MdtmAndSizeReportFileMetadata()
    {
        await using var server = FtpTestServer.Start();
        File.WriteAllText(server.PathOf("meta.txt"), "0123456789");

        using var client = await server.ConnectRawAsync();
        await client.LoginAsync();

        Assert.Equal("213 10", await client.SendAsync("SIZE meta.txt"));

        var mdtm = await client.SendAsync("MDTM meta.txt");
        Assert.StartsWith("213 ", mdtm);
        Assert.Equal(14, mdtm[4..].Trim().Length);
    }
}
