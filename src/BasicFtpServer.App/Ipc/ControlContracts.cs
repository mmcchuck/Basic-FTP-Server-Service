using System.Text.Json;
using System.Text.Json.Serialization;
using BasicFtpServer.Core.Config;

namespace BasicFtpServer.App.Ipc;

public static class ControlCommands
{
    public const string GetStatus = "status";
    public const string GetConfig = "config.get";
    public const string SetConfig = "config.set";
    public const string StartServer = "server.start";
    public const string StopServer = "server.stop";
    public const string GetLog = "log.get";
    public const string ClearLog = "log.clear";
}

public sealed record ControlRequest(string Command, string? Payload = null);

public sealed record ControlResponse(bool Ok, string? Payload = null, string? Error = null);

public sealed record SessionDto(
    int Id,
    string RemoteAddress,
    string? User,
    DateTimeOffset ConnectedAt,
    string LastCommand,
    long BytesReceived,
    long BytesSent);

public sealed record ServerStatusDto(
    bool Running,
    bool Retrying,
    string? LastError,
    int Port,
    string[] LocalAddresses,
    int PassivePortMin,
    int PassivePortMax,
    int PassiveAvailable,
    int PassiveChecked,
    DateTimeOffset? StartedAt,
    SessionDto[] Sessions);

public sealed record LogLineDto(long Sequence, string Kind, string Text);

public sealed record LogPageDto(long LastSequence, LogLineDto[] Lines);

/// <summary>
/// Config as it crosses the pipe.
///
/// Passwords travel as plaintext here and are re-protected with DPAPI on the service side.
/// That is deliberate: a technician has to be able to read a password back when setting up
/// a copier, and the pipe is local-only with an ACL restricted to Administrators and SYSTEM
/// — the same people who could read the DPAPI blob's plaintext anyway.
/// </summary>
public sealed record ConfigTransfer(ServerConfig Config, Dictionary<string, string> Passwords);

public static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
