using System.Globalization;

namespace BasicFtpServer.App.Setup;

/// <summary>
/// Windows Firewall rules for the server.
///
/// Two rules are required, not one: the control port alone is enough to log in and then
/// have every transfer hang, because the passive data connections land on a completely
/// different port range. That failure mode — "it connects but scanning times out" — is the
/// single most common FTP firewall mistake.
/// </summary>
public static class FirewallRules
{
    public const string ControlRuleName = "Basic FTP Server Service (Control)";
    public const string PassiveRuleName = "Basic FTP Server Service (Passive Data)";

    public static ProcessResult Add(string executablePath, int controlPort, int passiveMin, int passiveMax)
    {
        Remove();

        var control = Netsh(
            $"advfirewall firewall add rule name=\"{ControlRuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={controlPort.ToString(CultureInfo.InvariantCulture)} " +
            $"program=\"{executablePath}\" enable=yes profile=any");

        if (!control.Success)
        {
            return control;
        }

        var passive = Netsh(
            $"advfirewall firewall add rule name=\"{PassiveRuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={passiveMin.ToString(CultureInfo.InvariantCulture)}-" +
            $"{passiveMax.ToString(CultureInfo.InvariantCulture)} " +
            $"program=\"{executablePath}\" enable=yes profile=any");

        return passive.Success
            ? new ProcessResult(0, $"{control.Output}\n{passive.Output}".Trim())
            : passive;
    }

    public static ProcessResult Remove()
    {
        var control = Netsh($"advfirewall firewall delete rule name=\"{ControlRuleName}\"");
        var passive = Netsh($"advfirewall firewall delete rule name=\"{PassiveRuleName}\"");

        // A missing rule reports a non-zero exit code, which is not an error when removing.
        return new ProcessResult(0, $"{control.Output}\n{passive.Output}".Trim());
    }

    public static bool ControlRuleExists() =>
        Netsh($"advfirewall firewall show rule name=\"{ControlRuleName}\"").Success;

    private static ProcessResult Netsh(string arguments) => ProcessRunner.Run("netsh.exe", arguments);
}
