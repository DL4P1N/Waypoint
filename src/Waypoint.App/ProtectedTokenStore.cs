using System.IO;
using System.Security.Cryptography;
using System.Text;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>Keeps the mail sign-in encrypted to this Windows user, so the file is useless if copied.</summary>
internal sealed class ProtectedTokenStore(string path) : ITokenStore
{
    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(UserProtection.Instance.Unprotect(File.ReadAllBytes(path)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Written beside the file first and moved over it, so a crash mid-write never leaves a cut-off token.</summary>
    public void Save(string token)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, UserProtection.Instance.Protect(Encoding.UTF8.GetBytes(token)));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Encryption to this Windows user with DPAPI, as the sign-ins are kept: only this user on this PC can read it back.
/// Also used for the server's last answers in server-reads.json.
/// </summary>
internal sealed class UserProtection : IFileProtection
{
    public static readonly UserProtection Instance = new();

    private UserProtection()
    {
    }

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] stored) => ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
}
