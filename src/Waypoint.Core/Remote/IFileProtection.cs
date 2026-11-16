namespace Waypoint.Core.Remote;

/// <summary>
/// Encrypts a file kept on this machine, such as the server's last answers, so a copy of it is useless elsewhere.
/// The PC app protects to the Windows user.
/// </summary>
public interface IFileProtection
{
    byte[] Protect(byte[] plain);

    /// <summary>Throws a CryptographicException when the bytes weren't protected here, or were damaged.</summary>
    byte[] Unprotect(byte[] stored);
}
