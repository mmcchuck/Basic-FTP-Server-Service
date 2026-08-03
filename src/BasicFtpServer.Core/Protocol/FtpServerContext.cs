using System.Text;
using BasicFtpServer.Core.Auth;
using BasicFtpServer.Core.Config;
using BasicFtpServer.Core.Diagnostics;

namespace BasicFtpServer.Core.Protocol;

/// <summary>Shared, immutable-per-run state handed to every session.</summary>
public sealed class FtpServerContext
{
    public FtpServerContext(ServerConfig config, UserStore users, LogRing log, PassivePortAllocator allocator)
    {
        Config = config;
        Users = users;
        Log = log;
        PassivePorts = allocator;
        FallbackEncoding = ResolveEncoding(config.Compatibility.FallbackEncoding);
    }

    public ServerConfig Config { get; }
    public UserStore Users { get; }
    public LogRing Log { get; }
    public PassivePortAllocator PassivePorts { get; }
    public Encoding FallbackEncoding { get; }

    /// <summary>
    /// Legacy codepages need the CodePagesEncodingProvider registered on .NET; without this
    /// windows-1252 throws and every non-ASCII filename becomes a hard failure.
    /// </summary>
    public static Encoding ResolveEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(string.IsNullOrWhiteSpace(name) ? "windows-1252" : name);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}
