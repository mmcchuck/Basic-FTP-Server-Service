using System.Security.AccessControl;
using System.Security.Principal;

namespace BasicFtpServer.Core.Config;

/// <summary>
/// Loads and saves config.json under %ProgramData%, and locks it down so only SYSTEM and
/// Administrators can read it. The file holds DPAPI blobs rather than plaintext, but the
/// ACL is the second half of that story — without it any local user could enumerate the
/// destination folders and account names.
/// </summary>
public sealed class ConfigStore
{
    public const string AppFolderName = "BasicFtpServerService";

    private readonly object _saveLock = new();

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        ConfigPath = Path.Combine(Directory, "config.json");
        LogDirectory = Path.Combine(Directory, "logs");
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppFolderName);

    public string Directory { get; }
    public string ConfigPath { get; }
    public string LogDirectory { get; }

    public bool Exists => File.Exists(ConfigPath);

    public ServerConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new ServerConfig();
        }

        return ConfigSerializer.Deserialize(File.ReadAllText(ConfigPath));
    }

    /// <summary>Load, falling back to defaults if the file is unreadable or malformed.</summary>
    public ServerConfig LoadOrDefault(out string? error)
    {
        error = null;
        try
        {
            return Load();
        }
        catch (Exception ex)
        {
            error = $"Could not read {ConfigPath}: {ex.Message}. Using defaults.";
            return new ServerConfig();
        }
    }

    /// <summary>
    /// Writes atomically (temp file then replace) so a crash mid-save can never leave a
    /// truncated config that would strand the service at next boot.
    /// </summary>
    public void Save(ServerConfig config)
    {
        lock (_saveLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, ConfigSerializer.Serialize(config));

            if (File.Exists(ConfigPath))
            {
                File.Replace(temp, ConfigPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, ConfigPath);
            }

            TryHardenAcl();
        }
    }

    public void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Grants SYSTEM and Administrators full control and removes inherited access for
    /// everyone else. Failure is non-fatal: a locked-down ACL is defence in depth, not a
    /// precondition for the server running.
    /// </summary>
    public bool TryHardenAcl()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(ConfigPath))
            {
                return false;
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var info = new FileInfo(ConfigPath);
            var security = info.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (FileSystemAccessRule existing in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(existing);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
