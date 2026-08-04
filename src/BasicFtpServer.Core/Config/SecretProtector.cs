using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace BasicFtpServer.Core.Config;

/// <summary>
/// Protects stored passwords with DPAPI at LocalMachine scope.
///
/// A one-way hash would be the usual choice, but is wrong here: a technician configuring a
/// copier has to read the password back out of the UI. DPAPI + an ACL'd config file means
/// the value is unreadable on disk and unusable if the file is copied to another machine,
/// while still being recoverable through the elevated tray UI.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    bool TryUnprotect(string protectedValue, out string plaintext);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Ties the ciphertext to this application, so an unrelated DPAPI blob cannot be swapped in.
    private static readonly byte[] Entropy = "BasicFtpServerService/v1"u8.ToArray();

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        CryptographicOperations.ZeroMemory(bytes);
        return Convert.ToBase64String(cipher);
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrEmpty(protectedValue))
        {
            return true;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            plaintext = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // Config copied from another machine, or corrupted. Treat as unusable rather
            // than throwing — the server should still start and report the bad account.
            return false;
        }
    }
}

/// <summary>Plaintext pass-through used by tests so they need no DPAPI machine key.</summary>
public sealed class PlaintextSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = protectedValue ?? "";
        return true;
    }
}
