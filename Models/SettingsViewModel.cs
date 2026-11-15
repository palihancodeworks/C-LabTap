namespace C_LabTap.Models;

// Everything the "More" page (Settings/_Content.cshtml) needs to render
// its three panels: Branding, Staff Accounts, Login History. There's no
// real login/RBAC wired up yet (see AccountController), so every panel
// is shown to whoever opens the page for now - role-based visibility
// gets added once that's built.
public class SettingsViewModel
{
    // Filenames only (e.g. "logo_1731400000.png"), living under
    // wwwroot/images/branding/. Null/empty means "use the default
    // AAMNHS logo/background shipped with the app" - see BrandingHelper.
    public string? LogoFile { get; set; }
    public string? BackgroundFile { get; set; }

    public List<Dictionary<string, object?>> Staff { get; set; } = new();

    public string? StaffError { get; set; }
}
