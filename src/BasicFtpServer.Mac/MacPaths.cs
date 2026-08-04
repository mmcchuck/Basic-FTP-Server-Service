namespace BasicFtpServer.Mac;

internal static class MacPaths
{
    public const string Label = "com.basicftpserverservice.daemon";
    public static string DataDirectory => Environment.GetEnvironmentVariable("BASIC_FTP_DATA_DIR")
        ?? "/Library/Application Support/Basic FTP Server Service";
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string KeyPath => Path.Combine(DataDirectory, "credentials.key");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
