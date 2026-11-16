using System.Security.Cryptography;
using System.Text;
using Waypoint.Core.Platform;

namespace Waypoint.Server;

/// <summary>
/// The pairing token the PC app sends with every request. Only its hash is kept, so the token itself is shown
/// once, when it's made, and can't be read back off the disk. Deleting the file and restarting makes a new one.
/// </summary>
public sealed class ServerToken
{
    public const string FileName = "server-token.sha256";

    /// <summary>Where a new token is written once, readable only by the server's user, so it never goes to the log.</summary>
    public const string PairingFileName = "pairing-token.txt";

    private readonly byte[] _hash;

    private ServerToken(byte[] hash) => _hash = hash;

    /// <summary>Loads the token's hash, making a new token first if there isn't one. <paramref name="created"/> is the new token, shown once.</summary>
    public static ServerToken LoadOrCreate(string dataDirectory, out string? created)
    {
        var path = Path.Combine(dataDirectory, FileName);
        created = null;
        if (File.Exists(path) && TryParse(File.ReadAllText(path)) is { } stored)
        {
            return new ServerToken(stored);
        }

        created = Create(dataDirectory);
        return new ServerToken(Hash(created));
    }

    /// <summary>Makes and saves a new token, replacing any old one, and returns it.</summary>
    public static string Create(string dataDirectory)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        FileTokenStore.WriteOwnerOnly(Path.Combine(dataDirectory, FileName), Convert.ToHexString(Hash(token)));
        return token;
    }

    /// <summary>Compared in constant time, so how long a wrong guess takes says nothing about the right one.</summary>
    public bool Matches(string? presented) =>
        presented is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Hash(presented), _hash);

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));

    private static byte[]? TryParse(string text)
    {
        try
        {
            var bytes = Convert.FromHexString(text.Trim());
            return bytes.Length == SHA256.HashSizeInBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
