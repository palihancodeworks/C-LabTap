using System.Security.Cryptography;
using System.Text;
using C_LabTap.Services;

namespace C_LabTap.Models;

// "Confirm this email address" for newly created staff accounts.
// SettingsController.AddStaff creates the account with
// email_verified = false and calls SendAsync; the emailed link lands on
// AccountController.VerifyEmail, which flips it to true. Until then
// AccountController.Login refuses the account (and re-sends the link).
//
// The link carries a random 32-byte token. Only its SHA-256 is stored
// (in otp_codes, purpose "verify_email"), the same way the 6-digit
// codes are - a plain fast hash is fine here because the token is long
// and random, unlike a password someone chose.
public static class EmailVerification
{
    public const string Purpose = "verify_email";
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // baseUrl is the site's own address as the browser sees it, e.g.
    // "http://c-labtap.runasp.net" - no trailing slash.
    public static async Task SendAsync(
        SupabaseService supabase, EmailService email,
        long staffId, string toEmail, string fullName, string baseUrl)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await supabase.InsertAsync("otp_codes", new
        {
            staff_id = staffId,
            purpose = Purpose,
            code_hash = HashToken(token),
            expires_at = DateTimeOffset.UtcNow.Add(LinkLifetime),
        });

        var link = $"{baseUrl}/Account/VerifyEmail?uid={staffId}&token={token}";
        await email.SendVerificationEmailAsync(toEmail, fullName, link);
    }
}
