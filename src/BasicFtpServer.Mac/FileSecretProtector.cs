using System.Security.Cryptography;
using System.Text;
using BasicFtpServer.Core.Config;

namespace BasicFtpServer.Mac;

/// <summary>
/// Protects credentials with an AES key readable only by root. launchd runs the daemon as
/// root, and the installer creates both the key and config with mode 0600.
/// </summary>
internal sealed class FileSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private readonly byte[] _key;

    public FileSecretProtector(string keyPath, bool createIfMissing = true)
    {
        if (!File.Exists(keyPath))
        {
            if (!createIfMissing)
                throw new FileNotFoundException("The credential key does not exist.", keyPath);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _key = File.ReadAllBytes(keyPath);
        if (_key.Length != 32)
            throw new CryptographicException($"Credential key {keyPath} must contain exactly 32 bytes.");
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);

        var payload = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        payload[0] = FormatVersion;
        nonce.CopyTo(payload, 1);
        tag.CopyTo(payload, 13);
        cipher.CopyTo(payload, 29);
        return Convert.ToBase64String(payload);
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrEmpty(protectedValue)) return true;

        try
        {
            var payload = Convert.FromBase64String(protectedValue);
            if (payload.Length < 29 || payload[0] != FormatVersion) return false;
            var plain = new byte[payload.Length - 29];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plain);
            plaintext = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
