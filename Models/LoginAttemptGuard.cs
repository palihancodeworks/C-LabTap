using System.Text.Json;
using C_LabTap.Services;

namespace C_LabTap.Models;

// Shared brute-force lockout for every PIN/password check in the app
// (admin Login, the kiosk Admin tab PIN, the Equipment Return staff
// PIN, and the admin-side Change PIN "Current PIN" field - see chat).
// 5 failed attempts in a row locks that scope+identifier out for 5
// minutes; a success, or the lockout expiring, resets the count.
//
// "identifier" means something different per scope since not every
// check is tied to one account yet at the time it fails:
//   - login        -> the username being typed (not every account is
//                      real, so this can't be a staff account id)
//   - change_pin   -> the logged-in staff account's own id (this one
//                      IS a known account)
//   - kiosk_admin_pin / return_pin -> the kiosk's own IP address,
//                      since any active staff member's PIN can match -
//                      there's no single account to blame until one
//                      does, so the lockout is on the terminal itself.
public static class LoginAttemptGuard
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    // Null if not currently locked out, otherwise how many whole seconds
    // remain - for the "try again in Xm Ys" message.
    public static async Task<int?> GetLockoutSecondsAsync(SupabaseService supabase, string scope, string identifier)
    {
        var row = await GetRowAsync(supabase, scope, identifier);
        if (row == null) return null;

        var lockedUntil = GetDateTime(row, "locked_until");
        if (lockedUntil == null) return null;

        var remaining = lockedUntil.Value - DateTimeOffset.UtcNow;
        return remaining.TotalSeconds > 0 ? (int)Math.Ceiling(remaining.TotalSeconds) : null;
    }

    // Call after a failed attempt. Returns the lockout seconds if this
    // failure is the one that just triggered the lockout (so the
    // caller can show "too many attempts" immediately instead of
    // waiting for the next try), otherwise null.
    public static async Task<int?> RecordFailureAsync(SupabaseService supabase, string scope, string identifier)
    {
        var row = await GetRowAsync(supabase, scope, identifier);
        var nowUtc = DateTimeOffset.UtcNow;

        if (row == null)
        {
            await supabase.InsertAsync("login_attempts", new
            {
                scope,
                identifier,
                fail_count = 1,
                updated_at = nowUtc
            });
            return null;
        }

        var id = row["id"];
        var currentCount = GetInt(row, "fail_count");
        var newCount = currentCount + 1;

        if (newCount >= MaxAttempts)
        {
            var lockedUntil = nowUtc.Add(LockoutDuration);
            await supabase.UpdateAsync("login_attempts", $"id=eq.{id}", new
            {
                fail_count = 0,
                locked_until = lockedUntil,
                updated_at = nowUtc
            });
            return (int)LockoutDuration.TotalSeconds;
        }

        await supabase.UpdateAsync("login_attempts", $"id=eq.{id}", new
        {
            fail_count = newCount,
            updated_at = nowUtc
        });
        return null;
    }

    // Call after a successful attempt - clears the count so the next
    // mistake starts counting from zero again instead of carrying over.
    public static async Task RecordSuccessAsync(SupabaseService supabase, string scope, string identifier)
    {
        var row = await GetRowAsync(supabase, scope, identifier);
        if (row == null) return;

        await supabase.UpdateAsync("login_attempts", $"id=eq.{row["id"]}", new
        {
            fail_count = 0,
            locked_until = (DateTimeOffset?)null,
            updated_at = DateTimeOffset.UtcNow
        });
    }

    // "Try again in 4m 12s" / "Try again in 38s" - used by every caller
    // so the wording is identical everywhere.
    public static string FormatWait(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        if (minutes <= 0) return $"Try again in {secs}s.";
        return secs == 0 ? $"Try again in {minutes}m." : $"Try again in {minutes}m {secs}s.";
    }

    private static async Task<Dictionary<string, object?>?> GetRowAsync(SupabaseService supabase, string scope, string identifier)
    {
        var rows = await supabase.GetAsync(
            "login_attempts",
            $"select=id,fail_count,locked_until&scope=eq.{Uri.EscapeDataString(scope)}&identifier=eq.{Uri.EscapeDataString(identifier)}&limit=1");
        return rows.Count > 0 ? rows[0] : null;
    }

    private static int GetInt(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var val) && val is JsonElement el && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;
    }

    private static DateTimeOffset? GetDateTime(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var val) || val is not JsonElement el || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return DateTimeOffset.TryParse(el.GetString(), out var dt) ? dt : null;
    }
}
