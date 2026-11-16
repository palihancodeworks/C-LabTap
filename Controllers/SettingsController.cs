using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using C_LabTap.Models;
using C_LabTap.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace C_LabTap.Controllers;

// The "More" page (nav label changed from "Settings" - see
// _AdminLayout.cshtml - the controller/route name stays Settings).
// Three panels: Branding, Accounts, Login History - all account/system
// management, so the whole controller is Admin-only (see chat). A Staff
// account that tries to hit any action here, even by typing the URL
// directly, gets bounced to the dashboard before the action body runs.
public class SettingsController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly IWebHostEnvironment _env;
    private readonly EmailService _email;

    public SettingsController(SupabaseService supabase, IWebHostEnvironment env, EmailService email)
    {
        _supabase = supabase;
        _env = env;
        _email = email;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = RedirectToAction("Index", "Attendance");
            return;
        }
        base.OnActionExecuting(context);
    }

    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    public async Task<IActionResult> Index()
    {
        var model = await BuildViewModelAsync();

        if (IsAjax) return PartialView("_Content", model);

        ViewData["Title"] = "Settings";
        ViewData["Active"] = "Settings";
        return View(model);
    }

    private const long MaxLogoBytes = 2 * 1024 * 1024;        // 2 MB
    private const long MaxBackgroundBytes = 5 * 1024 * 1024;  // 5 MB

    // Checks an uploaded branding image before anything is saved:
    //  1. size is under the limit,
    //  2. the file name ends in .png / .jpg / .jpeg / .webp,
    //  3. the first bytes of the file really are that kind of image
    //     (a renamed .html or .exe fails here even with a .png name).
    // Returns the extension to save it under - always one of our own
    // fixed values, never whatever the uploader typed - or an error
    // message for the admin.
    private static async Task<(string? Extension, string? Error)> ValidateImageAsync(
        IFormFile file, string label, long maxBytes)
    {
        if (file.Length > maxBytes)
        {
            return (null, $"The {label} is too large. The limit is {maxBytes / (1024 * 1024)} MB.");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        if (ext != ".png" && ext != ".jpg" && ext != ".webp")
        {
            return (null, $"The {label} must be a PNG, JPG, or WebP image.");
        }

        var header = new byte[12];
        int read;
        await using (var stream = file.OpenReadStream())
        {
            read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        }

        var looksRight = ext switch
        {
            ".png" => read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
            ".jpg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            // WebP = "RIFF" + 4 size bytes + "WEBP"
            ".webp" => read >= 12
                && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50,
            _ => false,
        };
        if (!looksRight)
        {
            return (null, $"The {label} isn't a real {ext.TrimStart('.').ToUpperInvariant()} image. Choose a different file.");
        }

        return (ext, null);
    }

    // Admin uploads a new logo and/or background image. Either field can
    // be left empty to leave that one as-is. Saved with a timestamp in
    // the filename so the browser never serves a stale cached copy.
    // Both files are checked first; if either is rejected nothing is
    // saved and the admin sees why (BrandingError on the page).
    [HttpPost]
    public async Task<IActionResult> SaveBranding(IFormFile? logo, IFormFile? background)
    {
        string? logoExt = null, backgroundExt = null;

        if (logo is { Length: > 0 })
        {
            var (ext, error) = await ValidateImageAsync(logo, "logo", MaxLogoBytes);
            if (error != null)
            {
                TempData["BrandingError"] = error;
                return RedirectToAction("Index");
            }
            logoExt = ext;
        }
        if (background is { Length: > 0 })
        {
            var (ext, error) = await ValidateImageAsync(background, "background", MaxBackgroundBytes);
            if (error != null)
            {
                TempData["BrandingError"] = error;
                return RedirectToAction("Index");
            }
            backgroundExt = ext;
        }

        var brandingDir = Path.Combine(_env.WebRootPath, "images", "branding");
        Directory.CreateDirectory(brandingDir);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (logo is { Length: > 0 })
        {
            var oldFile = await GetCustomizationFieldAsync("logo_file");
            var fileName = $"logo_{stamp}{logoExt}";
            await using (var stream = System.IO.File.Create(Path.Combine(brandingDir, fileName)))
            {
                await logo.CopyToAsync(stream);
            }
            await UpdateCustomizationAsync(new { logo_file = fileName, updated_at = DateTimeOffset.UtcNow });
            DeleteOldBrandingFile(brandingDir, oldFile);
        }

        if (background is { Length: > 0 })
        {
            var oldFile = await GetCustomizationFieldAsync("background_file");
            var fileName = $"bg_{stamp}{backgroundExt}";
            await using (var stream = System.IO.File.Create(Path.Combine(brandingDir, fileName)))
            {
                await background.CopyToAsync(stream);
            }
            await UpdateCustomizationAsync(new { background_file = fileName, updated_at = DateTimeOffset.UtcNow });
            DeleteOldBrandingFile(brandingDir, oldFile);
        }

        return RedirectToAction("Index");
    }

    // Creates an Admin or Staff login - role comes from the toggle on
    // the "Add account" form (see chat: this used to always hardcode
    // Staff; now either role can be created from the same form). Email
    // and a starting PIN are mandatory here - the PIN is what that
    // person taps into the Logbook kiosk's Admin tab (see
    // LogbookController.VerifyAdminPin, now checked per-account instead
    // of one shared PIN), and both are meant to be changed by the staff
    // member themselves once the account is handed to them (profile
    // menu -> Change PIN / Link Google Account), not re-entered here.
    [HttpPost]
    public async Task<IActionResult> AddStaff([FromForm] string fullName, [FromForm] string password, [FromForm] string email, [FromForm] string pin, [FromForm] string? role)
    {
        fullName = (fullName ?? "").Trim();
        password = password ?? "";
        email = (email ?? "").Trim();
        pin = (pin ?? "").Trim();
        var resolvedRole = role == "Admin" ? "Admin" : "Staff";

        if (fullName.Length == 0 || password.Length < 8)
        {
            TempData["StaffError"] = password.Length < 8
                ? "Password must be at least 8 characters."
                : "Full name is required.";
            return RedirectToAction("Index");
        }
        if (email.Length == 0 || !email.Contains('@'))
        {
            TempData["StaffError"] = "A Gmail address is required.";
            return RedirectToAction("Index");
        }
        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            TempData["StaffError"] = "The starting PIN must be exactly 4 digits.";
            return RedirectToAction("Index");
        }

        // No separate Username field in the form anymore (see chat) - the
        // Gmail address doubles as the login username. staff_accounts.username
        // is still the column Login/ForgotPassword key off, but the DB only
        // enforces uniqueness among ACTIVE rows (see migration) - a removed
        // (is_active = false) account's email is free to reuse. Check the
        // same thing here so we show a clean message instead of letting a
        // DB unique-violation bubble up.
        var username = email;
        var existing = await _supabase.GetAsync("staff_accounts", $"select=id&username=eq.{Uri.EscapeDataString(username)}&is_active=eq.true&limit=1");
        if (existing.Count > 0)
        {
            TempData["StaffError"] = $"An account with the email \"{email}\" already exists.";
            return RedirectToAction("Index");
        }

        // Salted hashes - see SecretHasher.
        var passwordHash = SecretHasher.Hash(password);
        var pinHash = SecretHasher.Hash(pin);

        // email_verified starts false: the account exists but can't log in
        // (or use its PIN at the kiosk) until the address's owner opens
        // the link emailed right below - see EmailVerification.
        var inserted = await _supabase.InsertAsync("staff_accounts", new
        {
            full_name = fullName,
            username = username,
            password_hash = passwordHash,
            email = email,
            pin_hash = pinHash,
            role = resolvedRole,
            is_active = true,
            email_verified = false,
        });

        var newId = inserted?.GetValueOrDefault("id") is JsonElement idEl ? idEl.GetInt64() : 0;
        try
        {
            if (newId <= 0) throw new InvalidOperationException("The new account's id wasn't returned.");
            await EmailVerification.SendAsync(
                _supabase, _email, newId, email, fullName, $"{Request.Scheme}://{Request.Host}");
            TempData["StaffNotice"] = $"Account created. A verification link was emailed to {email} - it has to be opened before that account can log in.";
        }
        catch (Exception)
        {
            TempData["StaffError"] = $"Account created, but the verification email to {email} couldn't be sent. Check the server's email settings - a new link is sent automatically when they try to log in.";
        }

        return RedirectToAction("Index");
    }

    // Deactivates rather than hard-deletes, consistent with how students
    // and items are archived elsewhere in the app.
    [HttpPost]
    public async Task<IActionResult> RemoveStaff([FromForm] long id)
    {
        await _supabase.UpdateAsync("staff_accounts", $"id=eq.{id}", new { is_active = false });
        return RedirectToAction("Index");
    }



    // Powers the Login History card's pagination (see chat - it used to
    // be a plain server-rendered list capped at 50 rows with no way to
    // see older entries). Same GetPagedAsync + JSON shape convention as
    // Attendance/RegisterStudent/RegisterItem's List() actions.
    [HttpGet]
    public async Task<IActionResult> LoginHistoryList(int page = 1, int pageSize = 8)
    {
        var (rows, total) = await _supabase.GetPagedAsync(
            "login_history",
            "select=staff_name_snapshot,action,occurred_at&order=occurred_at.desc",
            page, pageSize);

        var history = rows.Select(row =>
        {
            var name = row.GetValueOrDefault("staff_name_snapshot")?.ToString() ?? "";
            var action = row.GetValueOrDefault("action")?.ToString() ?? "";
            var when = "";
            if (row.GetValueOrDefault("occurred_at")?.ToString() is string occurredRaw &&
                DateTime.TryParse(occurredRaw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var occurredAt))
            {
                when = occurredAt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
            }
            return new { name, action, when };
        }).ToList();

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 1;
        if (totalPages < 1) totalPages = 1;

        return Json(new { rows = history, total, page, pageSize, totalPages });
    }

    private async Task<SettingsViewModel> BuildViewModelAsync()
    {
        var customizationRows = await _supabase.GetAsync(
            "system_customization", "select=logo_file,background_file&id=eq.1&limit=1");
        var customization = customizationRows.Count > 0 ? customizationRows[0] : null;

        var staff = await _supabase.GetAsync(
            "staff_accounts",
            "select=id,full_name,username,role,is_active,email_verified,created_at&is_active=eq.true&order=created_at.asc");

        return new SettingsViewModel
        {
            LogoFile = customization?.GetValueOrDefault("logo_file")?.ToString(),
            BackgroundFile = customization?.GetValueOrDefault("background_file")?.ToString(),
            Staff = staff,
            StaffError = TempData["StaffError"] as string,
            StaffNotice = TempData["StaffNotice"] as string,
            BrandingError = TempData["BrandingError"] as string,
        };
    }

    // system_customization is a single row (id=1) of structured branding
    // columns - see the "system_customization" migration (chat) - rather
    // than the old generic settings key/value table, so these just read/
    // patch that one row instead of looking a key up each time.
    private async Task<string?> GetCustomizationFieldAsync(string column)
    {
        var rows = await _supabase.GetAsync("system_customization", $"select={column}&id=eq.1&limit=1");
        return rows.Count > 0 ? rows[0][column]?.ToString() : null;
    }

    private async Task UpdateCustomizationAsync(object fields)
    {
        await _supabase.UpdateAsync("system_customization", "id=eq.1", fields);
    }

    private static void DeleteOldBrandingFile(string brandingDir, string? oldFileName)
    {
        if (string.IsNullOrWhiteSpace(oldFileName)) return;
        var path = Path.Combine(brandingDir, oldFileName);
        if (System.IO.File.Exists(path))
        {
            try { System.IO.File.Delete(path); } catch { /* not worth failing the request over */ }
        }
    }
}
