using System.Text.RegularExpressions;

namespace C_LabTap.Models;

// Looks for text that reads like a classic SQL-injection attempt -
// quotes, SQL keywords, comment markers, etc - typed into a search
// box or the kiosk's LRN/RFID fields (see chat: "i will only get
// notified if someone puts sql commands in like typable areas").
//
// This is NOT itself a security control - the app's real protection
// against SQL injection is that it never builds raw SQL from user
// input in the first place (every database call goes through
// Supabase's REST API with escaped filter values - see chat: "how
// about a peerson trying to sql imject"). This class only exists to
// notice that someone TRIED, even though the attempt itself can't
// actually succeed here.
public static class SuspiciousInputDetector
{
    private static readonly Regex SqlPattern = new(
        @"(--|;|/\*|\*/|\bunion\b|\bselect\b|\bdrop\b|\binsert\b|\bdelete\b|\bupdate\b|\bor\s+1\s*=\s*1\b|'\s*or\s*'|xp_cmdshell)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeSqlInjection(string? input)
    {
        return !string.IsNullOrWhiteSpace(input) && SqlPattern.IsMatch(input);
    }

    // Fires the ntfy alert if the text looks suspicious. "where" is a
    // short label (e.g. "Item search", "Kiosk LRN field") so the
    // notification says which field it came from. includeValue=false
    // for a password field - the alert still fires, it just doesn't
    // echo what was typed (see chat: don't want an attempted, or a
    // real, password ending up inside a ntfy notification).
    public static async Task CheckAndAlertAsync(string? input, string where, bool includeValue = true)
    {
        if (!LooksLikeSqlInjection(input)) return;

        var message = includeValue
            ? $"Possible SQL injection attempt in {where}: {Truncate(input!, 200)}"
            : $"Possible SQL injection attempt in {where} (value hidden).";

        await NtfyNotifier.SendAsync("C-LabTap security alert", message);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
