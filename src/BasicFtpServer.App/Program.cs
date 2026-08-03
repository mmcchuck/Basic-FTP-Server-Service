using System.Runtime.InteropServices;
using BasicFtpServer.App.Ipc;
using BasicFtpServer.App.Service;
using BasicFtpServer.App.Setup;
using BasicFtpServer.App.Tray;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace BasicFtpServer.App;

/// <summary>
/// Single executable, two roles.
///
/// Session 0 isolation means a service can never show UI, so the tray has to be its own
/// process in the user's session. Shipping both roles in one exe keeps deployment, signing,
/// and versioning to a single artifact.
/// </summary>
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--tray";

        return mode switch
        {
            "--service" => RunService(args),
            "--tray" => RunTray(),
            "--install-service" => Cli(InstallService),
            "--uninstall-service" => Cli(UninstallService),
            "--add-firewall-rules" => Cli(AddFirewallRules),
            "--remove-firewall-rules" => Cli(() => Report(FirewallRules.Remove(), "Firewall rules removed.")),
            // Optional second argument names the account the tray should start for; the
            // installer supplies the pre-elevation user, which may not be the one that
            // answered the UAC prompt.
            "--register-tray" => Cli(() => Report(
                TrayAutostart.Register(ServiceControl.ExecutablePath, args.Length > 1 ? args[1] : null),
                "Tray logon task registered.")),
            "--unregister-tray" => Cli(() => Report(TrayAutostart.Unregister(), "Tray logon task removed.")),
            "--status" => Cli(PrintStatus),
            "--help" or "-h" or "/?" => Cli(PrintHelp),
            _ => RunTray(),
        };
    }

    // ---- Service role ----------------------------------------------------------------

    private static int RunService(string[] args)
    {
        var store = new ConfigStore();
        store.EnsureDirectories();

        // First run: write a config with no accounts. Seeding a default user with a blank
        // password would leave an open FTP server on the network until somebody noticed.
        if (!store.Exists)
        {
            store.Save(new ServerConfig());
        }

        var config = store.LoadOrDefault(out _);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(config.Logging.Level))
            .WriteTo.File(
                Path.Combine(store.LogDirectory, "ftpserver-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, config.Logging.RetainDays),
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddWindowsService(options => options.ServiceName = ServiceControl.ServiceName);

            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger, dispose: true);

            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(new LogRing());
            builder.Services.AddSingleton<ServerSupervisor>();
            builder.Services.AddSingleton<ControlPipeServer>();
            builder.Services.AddHostedService<FtpWorker>();

            builder.Build().Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The service terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static LogEventLevel ParseLevel(string level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;

    // ---- Tray role -------------------------------------------------------------------

    private static int RunTray()
    {
        using var singleInstance = new Mutex(true, TraySignals.SingleInstanceMutex, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Hand off to the tray that is already running rather than exiting silently,
            // which makes the Start Menu shortcut look broken.
            try
            {
                using var showUi = EventWaitHandle.OpenExisting(TraySignals.ShowUiEvent);
                showUi.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other instance is starting up or shutting down. Nothing to hand off to.
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
        return 0;
    }

    // ---- Installer helpers -----------------------------------------------------------

    private static int InstallService()
    {
        var result = ServiceControl.Install();
        if (!result.Success)
        {
            Console.WriteLine($"Failed to register the service: {result.Output}");
            return result.ExitCode;
        }

        Console.WriteLine("Service registered.");
        return ServiceControl.Start() ? 0 : 0;
    }

    private static int UninstallService()
    {
        var result = ServiceControl.Uninstall();
        Console.WriteLine(result.Success ? "Service removed." : $"Failed to remove the service: {result.Output}");
        return result.Success ? 0 : result.ExitCode;
    }

    private static int AddFirewallRules()
    {
        var config = new ConfigStore().LoadOrDefault(out _);
        var result = FirewallRules.Add(
            ServiceControl.ExecutablePath,
            config.Server.Port,
            config.Server.PassivePortMin,
            config.Server.PassivePortMax);

        return Report(result, "Firewall rules added.");
    }

    private static int PrintStatus()
    {
        Console.WriteLine($"Service:  {ServiceControl.GetState()}");
        Console.WriteLine($"Tray task registered: {TrayAutostart.IsRegistered()}");

        var status = ControlPipeClient.GetStatusAsync().GetAwaiter().GetResult();
        if (status is null)
        {
            Console.WriteLine("Control pipe: unreachable (the service is not running, or you are not elevated).");
            return 1;
        }

        Console.WriteLine($"Listening: {status.Running} on port {status.Port}");
        Console.WriteLine($"Addresses: {string.Join(", ", status.LocalAddresses)}");
        Console.WriteLine($"Passive:   {status.PassivePortMin}-{status.PassivePortMax} " +
                          $"({status.PassiveAvailable}/{status.PassiveChecked} bindable)");
        Console.WriteLine($"Sessions:  {status.Sessions.Length}");

        if (!string.IsNullOrEmpty(status.LastError))
        {
            Console.WriteLine($"Last error: {status.LastError}");
        }

        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            Basic FTP Server Service

              BasicFtpServer.exe                      Open the system tray UI (default)
              BasicFtpServer.exe --tray               Open the system tray UI
              BasicFtpServer.exe --service            Run as a Windows service (used by the SCM)
              BasicFtpServer.exe --status             Print service and listener status

            Setup (require an elevated prompt):
              --install-service / --uninstall-service
              --add-firewall-rules / --remove-firewall-rules
              --register-tray [DOMAIN\user] / --unregister-tray

            --register-tray takes the account the tray should start for. Pass it when the
            person installing is not the person who will be logged in; it defaults to
            whoever runs the command.
            """);
        return 0;
    }

    private static int Report(ProcessResult result, string successMessage)
    {
        Console.WriteLine(result.Success ? successMessage : result.Output);
        return result.Success ? 0 : result.ExitCode;
    }

    /// <summary>
    /// Runs a console-style command from a WinExe. Without attaching to the parent console
    /// the output of the installer helpers would go nowhere.
    /// </summary>
    private static int Cli(Func<int> action)
    {
        var attached = AttachConsole(AttachParentProcess);
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (attached)
            {
                FreeConsole();
            }
        }
    }

    private const int AttachParentProcess = -1;

    // Classic DllImport rather than LibraryImport: the source-generated variant requires
    // AllowUnsafeBlocks across the whole project, which is not worth it for two calls.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
