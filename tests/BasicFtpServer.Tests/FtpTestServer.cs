using System.Net.Sockets;
using System.Text;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using BasicFtpServer.Core.Protocol;
using FluentFTP;

namespace BasicFtpServer.Tests;

/// <summary>
/// Spins the real server up on an ephemeral port against a temp directory.
///
/// Passive ports are configured as 0-0 so the OS hands out an ephemeral port per transfer,
/// which keeps parallel test runs from fighting over a fixed range.
/// </summary>
public sealed class FtpTestServer : IAsyncDisposable
{
    public const string User = "copier1";
    public const string Password = "s3cret";

    private FtpTestServer(FtpServerHost host, ServerConfig config, LogRing log, string root)
    {
        Host = host;
        Config = config;
        Log = log;
        Root = root;
    }

    public FtpServerHost Host { get; }
    public ServerConfig Config { get; }
    public LogRing Log { get; }
    public string Root { get; }
    public int Port => Host.Port;

    public static FtpTestServer Start(Action<ServerConfig>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "bftps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var config = new ServerConfig
        {
            Server =
            {
                ListenAddress = "127.0.0.1",
                Port = 0,
                PassivePortMin = 0,
                PassivePortMax = 0,
                IdleTimeoutSeconds = 30,
            },
            Users =
            [
                new FtpUser
                {
                    Name = User,
                    PasswordProtected = Password,
                    HomeDirectory = root,
                    Enabled = true,
                    Permissions = new FtpPermissions
                    {
                        Read = true,
                        Write = true,
                        Delete = true,
                        CreateDirectory = true,
                        List = true,
                    },
                },
            ],
        };

        configure?.Invoke(config);

        var log = new LogRing();
        var host = new FtpServerHost(config, new PlaintextSecretProtector(), log);
        host.Start();

        return new FtpTestServer(host, config, log, root);
    }

    public AsyncFtpClient CreateClient(bool passive = true, string? user = null, string? password = null)
    {
        var client = new AsyncFtpClient("127.0.0.1", user ?? User, password ?? Password, Port);
        client.Config.DataConnectionType = passive ? FtpDataConnectionType.PASV : FtpDataConnectionType.PORT;
        client.Config.ValidateAnyCertificate = true;
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 15000;
        client.Config.DataConnectionConnectTimeout = 15000;
        client.Config.DataConnectionReadTimeout = 15000;
        return client;
    }

    public Task<RawFtpClient> ConnectRawAsync() => RawFtpClient.ConnectAsync(Port);

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public string DumpLog() => string.Join(Environment.NewLine, Log.Snapshot().Select(e => e.ToString()));

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best effort; a leftover temp directory should never fail a test.
        }
    }
}

/// <summary>
/// Line-level FTP client used where the exact wire behaviour is the thing under test and a
/// well-behaved library client would paper over it.
/// </summary>
public sealed class RawFtpClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private RawFtpClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
    }

    public static async Task<RawFtpClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var client = new RawFtpClient(tcp);
        await client.ReadReplyAsync();
        return client;
    }

    /// <summary>Reads a reply, transparently consuming the continuation lines of a multi-line response.</summary>
    public async Task<string> ReadReplyAsync()
    {
        var first = await _reader.ReadLineAsync() ?? throw new IOException("Connection closed.");
        if (first.Length < 4 || first[3] != '-')
        {
            return first;
        }

        var code = first[..3];
        var builder = new StringBuilder(first);
        while (true)
        {
            var line = await _reader.ReadLineAsync() ?? throw new IOException("Connection closed mid-reply.");
            builder.Append('\n').Append(line);
            if (line.StartsWith(code + " ", StringComparison.Ordinal))
            {
                return builder.ToString();
            }
        }
    }

    public async Task<string> SendAsync(string command)
    {
        await _writer.WriteLineAsync(command);
        return await ReadReplyAsync();
    }

    public async Task<string> LoginAsync(string user = FtpTestServer.User, string password = FtpTestServer.Password)
    {
        await SendAsync($"USER {user}");
        return await SendAsync($"PASS {password}");
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
            _reader.Dispose();
            _tcp.Dispose();
        }
        catch
        {
            // Already closed.
        }
    }
}
