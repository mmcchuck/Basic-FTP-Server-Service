using System.Security.Cryptography;
using System.Text;
using BasicFtpServer.Core.Config;

namespace BasicFtpServer.Core.Auth;

public enum AuthResult
{
    Success,
    UnknownUser,
    BadPassword,
    Disabled,
    /// <summary>Credential could not be decrypted — usually a config copied from another machine.</summary>
    CredentialUnreadable,
    HomeDirectoryMissing,
}

public sealed class UserStore
{
    private readonly ISecretProtector _protector;
    private volatile IReadOnlyList<FtpUser> _users;

    public UserStore(IEnumerable<FtpUser> users, ISecretProtector protector)
    {
        _protector = protector;
        _users = [.. users];
    }

    public void Replace(IEnumerable<FtpUser> users) => _users = [.. users];

    public IReadOnlyList<FtpUser> Users => _users;

    public FtpUser? Find(string name) =>
        _users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    public AuthResult Authenticate(string username, string password, out FtpUser? user)
    {
        user = Find(username);
        if (user is null)
        {
            return AuthResult.UnknownUser;
        }

        if (!user.Enabled)
        {
            return AuthResult.Disabled;
        }

        if (!_protector.TryUnprotect(user.PasswordProtected, out var expected))
        {
            return AuthResult.CredentialUnreadable;
        }

        // An empty stored password means the account is open (classic anonymous behaviour):
        // the copier can send anything, which is what devices with no password field do.
        if (expected.Length > 0 && !FixedTimeEquals(expected, password))
        {
            return AuthResult.BadPassword;
        }

        if (string.IsNullOrWhiteSpace(user.HomeDirectory))
        {
            return AuthResult.HomeDirectoryMissing;
        }

        return AuthResult.Success;
    }

    /// <summary>Decrypts a stored password so the tray UI can show it to a technician.</summary>
    public bool TryRevealPassword(FtpUser user, out string password) =>
        _protector.TryUnprotect(user.PasswordProtected, out password);

    public string ProtectPassword(string plaintext) => _protector.Protect(plaintext);

    /// <summary>
    /// Compares via fixed-size digests so the comparison time does not leak the password
    /// length. FTP is cleartext on the wire anyway, but this costs nothing.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> left = stackalloc byte[32];
        Span<byte> right = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), left);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), right);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
