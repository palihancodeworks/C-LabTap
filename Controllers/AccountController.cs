using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using C_LabTap.Models;
using C_LabTap.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Keep this in sync with Program.cs's ExternalAuthScheme constant - a
// plain string "scheme name" has to match exactly on both sides, there's
// no shared constant to reference across Program.cs and a controller.

namespace C_LabTap.Controllers;

// Real staff/admin login against staff_accounts - this used to be a
// placeholder (the Log-In button just linked straight to /Attendance,
// no credentials checked - see chat). Every other controller now
// requires a signed-in account by default (Program.cs's global
// AuthorizeFilter); this one stays open so there's a way to actually
// sign in, and LogbookController stays open too since that's the
// public kiosk, not an admin page.
[AllowAnonymous]
public class AccountController : Controller
{
    private const string ExternalAuthScheme = "External";

    private readonly SupabaseService _supabase;
    private readonly EmailService _email;

    public AccountController(SupabaseService supabase, EmailService email)
    {
        _supabase = supabase;
        _email = email;
    }

    // Default landing page (served for "/" and "/Account").
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Attendance");
        }
        return View("LoggedOut");
    }

    public IActionResult LoggedOut()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Attendance");
        }
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password)
    {
        // See SuspiciousInputDetector - flags SQL-looking text typed
        // into the login form and pings ntfy (see chat: "wht about the
        // login text boxedsd"). Password value itself is never echoed.
        await SuspiciousInputDetector.CheckAndAlertAsync(username, "Login username field");
        await SuspiciousInputDetector.CheckAndAlertAsync(password, "Login password field", includeValue: false);

        username = (username ?? "").Trim();
        password = password ?? "";

        if (username.Length == 0 || password.Length == 0)
        {
            TempData["LoginError"] = "Enter your username and password.";
            return RedirectToAction("LoggedOut");
        }

        // Brute-force lockout: 5 wrong passwords for this username locks
        // it out for 5 minutes, checked before even looking the account
        // up (see chat - LoginAttemptGuard, shared with the kiosk PINs
        // and Change PIN). Keyed on the username, not the account id,
        // since a mistyped/unknown username never resolves to one.
        var loginAttemptKey = username.ToLowerInvariant();
        var lockedFor = await LoginAttemptGuard.GetLockoutSecondsAsync(_supabase, "login", loginAttemptKey);
        if (lockedFor != null)
        {
            TempData["LoginError"] = $"Too many failed attempts. {LoginAttemptGuard.FormatWait(lockedFor.Value)}";
            return RedirectToAction("LoggedOut");
        }

        // is_active=eq.true is in the query itself, not just checked after -
        // a removed account's email can now be reused by a new active
        // account (see chat), so the two can briefly share a username.
        // Filtering here means we always get the live account, never the
        // old removed one that happens to still be sitting in the table.
        var rows = await _supabase.GetAsync(
            "staff_accounts",
            $"select=id,full_name,username,password_hash,role,is_active,email,email_verified&username=eq.{Uri.EscapeDataString(username)}&is_active=eq.true&limit=1");

        var account = rows.Count > 0 ? rows[0] : null;
        var isActive = account != null && account["is_active"] is JsonElement activeEl
            && activeEl.ValueKind == JsonValueKind.True;

        // Salted hash check - see SecretHasher. needsRehash comes back true
        // when this account still holds an old unsalted hash, so it can be
        // replaced below now that the plain password is in hand.
        var storedHash = account?["password_hash"]?.ToString() ?? "";
        var needsRehash = false;
        var passwordOk = account != null && SecretHasher.Verify(storedHash, password, out needsRehash);

        if (account == null || !isActive || !passwordOk)
        {
            var justLockedFor = await LoginAttemptGuard.RecordFailureAsync(_supabase, "login", loginAttemptKey);
            TempData["LoginError"] = justLockedFor != null
                ? $"Too many failed attempts. {LoginAttemptGuard.FormatWait(justLockedFor.Value)}"
                : "Incorrect username or password.";
            return RedirectToAction("LoggedOut");
        }

        await LoginAttemptGuard.RecordSuccessAsync(_supabase, "login", loginAttemptKey);

        var id = ((JsonElement)account["id"]!).GetInt64();
        var fullName = account["full_name"]?.ToString() ?? username;
        var role = account["role"]?.ToString() ?? "Staff";

        if (needsRehash)
        {
            await _supabase.UpdateAsync("staff_accounts", $"id=eq.{id}", new { password_hash = SecretHasher.Hash(password) });
        }

        // A new account can't be used until its owner has clicked the
        // link emailed when an admin created it (see EmailVerification).
        // The password was right, so it's safe to tell them that much -
        // and to send a fresh link, in case the first one expired or got
        // lost. Skipped if one already went out in the last two minutes
        // so repeated attempts can't flood the inbox.
        var emailVerified = !(account.GetValueOrDefault("email_verified") is JsonElement verifiedEl
            && verifiedEl.ValueKind == JsonValueKind.False);
        if (!emailVerified)
        {
            var accountEmail = account.GetValueOrDefault("email")?.ToString();
            if (!string.IsNullOrWhiteSpace(accountEmail))
            {
                var twoMinutesAgo = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o"));
                var recent = await _supabase.GetAsync(
                    "otp_codes",
                    $"select=id&staff_id=eq.{id}&purpose=eq.{EmailVerification.Purpose}&created_at=gt.{twoMinutesAgo}&limit=1");
                if (recent.Count == 0)
                {
                    try
                    {
                        await EmailVerification.SendAsync(
                            _supabase, _email, id, accountEmail!, fullName, $"{Request.Scheme}://{Request.Host}");
                    }
                    catch (Exception)
                    {
                        // The message below still tells them what's needed.
                    }
                }
            }

            TempData["LoginError"] = "Verify your email before logging in. We sent a verification link to your inbox - open it, then log in again.";
            return RedirectToAction("LoggedOut");
        }

        await SignInStaffAsync(id, fullName, role, username);
        return RedirectToAction("Index", "Attendance");
    }

    // Where the link in the verification email lands (see
    // EmailVerification.SendAsync). Marks the account's email as
    // confirmed so Login will accept it, then sends the person to the
    // login page - it deliberately does NOT sign them in: the link proves
    // the mailbox, the password still proves the person.
    [HttpGet]
    public async Task<IActionResult> VerifyEmail(long uid, string? token)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(token))
        {
            TempData["LoginError"] = "That verification link isn't valid.";
            return RedirectToAction("LoggedOut");
        }

        var nowIso = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"));
        var tokenHash = EmailVerification.HashToken(token.Trim());
        var rows = await _supabase.GetAsync(
            "otp_codes",
            $"select=id&staff_id=eq.{uid}&purpose=eq.{EmailVerification.Purpose}&code_hash=eq.{tokenHash}&consumed=eq.false&expires_at=gt.{nowIso}&limit=1");

        if (rows.Count == 0)
        {
            var accountRows = await _supabase.GetAsync(
                "staff_accounts", $"select=email_verified&id=eq.{uid}&limit=1");
            var alreadyVerified = accountRows.Count > 0
                && accountRows[0].GetValueOrDefault("email_verified") is JsonElement el
                && el.ValueKind == JsonValueKind.True;

            if (alreadyVerified)
            {
                TempData["LoginNotice"] = "This email is already verified. You can log in.";
            }
            else
            {
                TempData["LoginError"] = "That verification link has expired or was already used. Log in with your password and we'll email you a new one.";
            }
            return RedirectToAction("LoggedOut");
        }

        var rowId = ((JsonElement)rows[0]["id"]!).GetInt64();
        await _supabase.UpdateAsync("otp_codes", $"id=eq.{rowId}", new { consumed = true });
        await _supabase.UpdateAsync("staff_accounts", $"id=eq.{uid}", new { email_verified = true });

        TempData["LoginNotice"] = "Email verified. You can log in now.";
        return RedirectToAction("LoggedOut");
    }

    // "Forgot password" on the login page - unauthenticated by nature
    // (that's the whole point), so this can't reuse RequestOtp/ChangePassword
    // above, which both require a signed-in session. Looks up the account
    // by username and emails a code to whatever address is on file for it.
    // Always answers the same way whether or not the username exists (and
    // whether or not it has an email on file) so this can't be used to
    // probe which usernames are real.
    [HttpPost]
    public async Task<IActionResult> ForgotPasswordRequestOtp([FromForm] string username)
    {
        username = (username ?? "").Trim();
        const string genericReply = "If that account exists and has an email on file, a code was sent to it.";

        if (username.Length == 0)
        {
            return Json(new { error = "Enter your username." });
        }

        var rows = await _supabase.GetAsync(
            "staff_accounts",
            $"select=id,email&username=eq.{Uri.EscapeDataString(username)}&is_active=eq.true&limit=1");

        if (rows.Count > 0)
        {
            var staffId = ((JsonElement)rows[0]["id"]!).GetInt64();
            var email = rows[0]["email"]?.ToString();

            if (!string.IsNullOrWhiteSpace(email))
            {
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

                await _supabase.InsertAsync("otp_codes", new
                {
                    staff_id = staffId,
                    purpose = "password_reset",
                    code_hash = codeHash,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
                });

                try
                {
                    await _email.SendOtpEmailAsync(email!, code, "Reset Password");
                }
                catch (Exception)
                {
                    // Same generic reply either way - don't leak that the
                    // account exists just because the email send failed.
                }
            }
        }

        return Json(new { success = true, message = genericReply });
    }

    // Second half of "Forgot password" - consumes the code and sets the
    // new password in one step (there's no signed-in session to carry a
    // "verified" state between two requests the way the logged-in Change
    // Password flow does).
    [HttpPost]
    public async Task<IActionResult> ForgotPasswordReset([FromForm] string username, [FromForm] string otp, [FromForm] string newPassword)
    {
        username = (username ?? "").Trim();
        newPassword = newPassword ?? "";

        if (newPassword.Length < 8)
        {
            return Json(new { error = "New password must be at least 8 characters." });
        }

        // Same brute-force lockout as Login - without this, the 6-digit
        // code itself (only ~1,000,000 possibilities) could just be
        // guessed over and over within its 10-minute window instead of
        // actually being read from the account's email (see chat: "try
        // hacking again outside and inside the system"). Keyed on the
        // username, same as Login, so this locks out before the account
        // lookup below even runs.
        var resetAttemptKey = username.ToLowerInvariant();
        var lockedFor = await LoginAttemptGuard.GetLockoutSecondsAsync(_supabase, "password_reset_otp", resetAttemptKey);
        if (lockedFor != null)
        {
            return Json(new { error = $"Too many failed attempts. {LoginAttemptGuard.FormatWait(lockedFor.Value)}" });
        }

        var rows = await _supabase.GetAsync(
            "staff_accounts",
            $"select=id&username=eq.{Uri.EscapeDataString(username)}&is_active=eq.true&limit=1");

        if (rows.Count == 0)
        {
            // Same message a wrong code would get - don't confirm whether
            // the username itself was the problem. Still counts as a
            // failed attempt against this username key, same as Login
            // does for an unknown username.
            await LoginAttemptGuard.RecordFailureAsync(_supabase, "password_reset_otp", resetAttemptKey);
            return Json(new { error = "Invalid code or username." });
        }

        var staffId = ((JsonElement)rows[0]["id"]!).GetInt64();

        if (!await TryConsumeOtpAsync(staffId, "password_reset", otp))
        {
            var justLockedFor = await LoginAttemptGuard.RecordFailureAsync(_supabase, "password_reset_otp", resetAttemptKey);
            return Json(new
            {
                error = justLockedFor != null
                    ? $"Too many failed attempts. {LoginAttemptGuard.FormatWait(justLockedFor.Value)}"
                    : "Invalid or expired code. Request a new one and try again."
            });
        }

        await LoginAttemptGuard.RecordSuccessAsync(_supabase, "password_reset_otp", resetAttemptKey);

        // The code just consumed was emailed to this account's address,
        // so the mailbox is proven too - no separate verification needed.
        var newHash = SecretHasher.Hash(newPassword);
        await _supabase.UpdateAsync("staff_accounts", $"id=eq.{staffId}", new { password_hash = newHash, email_verified = true });

        return Json(new { success = true });
    }

    // Shared by password Login and a Google sign-in that resolves to an
    // already-linked account (see GoogleResponse) - same claims, same
    // login_history entry, either way in.
    private async Task SignInStaffAsync(long id, string fullName, string role, string? username)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, fullName),
            new Claim(ClaimTypes.Role, role),
        };
        if (!string.IsNullOrEmpty(username)) claims.Add(new Claim("username", username));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        // login_history.action has a check constraint allowing only the
        // lowercase values 'login'/'logout' - not "Login"/"Logout".
        await _supabase.InsertAsync("login_history", new
        {
            staff_id = id,
            staff_name_snapshot = fullName,
            action = "login",
        });
    }

    // "Sign in with Google" (see LoggedOut.cshtml's button) - kicks off
    // the OAuth redirect to Google; GoogleResponse below handles what
    // comes back. Google only ever identifies a person, it doesn't
    // create accounts - see staff_google_accounts (chat): the Google
    // identity has to already be linked to an existing staff_accounts
    // row via GoogleLink, or this just shows an error.
    public IActionResult GoogleLogin()
    {
        var redirectUrl = Url.Action("GoogleResponse", "Account");
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    // Lets an already signed-in staff/admin connect their own Google
    // account from the sidebar profile menu, so they can use "Sign in
    // with Google" next time instead of their password. The "I'm
    // linking" flag rides inside the OAuth state (AuthenticationProperties
    // .Items) rather than TempData: TempData is a plain cookie that only
    // clears once it's actually read, so if this round trip to Google
    // ever gets interrupted (cancelled, blocked, closed tab) the flag
    // would otherwise leak into the next unrelated "Sign in with Google"
    // login attempt and make GoogleResponse misread it as a link request.
    // Items are bound into the signed state for this one request only.
    // Also requires a one-time `token` minted by VerifyGoogleOtp below,
    // which only hands one out after the signed-in person enters the
    // OTP code emailed to the account - so linking (or unlinking)
    // Google always needs that email step first, not just being logged in.
    public async Task<IActionResult> GoogleLink(string? token)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("LoggedOut");
        }

        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId) || string.IsNullOrEmpty(token))
        {
            return RedirectToAction("Index", "Attendance", new { notice = "linkexpired" });
        }

        // The OTP step (VerifyGoogleOtp below) already confirmed this
        // person's identity by email and minted this one-time token -
        // redeem it here so the same link can't be replayed.
        var tokenRows = await _supabase.GetAsync(
            "otp_codes",
            $"select=id&staff_id=eq.{staffId}&purpose=eq.google&link_token=eq.{Uri.EscapeDataString(token)}&link_token_used=eq.false&limit=1");
        if (tokenRows.Count == 0)
        {
            return RedirectToAction("Index", "Attendance", new { notice = "linkexpired" });
        }
        var tokenRowId = ((JsonElement)tokenRows[0]["id"]!).GetInt64();
        await _supabase.UpdateAsync("otp_codes", $"id=eq.{tokenRowId}", new { link_token_used = true });

        var redirectUrl = Url.Action("GoogleResponse", "Account");
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        properties.Items["link_staff_id"] = idClaim!;
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    // Where Google sends the browser back to after the user approves
    // (or cancels) sign-in - see Program.cs's AddGoogle(SignInScheme:
    // "External"). Reads the short-lived External cookie Google's own
    // middleware just signed in with its raw claims, then either links
    // it to the currently signed-in account
    // (GoogleLink's state flag) or looks it up in staff_google_accounts
    // to log in.
    public async Task<IActionResult> GoogleResponse()
    {
        var externalResult = await HttpContext.AuthenticateAsync(ExternalAuthScheme);
        await HttpContext.SignOutAsync(ExternalAuthScheme);

        if (!externalResult.Succeeded || externalResult.Principal == null)
        {
            TempData["LoginError"] = "Google sign-in was cancelled or failed.";
            return RedirectToAction("LoggedOut");
        }

        var googleSub = externalResult.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var googleEmail = externalResult.Principal.FindFirst(ClaimTypes.Email)?.Value;
        var googleName = externalResult.Principal.FindFirst(ClaimTypes.Name)?.Value;
        var googlePicture = externalResult.Principal.FindFirst("urn:google:image")?.Value;
        if (string.IsNullOrEmpty(googleSub))
        {
            TempData["LoginError"] = "Could not read your Google account.";
            return RedirectToAction("LoggedOut");
        }

        var linkStaffIdRaw = externalResult.Properties?.Items.TryGetValue("link_staff_id", out var linkStaffIdItem) == true ? linkStaffIdItem : null;
        if (!string.IsNullOrEmpty(linkStaffIdRaw) && long.TryParse(linkStaffIdRaw, out var linkStaffId))
        {
            // Linking flow - someone already logged in wants to connect
            // this Google account to their own staff_accounts row.
            var already = await _supabase.GetAsync(
                "staff_google_accounts", $"select=id&google_sub=eq.{Uri.EscapeDataString(googleSub)}&limit=1");
            if (already.Count > 0)
            {
                TempData["LoginError"] = "That Google account is already linked to a staff account.";
                return RedirectToAction("LoggedOut");
            }

            await _supabase.InsertAsync("staff_google_accounts", new
            {
                staff_account_id = linkStaffId,
                google_sub = googleSub,
                google_email = googleEmail ?? "",
                google_name = googleName,
                picture_url = googlePicture,
            });

            return RedirectToAction("Index", "Attendance");
        }

        // Login flow - find which staff account (if any) this Google
        // identity is linked to.
        var links = await _supabase.GetAsync(
            "staff_google_accounts", $"select=staff_account_id&google_sub=eq.{Uri.EscapeDataString(googleSub)}&limit=1");
        if (links.Count == 0)
        {
            TempData["LoginError"] = "This Google account isn't linked to any staff account. Log in with your password first, then link Google from your profile menu.";
            return RedirectToAction("LoggedOut");
        }

        var staffAccountId = ((JsonElement)links[0]["staff_account_id"]!).GetInt64();

        // Keep the stored Google name/photo fresh (also backfills the
        // name/picture for links made before those columns existed).
        await _supabase.UpdateAsync(
            "staff_google_accounts", $"google_sub=eq.{Uri.EscapeDataString(googleSub)}",
            new { google_email = googleEmail ?? "", google_name = googleName, picture_url = googlePicture });
        var accounts = await _supabase.GetAsync(
            "staff_accounts", $"select=id,full_name,username,role,is_active&id=eq.{staffAccountId}&limit=1");
        var account = accounts.Count > 0 ? accounts[0] : null;
        var isActive = account != null && account["is_active"] is JsonElement activeEl
            && activeEl.ValueKind == JsonValueKind.True;
        if (account == null || !isActive)
        {
            TempData["LoginError"] = "This staff account is no longer active.";
            return RedirectToAction("LoggedOut");
        }

        var id = ((JsonElement)account["id"]!).GetInt64();
        var fullName = account["full_name"]?.ToString() ?? "Account";
        var role = account["role"]?.ToString() ?? "Staff";
        var username = account["username"]?.ToString();

        await SignInStaffAsync(id, fullName, role, username);
        return RedirectToAction("Index", "Attendance");
    }

    // Lets a signed-in staff/admin change their own password from the
    // The Change Password/PIN modals are two-step on purpose (see chat):
    // the New/Confirm/OTP fields stay hidden until these two endpoints
    // confirm the CURRENT password/PIN is actually right, so nobody can
    // even get to typing a new one without proving they know the old
    // one first. ChangePassword/ChangePin below still re-check the
    // current value themselves too when the change is actually saved -
    // this step is a UX gate, not the only check.
    [HttpPost]
    public async Task<IActionResult> VerifyCurrentPassword([FromForm] string currentPassword)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        var rows = await _supabase.GetAsync("staff_accounts", $"select=password_hash&id=eq.{staffId}&limit=1");
        if (rows.Count == 0)
        {
            return Json(new { error = "Account not found." });
        }

        var storedHash = rows[0]["password_hash"]?.ToString() ?? "";
        if (!SecretHasher.Verify(storedHash, currentPassword ?? "", out _))
        {
            return Json(new { error = "Current password is incorrect." });
        }

        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> VerifyCurrentPin([FromForm] string currentPin)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        // Brute-force lockout, keyed on this staff account's own id -
        // unlike the kiosk PINs, this one IS a known account the moment
        // the request comes in (see chat - LoginAttemptGuard).
        var lockAttemptKey = staffId.ToString();
        var lockedFor = await LoginAttemptGuard.GetLockoutSecondsAsync(_supabase, "change_pin", lockAttemptKey);
        if (lockedFor != null)
        {
            return Json(new { error = $"Too many failed attempts. {LoginAttemptGuard.FormatWait(lockedFor.Value)}" });
        }

        var rows = await _supabase.GetAsync("staff_accounts", $"select=pin_hash&id=eq.{staffId}&limit=1");
        if (rows.Count == 0)
        {
            return Json(new { error = "Account not found." });
        }

        var storedHash = rows[0]["pin_hash"]?.ToString() ?? "";
        if (storedHash.Length > 0 && !SecretHasher.Verify(storedHash, (currentPin ?? "").Trim(), out _))
        {
            var justLockedFor = await LoginAttemptGuard.RecordFailureAsync(_supabase, "change_pin", lockAttemptKey);
            return Json(new
            {
                error = justLockedFor != null
                    ? $"Too many failed attempts. {LoginAttemptGuard.FormatWait(justLockedFor.Value)}"
                    : "Current PIN is incorrect."
            });
        }

        await LoginAttemptGuard.RecordSuccessAsync(_supabase, "change_pin", lockAttemptKey);
        return Json(new { success = true });
    }

    // sidebar profile menu - checks the CURRENT password against
    // staff_accounts before writing the new one, same as any normal
    // account settings page (not the Admin tab's separate kiosk PIN,
    // which is a different thing entirely).
    // NOTE: this controller has class-level [AllowAnonymous] (needed for
    // Login/LoggedOut), and [AllowAnonymous] always wins over any
    // [Authorize] in the same pipeline - so this checks
    // User.Identity.IsAuthenticated itself instead of an [Authorize]
    // attribute, which would silently do nothing here.
    [HttpPost]
    public async Task<IActionResult> ChangePassword([FromForm] string currentPassword, [FromForm] string newPassword, [FromForm] string otp)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        currentPassword = currentPassword ?? "";
        newPassword = newPassword ?? "";
        if (newPassword.Length < 8)
        {
            return Json(new { error = "New password must be at least 8 characters." });
        }

        var rows = await _supabase.GetAsync(
            "staff_accounts", $"select=password_hash&id=eq.{staffId}&limit=1");
        if (rows.Count == 0)
        {
            return Json(new { error = "Account not found." });
        }

        var storedHash = rows[0]["password_hash"]?.ToString() ?? "";
        if (!SecretHasher.Verify(storedHash, currentPassword, out _))
        {
            return Json(new { error = "Current password is incorrect." });
        }

        if (!await TryConsumeOtpAsync(staffId, "password", otp))
        {
            return Json(new { error = "Invalid or expired code. Send a new code and try again." });
        }

        var newHash = SecretHasher.Hash(newPassword);
        await _supabase.UpdateAsync("staff_accounts", $"id=eq.{staffId}", new { password_hash = newHash });

        return Json(new { success = true });
    }

    // Lets whoever's signed in set their own kiosk PIN (staff_accounts
    // .pin_hash - see LogbookController.VerifyAdminPin), the same way
    // ChangePassword above works, including the same [AllowAnonymous]-
    // on-the-class caveat (see that method's comment), so this checks
    // IsAuthenticated itself rather than relying on [Authorize].
    [HttpPost]
    public async Task<IActionResult> ChangePin([FromForm] string currentPin, [FromForm] string newPin, [FromForm] string otp, [FromForm] bool forgotPin = false)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        currentPin = (currentPin ?? "").Trim();
        newPin = (newPin ?? "").Trim();
        if (newPin.Length != 4 || !newPin.All(char.IsDigit))
        {
            return Json(new { error = "New PIN must be exactly 4 digits." });
        }

        var rows = await _supabase.GetAsync(
            "staff_accounts", $"select=pin_hash&id=eq.{staffId}&limit=1");
        if (rows.Count == 0)
        {
            return Json(new { error = "Account not found." });
        }

        // "Forgot PIN" skips this match check entirely - the OTP check
        // right below still has to pass, and that's already proof this is
        // the account holder (they're also already signed in with their
        // password to have gotten to this modal at all), so a forgotten
        // current PIN doesn't lock them out of resetting it.
        if (!forgotPin)
        {
            var storedHash = rows[0]["pin_hash"]?.ToString() ?? "";
            if (storedHash.Length > 0 && !SecretHasher.Verify(storedHash, currentPin, out _))
            {
                return Json(new { error = "Current PIN is incorrect." });
            }
        }

        if (!await TryConsumeOtpAsync(staffId, "pin", otp))
        {
            return Json(new { error = "Invalid or expired code. Send a new code and try again." });
        }

        var newHash = SecretHasher.Hash(newPin);
        await _supabase.UpdateAsync("staff_accounts", $"id=eq.{staffId}", new { pin_hash = newHash });

        return Json(new { success = true });
    }

    // Emails a 6-digit code to whoever's signed in, for one of the three
    // things that now require it: changing their password, changing
    // their PIN, or linking/unlinking their Google account. The code
    // always goes to the email on THEIR OWN staff_accounts row - never
    // anyone else's - so each person verifies their own change.
    [HttpPost]
    public async Task<IActionResult> RequestOtp([FromForm] string purpose)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        if (purpose != "password" && purpose != "pin" && purpose != "google")
        {
            return Json(new { error = "Invalid request." });
        }

        var rows = await _supabase.GetAsync("staff_accounts", $"select=email&id=eq.{staffId}&limit=1");
        var email = rows.Count > 0 ? rows[0]["email"]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(email))
        {
            return Json(new { error = "No email is on file for your account. Ask an admin to add one." });
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

        await _supabase.InsertAsync("otp_codes", new
        {
            staff_id = staffId,
            purpose = purpose,
            code_hash = codeHash,
            expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var purposeLabel = purpose switch
        {
            "password" => "Change Password",
            "pin" => "Change PIN",
            "google" => "Google Account Change",
            _ => "Account Change",
        };

        try
        {
            await _email.SendOtpEmailAsync(email!, code, purposeLabel);
        }
        catch (Exception)
        {
            return Json(new { error = "Couldn't send the email - check the server's email settings." });
        }

        return Json(new { success = true });
    }

    // Google linking is a full-page redirect to Google and back (see
    // GoogleLink), so the OTP for it can't just be passed along with
    // the next form post the way password/pin's OTP is - this verifies
    // the code first and, if it checks out, mints a one-time token for
    // GoogleLink to redeem right before it actually challenges Google.
    [HttpPost]
    public async Task<IActionResult> VerifyGoogleOtp([FromForm] string otp)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        var otpRowId = await TryConsumeOtpRowAsync(staffId, "google", otp);
        if (otpRowId == null)
        {
            return Json(new { error = "Invalid or expired code. Send a new code and try again." });
        }

        var token = Guid.NewGuid().ToString("N");
        await _supabase.UpdateAsync("otp_codes", $"id=eq.{otpRowId}", new { link_token = token });

        return Json(new { token });
    }

    // Removes this account's linked Google sign-in - also OTP-gated,
    // same as changing a password or PIN, since it's an account-security
    // change too.
    [HttpPost]
    public async Task<IActionResult> GoogleUnlink([FromForm] string otp)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(idClaim, out var staffId))
        {
            return Json(new { error = "Your session has expired - please log in again." });
        }

        if (!await TryConsumeOtpAsync(staffId, "google", otp))
        {
            return Json(new { error = "Invalid or expired code. Send a new code and try again." });
        }

        await _supabase.DeleteAsync("staff_google_accounts", $"staff_account_id=eq.{staffId}");

        return Json(new { success = true });
    }

    // Shared OTP check for password/PIN/unlink, which (unlike Google
    // linking above) consume the code and finish their change in the
    // same request - so this just needs a yes/no.
    private async Task<bool> TryConsumeOtpAsync(long staffId, string purpose, string? enteredCode)
    {
        return await TryConsumeOtpRowAsync(staffId, purpose, enteredCode) != null;
    }

    // Looks up the most recent unconsumed, unexpired OTP row for this
    // account+purpose, checks the hash, and marks it consumed - returns
    // that row's id (so callers like VerifyGoogleOtp can attach a
    // link_token to it) or null if the code's wrong/expired/missing.
    private async Task<long?> TryConsumeOtpRowAsync(long staffId, string purpose, string? enteredCode)
    {
        if (string.IsNullOrWhiteSpace(enteredCode)) return null;

        var nowIso = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"));
        var rows = await _supabase.GetAsync(
            "otp_codes",
            $"select=id,code_hash&staff_id=eq.{staffId}&purpose=eq.{purpose}&consumed=eq.false&expires_at=gt.{nowIso}&order=created_at.desc&limit=1");
        if (rows.Count == 0) return null;

        var storedHash = rows[0]["code_hash"]?.ToString() ?? "";
        var enteredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(enteredCode.Trim()))).ToLowerInvariant();
        if (!string.Equals(storedHash, enteredHash, StringComparison.OrdinalIgnoreCase)) return null;

        var id = ((JsonElement)rows[0]["id"]!).GetInt64();
        await _supabase.UpdateAsync("otp_codes", $"id=eq.{id}", new { consumed = true });
        return id;
    }

    // openForgot lets the "Forgot your current password?" link inside the
    // signed-in Change Password modal (_AdminLayout.cshtml) sign the user
    // out and land back on the login page with the Forgot Password overlay
    // already open (see forgot-password.js), instead of making them close
    // the modal, log out by hand, and find that link themselves.
    public async Task<IActionResult> Logout(bool openForgot = false)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var nameClaim = User.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
            await _supabase.InsertAsync("login_history", new
            {
                staff_id = long.TryParse(idClaim, out var id) ? (long?)id : null,
                staff_name_snapshot = nameClaim,
                action = "logout",
            });
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return openForgot
            ? RedirectToAction("LoggedOut", new { openForgot = true })
            : RedirectToAction("LoggedOut");
    }
}
