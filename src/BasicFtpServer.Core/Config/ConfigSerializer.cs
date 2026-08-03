using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasicFtpServer.Core.Config;

public static class ConfigSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Keep backslashes in Windows paths readable rather than \-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ServerConfig config) => JsonSerializer.Serialize(config, Options);

    public static ServerConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<ServerConfig>(json, Options) ?? new ServerConfig();

    public static ServerConfig Clone(ServerConfig config) => Deserialize(Serialize(config));
}
