using C_LabTap.Services;

namespace C_LabTap.Models;

// Shared by every view that shows the school's logo/background - the
// admin sidebar, the Logbook kiosk, and the Login/Logged-out screens -
// so they all reflect whatever Admin/Staff last uploaded in the
// Branding settings panel, instead of the hardcoded AAMNHS files.
//
// Uploaded files live in wwwroot/images/branding/ with a timestamp in
// the filename (see SettingsController.SaveBranding), so a new upload
// always gets a fresh URL and never fights the browser's image cache -
// no cache-busting query string needed.
public static class BrandingHelper
{
    public const string DefaultLogoUrl = "/images/aamnhs-logo.png";
    public const string DefaultBackgroundUrl = "/images/aamnhs-bg.jpg";

    public static async Task<string> GetLogoUrlAsync(SupabaseService supabase)
    {
        var fileName = await GetCustomizationFieldAsync(supabase, "logo_file");
        return string.IsNullOrWhiteSpace(fileName)
            ? DefaultLogoUrl
            : $"/images/branding/{fileName}";
    }

    public static async Task<string> GetBackgroundUrlAsync(SupabaseService supabase)
    {
        var fileName = await GetCustomizationFieldAsync(supabase, "background_file");
        return string.IsNullOrWhiteSpace(fileName)
            ? DefaultBackgroundUrl
            : $"/images/branding/{fileName}";
    }

    // system_customization is the single row (id=1) SettingsController's
    // own Branding panel actually saves to (see SettingsController.
    // GetCustomizationFieldAsync) - this used to read a separate, never-
    // written-to "settings" key/value table instead, which meant an
    // uploaded logo/background never actually showed up anywhere that
    // used this helper (admin sidebar, kiosk, login page) - see chat.
    private static async Task<string?> GetCustomizationFieldAsync(SupabaseService supabase, string column)
    {
        try
        {
            var rows = await supabase.GetAsync("system_customization", $"select={column}&id=eq.1&limit=1");
            return rows.Count > 0 ? rows[0][column]?.ToString() : null;
        }
        catch
        {
            // If Supabase is unreachable, fall back to the default
            // branding rather than breaking every page's layout.
            return null;
        }
    }
}
