using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace C_LabTap.Models;

// One place that hashes and checks passwords and kiosk PINs.
//
// These used to be a bare SHA-256 of the text with no salt, which means
// two people with the same password had the same hash and a leaked table
// could be reversed with a lookup list. This uses ASP.NET Core's own
// PasswordHasher instead: PBKDF2 with a random salt per hash and many
// iterations, the framework's standard for stored passwords.
//
// Accounts created before this change still hold the old 64-character
// hex hash. Verify() recognises those, checks them the old way, and
// reports needsRehash = true so the caller can store a new salted hash
// right then - every account upgrades itself the next time its owner
// types the correct password or PIN, with no reset needed.
public static class SecretHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object NoUser = new();

    public static string Hash(string secret) => Hasher.HashPassword(NoUser, secret);

    public static bool Verify(string? storedHash, string? entered, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(storedHash) || entered == null) return false;

        if (IsLegacySha256(storedHash))
        {
            var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entered))).ToLowerInvariant();
            var matches = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(legacy),
                Encoding.ASCII.GetBytes(storedHash.ToLowerInvariant()));
            needsRehash = matches;
            return matches;
        }

        PasswordVerificationResult result;
        try
        {
            result = Hasher.VerifyHashedPassword(NoUser, storedHash, entered);
        }
        catch (FormatException)
        {
            // Not a hash this app wrote - treat as "doesn't match".
            return false;
        }

        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }

    public static bool IsLegacySha256(string hash) =>
        hash.Length == 64 && hash.All(Uri.IsHexDigit);
}
