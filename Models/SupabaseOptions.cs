namespace C_LabTap.Models;

// Bound from the "Supabase" section of appsettings.*.json. ServiceRoleKey
// bypasses Row Level Security entirely - it must only ever live in
// server-side config (appsettings.Development.json / user-secrets / env
// vars), never in anything served to the browser.
public class SupabaseOptions
{
    public string Url { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;
}
