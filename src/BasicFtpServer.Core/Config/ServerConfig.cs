using System.Text.Json.Serialization;

namespace BasicFtpServer.Core.Config;

/// <summary>Root configuration object, persisted as config.json in %ProgramData%.</summary>
public sealed class ServerConfig
{
    public ServerSettings Server { get; set; } = new();
    public CompatibilitySettings Compatibility { get; set; } = new();
    public List<FtpUser> Users { get; set; } = [];
    public LoggingSettings Logging { get; set; } = new();

    public ServerConfig Clone() => ConfigSerializer.Clone(this);
}

public sealed class ServerSettings
{
    /// <summary>IP to bind the control listener to. 0.0.0.0 binds all interfaces (dual-stack).</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 21;

    /// <summary>
    /// Passive data port range. Defaults deliberately avoid 49152-51000, where Windows
    /// (Hyper-V / WSL / WinNAT) commonly reserves blocks — see PortRangeProbe.
    /// </summary>
    public int PassivePortMin { get; set; } = 55000;

    public int PassivePortMax { get; set; } = 55100;

    /// <summary>
    /// Overrides the IP advertised in the 227/229 passive reply. Needed on machines with
    /// Hyper-V / VPN / Docker adapters where automatic selection picks an interface the
    /// copier cannot reach. Null = derive from the control connection's local address.
    /// </summary>
    public string? ForcedPassiveIp { get; set; }

    /// <summary>
    /// When true, the address inside a PORT/EPRT command is ignored and the control
    /// connection's peer address is used instead. Many MFPs send a wrong or unroutable
    /// address here, so this defaults to true.
    /// </summary>
    public bool IgnorePortCommandAddress { get; set; } = true;

    public int MaxConnections { get; set; } = 20;

    public int IdleTimeoutSeconds { get; set; } = 300;

    /// <summary>Empty means allow any client. Entries may be plain IPs or CIDR ranges.</summary>
    public List<string> AllowedClientIps { get; set; } = [];
}

public sealed class CompatibilitySettings
{
    /// <summary>Create missing directories on CWD and STOR. Several MFPs upload into a path they never create.</summary>
    public bool AutoCreateDirectories { get; set; } = true;

    /// <summary>"unix" or "dos". Most embedded FTP clients only parse Unix `ls -l` output.</summary>
    public string ListingFormat { get; set; } = "unix";

    /// <summary>Codepage used for path bytes before the client negotiates UTF-8 via OPTS UTF8 ON.</summary>
    public string FallbackEncoding { get; set; } = "windows-1252";

    /// <summary>Emit a stripped-down FEAT reply. Some older firmware chokes on a long feature list.</summary>
    public bool MinimalFeat { get; set; }

    public bool EnableEpsv { get; set; } = true;

    public bool EnableEprt { get; set; } = true;

    /// <summary>Replace characters that are legal in a scanner job name but illegal on NTFS.</summary>
    public bool SanitizeFilenames { get; set; } = true;

    /// <summary>Upload to "name.part" then rename on completion, so folder watchers never see a partial scan.</summary>
    public bool WriteToPartFile { get; set; } = true;

    /// <summary>rename | overwrite | reject</summary>
    public string OnDuplicate { get; set; } = "rename";

    public string Greeting { get; set; } = "Basic FTP Server Service ready.";

    [JsonIgnore]
    public DuplicatePolicy DuplicatePolicy => OnDuplicate?.ToLowerInvariant() switch
    {
        "overwrite" => DuplicatePolicy.Overwrite,
        "reject" => DuplicatePolicy.Reject,
        _ => DuplicatePolicy.Rename,
    };

    [JsonIgnore]
    public bool UseDosListing => string.Equals(ListingFormat, "dos", StringComparison.OrdinalIgnoreCase);
}

public enum DuplicatePolicy
{
    Rename,
    Overwrite,
    Reject,
}

/// <summary>
/// A virtual user. Deliberately NOT a Windows account: FTP sends the password in the clear
/// and copiers store it readably in their own web UI, so a compromised device must not
/// yield an OS credential.
/// </summary>
public sealed class FtpUser
{
    public string Name { get; set; } = "";

    /// <summary>DPAPI (LocalMachine scope) protected password, base64. Empty means anonymous.</summary>
    public string PasswordProtected { get; set; } = "";

    public string HomeDirectory { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public FtpPermissions Permissions { get; set; } = new();
}

/// <summary>Least privilege by default: a copier needs STOR and nothing else.</summary>
public sealed class FtpPermissions
{
    public bool Read { get; set; }
    public bool Write { get; set; } = true;
    public bool Delete { get; set; }
    public bool CreateDirectory { get; set; } = true;
    public bool List { get; set; } = true;
}

public sealed class LoggingSettings
{
    public string Level { get; set; } = "Information";
    public int RetainDays { get; set; } = 14;

    /// <summary>Log every FTP verb and reply. This is the single most useful copier troubleshooting tool.</summary>
    public bool LogProtocolCommands { get; set; } = true;
}
