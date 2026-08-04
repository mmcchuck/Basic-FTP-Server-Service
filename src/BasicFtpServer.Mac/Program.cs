using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using BasicFtpServer.Core.Protocol;

namespace BasicFtpServer.Mac;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This host is for macOS. Use BasicFtpServer.exe on Windows.");
            return 1;
        }

        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
            return command switch
            {
                "run" or "--run" => await RunAsync(),
                "init" => Init(),
                "add-user" => AddUser(args.Skip(1).ToArray()),
                "remove-user" => RemoveUser(args.Skip(1).ToArray()),
                "list-users" => ListUsers(),
                "show-config" => ShowConfig(),
                "status" => Status(),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static ConfigStore Store() => new(MacPaths.DataDirectory);

    private static int Init()
    {
        RequireRootForSystemData();
        var store = Store();
        store.EnsureDirectories();
        _ = new FileSecretProtector(MacPaths.KeyPath);
        if (!store.Exists) store.Save(new ServerConfig());
        Harden(store);
        Console.WriteLine($"Initialized {MacPaths.DataDirectory}");
        return 0;
    }

    private static int AddUser(string[] args)
    {
        RequireRootForSystemData();
        if (args.Length < 3)
            throw new ArgumentException("Usage: add-user <name> <password> <scan-folder> [--read] [--delete]");

        var store = Store();
        store.EnsureDirectories();
        var protector = new FileSecretProtector(MacPaths.KeyPath);
        var config = store.LoadOrDefault(out var error);
        if (error is not null) throw new InvalidOperationException(error);
        if (config.Users.Any(u => string.Equals(u.Name, args[0], StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"User '{args[0]}' already exists.");

        var home = Path.GetFullPath(args[2]);
        System.IO.Directory.CreateDirectory(home);
        config.Users.Add(new FtpUser
        {
            Name = args[0],
            PasswordProtected = protector.Protect(args[1]),
            HomeDirectory = home,
            Permissions = new FtpPermissions
            {
                Read = args.Contains("--read"),
                Delete = args.Contains("--delete"),
            },
        });
        store.Save(config);
        Harden(store);
        Console.WriteLine($"Added '{args[0]}' with scan folder {home}. Restart the service to apply it.");
        return 0;
    }

    private static int RemoveUser(string[] args)
    {
        RequireRootForSystemData();
        if (args.Length != 1) throw new ArgumentException("Usage: remove-user <name>");
        var store = Store();
        var config = store.Load();
        var removed = config.Users.RemoveAll(u => string.Equals(u.Name, args[0], StringComparison.OrdinalIgnoreCase));
        if (removed == 0) throw new InvalidOperationException($"User '{args[0]}' was not found.");
        store.Save(config);
        Harden(store);
        Console.WriteLine($"Removed '{args[0]}'. Restart the service to apply it.");
        return 0;
    }

    private static int ListUsers()
    {
        var config = Store().Load();
        if (config.Users.Count == 0) Console.WriteLine("No FTP users configured.");
        foreach (var user in config.Users)
            Console.WriteLine($"{user.Name}\t{(user.Enabled ? "enabled" : "disabled")}\t{user.HomeDirectory}");
        return 0;
    }

    private static int ShowConfig()
    {
        Console.WriteLine(File.ReadAllText(MacPaths.ConfigPath));
        return 0;
    }

    private static int Status()
    {
        var config = Store().LoadOrDefault(out _);
        var launchctl = RunProcess("/bin/launchctl", "print", $"system/{MacPaths.Label}");
        Console.WriteLine(launchctl == 0 ? "Service: loaded" : "Service: not loaded");
        Console.WriteLine($"Listener: {config.Server.ListenAddress}:{config.Server.Port}");
        Console.WriteLine($"Addresses: {string.Join(", ", LocalAddresses())}");
        Console.WriteLine($"Users: {config.Users.Count(u => u.Enabled)} enabled");
        return launchctl == 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync()
    {
        var store = Store();
        store.EnsureDirectories();
        if (!store.Exists)
        {
            store.Save(new ServerConfig());
            Harden(store);
        }
        var config = store.Load();
        var protector = new FileSecretProtector(MacPaths.KeyPath);
        var log = new LogRing();
        using var writer = OpenLog();
        log.EntryAdded += entry =>
        {
            writer.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Kind}] {entry.Message}");
            writer.Flush();
        };

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });

        var delay = TimeSpan.FromSeconds(2);
        while (!shutdown.IsCancellationRequested)
        {
            await using var host = new FtpServerHost(config, protector, log);
            try
            {
                host.Start();
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (FtpBindException ex)
            {
                log.Add(LogKind.Warning, $"{ex.Message} Retrying in {(int)delay.TotalSeconds}s.");
                try { await Task.Delay(delay, shutdown.Token); } catch (OperationCanceledException) { }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                continue;
            }
            finally { await host.StopAsync(); }
            break;
        }
        return 0;
    }

    private static StreamWriter OpenLog()
    {
        System.IO.Directory.CreateDirectory(MacPaths.LogDirectory);
        var path = Path.Combine(MacPaths.LogDirectory, $"ftpserver-{DateTime.Now:yyyyMMdd}.log");
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    }

    private static void Harden(ConfigStore store)
    {
        if (File.Exists(store.ConfigPath))
            File.SetUnixFileMode(store.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(MacPaths.DataDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RequireRootForSystemData()
    {
        if (MacPaths.DataDirectory.StartsWith("/Library/", StringComparison.Ordinal) && Environment.UserName != "root")
            throw new UnauthorizedAccessException("Run this command with sudo.");
    }

    private static int RunProcess(string file, params string[] args)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit();
        return process.ExitCode;
    }

    private static string[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).Distinct().Order().ToArray();

    private static int Help()
    {
        Console.WriteLine("""
            Basic FTP Server Service for macOS

              basic-ftp-server init
              basic-ftp-server add-user <name> <password> <scan-folder> [--read] [--delete]
              basic-ftp-server remove-user <name>
              basic-ftp-server list-users
              basic-ftp-server status
              basic-ftp-server show-config
              basic-ftp-server run

            System configuration lives in /Library/Application Support/Basic FTP Server Service.
            Use sudo for init and account changes, then restart with:
              sudo launchctl kickstart -k system/com.basicftpserverservice.daemon
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Help();
        return 2;
    }
}
