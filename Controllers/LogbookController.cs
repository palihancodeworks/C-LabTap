using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Models;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

// The kiosk-facing "Logbook" - a standalone full-screen page (own layout,
// no admin sidebar) where students tap an RFID card or type their LRN to
// record attendance. This controller only covers the Attendance tab;
// Equipment (item borrow/return) is a separate tab built later.
//
// Public on purpose (Program.cs's global AuthorizeFilter would otherwise
// require a signed-in staff/admin account, same as the rest of the admin
// app) - this is the unsupervised kiosk terminal students tap in on.
[AllowAnonymous]
public class LogbookController : Controller
{
    private readonly SupabaseService _supabase;

    public LogbookController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        ViewData["Title"] = "Logbook - Attendance";
        return View();
    }

    // Equipment tab (see chat - was a "Coming soon" placeholder, then a
    // separate Borrow/Return sub-nav, simplified down to one screen -
    // see chat). Identification itself (RFID tap / manual LRN) is the
    // same two-mode UI as Attendance, just reused here instead of
    // instantly recording anything - Equipment needs an item picked
    // first, so identifying only confirms who's at the kiosk. Once
    // identified, the page shows whatever's relevant: items the student
    // currently has out (if any, with a Return action) and what's free
    // to borrow - driven by their actual data, not by a nav sub-route.
    public IActionResult Equipment()
    {
        ViewData["Title"] = "Logbook - Equipment";
        return View();
    }

    // Gates the kiosk's "Admin" tab. Every active staff/admin now has
    // their own PIN (staff_accounts.pin_hash, set when the account is
    // created and changeable from the profile menu afterward) instead
    // of one shared PIN - the PIN is never stored in plain text, only
    // its SHA-256 hash, and we hash whatever was typed here and compare
    // against every active account's hash rather than any raw value.
    [HttpPost]
    public async Task<IActionResult> VerifyAdminPin([FromForm] string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return Json(new { ok = false });
        }

        // Brute-force lockout, keyed on this kiosk terminal's own IP
        // (see chat - LoginAttemptGuard) rather than an account: any
        // active staff member's PIN can match here, so there's no one
        // account to blame until a PIN actually matches.
        var kioskKey = KioskIdentifier();
        var lockedFor = await LoginAttemptGuard.GetLockoutSecondsAsync(_supabase, "kiosk_admin_pin", kioskKey);
        if (lockedFor != null)
        {
            return Json(new { ok = false, message = $"Too many failed attempts. {LoginAttemptGuard.FormatWait(lockedFor.Value)}" });
        }

        var enteredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin.Trim()))).ToLowerInvariant();

        var rows = await _supabase.GetAsync(
            "staff_accounts", $"select=id&is_active=eq.true&pin_hash=eq.{enteredHash}&limit=1");

        if (rows.Count == 0)
        {
            var justLockedFor = await LoginAttemptGuard.RecordFailureAsync(_supabase, "kiosk_admin_pin", kioskKey);
            return Json(new
            {
                ok = false,
                message = justLockedFor != null
                    ? $"Too many failed attempts. {LoginAttemptGuard.FormatWait(justLockedFor.Value)}"
                    : (string?)null
            });
        }

        await LoginAttemptGuard.RecordSuccessAsync(_supabase, "kiosk_admin_pin", kioskKey);
        return Json(new { ok = true });
    }

    // Identifies "this kiosk terminal" for PIN lockouts that aren't tied
    // to one staff account (see VerifyAdminPin/ReturnEquipment) - the
    // connecting IP, since each kiosk is one shared physical device on
    // the school network, not a personal one.
    private string KioskIdentifier()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    // Used by manual entry once all 12 LRN digits are typed, to show
    // "is this you? <name>" before the student confirms. Archived
    // students (is_active = false - transferred out, see
    // RegisterStudentController.Delete) come back as not found, same as
    // RecordAttendance below - they shouldn't be greeted by name here
    // either if they can't actually tap in anymore.
    //
    // rfidTag (optional, added for the Equipment tab - see chat): a
    // plain identify-only lookup by card tag, distinct from
    // RecordAttendance's RFID path, which logs a time in/out immediately
    // on tap. Equipment needs to know *who* tapped before anything is
    // recorded (an item still has to be picked), so it can't reuse that
    // recording endpoint. Returns the student's id too, now, so the
    // Equipment screen has something to pass to EquipmentBorrowed/
    // CreateEquipmentBorrow afterward - Attendance's own JS only reads
    // "found"/"name" and ignores the new field, so this stays backward
    // compatible.
    [HttpGet]
    public async Task<IActionResult> LookupStudent(string? lrn, string? rfidTag)
    {
        string query;
        if (!string.IsNullOrWhiteSpace(rfidTag))
        {
            query = $"select=id,full_name,is_active&rfid_tag=eq.{Uri.EscapeDataString(rfidTag.Trim())}&limit=1";
        }
        else if (!string.IsNullOrWhiteSpace(lrn))
        {
            query = $"select=id,full_name,is_active&lrn=eq.{Uri.EscapeDataString(lrn.Trim())}&limit=1";
        }
        else
        {
            return Json(new { found = false });
        }

        var rows = await _supabase.GetAsync("students", query);

        if (rows.Count == 0)
        {
            return Json(new { found = false });
        }

        var isActive = true;
        if (rows[0].TryGetValue("is_active", out var activeVal) &&
            activeVal is JsonElement activeEl &&
            (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False))
        {
            isActive = activeEl.GetBoolean();
        }
        if (!isActive)
        {
            return Json(new { found = false });
        }

        var id = ((JsonElement)rows[0]["id"]!).GetInt64();
        return Json(new { found = true, id, name = rows[0]["full_name"]?.ToString() });
    }

    // Fires on an RFID tap (immediately) or a manual Confirm click.
    // Resolves the student by LRN or RFID tag, auto-decides Time In vs
    // Time Out from today's existing attendance_log rows, and logs it.
    // A soft-deleted student (is_active = false - see
    // RegisterStudentController.Delete) does NOT get reactivated by a tap
    // anymore - Delete now means "transferred out," not "temporarily
    // away," so their card/LRN stops working here until an admin brings
    // them back from the Archived tab themselves (see chat).
    [HttpPost]
    public async Task<IActionResult> RecordAttendance([FromForm] AttendanceRecordRequest request)
    {
        List<Dictionary<string, object?>> studentRows;

        if (!string.IsNullOrWhiteSpace(request.RfidTag))
        {
            studentRows = await _supabase.GetAsync(
                "students",
                $"select=id,full_name,is_active&rfid_tag=eq.{Uri.EscapeDataString(request.RfidTag.Trim())}&limit=1");
        }
        else if (!string.IsNullOrWhiteSpace(request.Lrn))
        {
            studentRows = await _supabase.GetAsync(
                "students",
                $"select=id,full_name,is_active&lrn=eq.{Uri.EscapeDataString(request.Lrn.Trim())}&limit=1");
        }
        else
        {
            return Json(new { recognized = false });
        }

        if (studentRows.Count == 0)
        {
            return Json(new { recognized = false });
        }

        var studentId = ((JsonElement)studentRows[0]["id"]!).GetInt64();
        var studentName = studentRows[0]["full_name"]?.ToString() ?? "";

        var isActive = true;
        if (studentRows[0].TryGetValue("is_active", out var activeVal) &&
            activeVal is JsonElement activeEl &&
            (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False))
        {
            isActive = activeEl.GetBoolean();
        }

        if (!isActive)
        {
            // Archived = transferred out. Their card/LRN no longer works
            // at the kiosk; an admin has to retrieve them from the
            // Archived tab on purpose for them to be able to tap in again.
            return Json(new { recognized = false, reason = "archived" });
        }

        // "Today" in the kiosk's own local time (the server's local clock -
        // this machine sits in the lab, so its local time is the school's
        // local time), converted to UTC for the query since occurred_at is
        // stored as timestamptz.
        var localNow = DateTime.Now;
        var startUtc = localNow.Date.ToUniversalTime().ToString("o");
        var endUtc = localNow.Date.AddDays(1).ToUniversalTime().ToString("o");

        var todayRows = await _supabase.GetAsync(
            "attendance_log",
            $"select=event_type&student_id=eq.{studentId}" +
            $"&occurred_at=gte.{Uri.EscapeDataString(startUtc)}" +
            $"&occurred_at=lt.{Uri.EscapeDataString(endUtc)}" +
            "&order=occurred_at.desc&limit=1");

        // No record yet today, or the last one was a time_out -> this tap
        // is a time_in. Otherwise (last one was a time_in) -> time_out.
        var lastEventType = todayRows.Count > 0 ? todayRows[0]["event_type"]?.ToString() : null;
        var eventType = lastEventType == "time_in" ? "time_out" : "time_in";

        var method = request.Method == "RFID" ? "RFID" : "Manual";

        await _supabase.InsertAsync("attendance_log", new
        {
            student_id = studentId,
            event_type = eventType,
            method = method
        });

        return Json(new { recognized = true, eventType, studentName });
    }

    // Right panel of the Equipment "Borrow" screen - everything
    // currently free to take out. Duplicated here (rather than calling
    // ItemLendingController.AvailableItems) because this controller has
    // to stay [AllowAnonymous] for the public kiosk, while
    // ItemLendingController sits behind the normal staff-login
    // AuthorizeFilter (see Program.cs).
    [HttpGet]
    public async Task<IActionResult> EquipmentAvailable()
    {
        var rows = await _supabase.GetAsync(
            "items", "select=id,name,item_code,category&is_active=eq.true&status=eq.Available&order=name.asc");

        var items = rows.Select(r => new
        {
            id = ((JsonElement)r["id"]!).GetInt64(),
            name = r["name"]?.ToString() ?? "",
            itemCode = r["item_code"]?.ToString() ?? "",
            category = r["category"]?.ToString() ?? ""
        });

        return Json(items);
    }

    // Right panel of the Equipment "Return" screen - whatever the
    // identified student currently has out (borrow_session_items rows
    // still "Borrowed", across any of their sessions), flattened to one
    // row per item with the borrow_session_items id the Return call
    // itself needs. Fetches the student's sessions with items embedded,
    // same pattern as ItemLendingController.ShapeSession, and filters
    // to just the still-out items in C# - PostgREST can embed
    // borrow_session_items under borrow_sessions, but filtering the
    // embedded rows by status would also hide any OTHER item in that
    // same session this call still needs to see returned state for.
    [HttpGet]
    public async Task<IActionResult> EquipmentBorrowed(long studentId)
    {
        if (studentId <= 0)
        {
            return Json(Array.Empty<object>());
        }

        var rows = await _supabase.GetAsync(
            "borrow_sessions",
            $"select=id,borrow_session_items(id,status,items(name,item_code))&student_id=eq.{studentId}&order=time_out.desc");

        var borrowed = new List<object>();
        foreach (var row in rows)
        {
            if (row["borrow_session_items"] is not JsonElement itemsEl || itemsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var si in itemsEl.EnumerateArray())
            {
                var status = si.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                if (status != "Borrowed") continue;

                var sessionItemId = si.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                string name = "", itemCode = "";
                if (si.TryGetProperty("items", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object)
                {
                    name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    itemCode = itemEl.TryGetProperty("item_code", out var codeEl) ? codeEl.GetString() ?? "" : "";
                }
                borrowed.Add(new { sessionItemId, name, itemCode });
            }
        }

        return Json(borrowed);
    }

    // Self-serve borrow (see chat - stands in for what used to be only
    // possible from Admin > Item Lending's "Log a Borrow"). Same
    // availability re-check and session/items insert as
    // ItemLendingController.CreateManual, just always tied to a real
    // student_id (no staff/teacher borrower option at the kiosk) and
    // identified by RFID tap or LRN instead of a staff member typing it.
    [HttpPost]
    public async Task<IActionResult> CreateEquipmentBorrow([FromForm] long studentId, [FromForm] List<long> itemIds, [FromForm] string? method)
    {
        if (studentId <= 0)
        {
            return BadRequest(new { error = "Identify yourself first." });
        }
        if (itemIds == null || itemIds.Count == 0)
        {
            return BadRequest(new { error = "Select at least one item to borrow." });
        }

        var students = await _supabase.GetAsync(
            "students", $"select=id,is_active&id=eq.{studentId}&limit=1");
        var isActive = students.Count > 0 && students[0]["is_active"] is JsonElement activeEl
            && activeEl.ValueKind == JsonValueKind.True;
        if (!isActive)
        {
            return NotFound(new { error = "Student record not found." });
        }

        var idList = string.Join(",", itemIds);
        var itemRows = await _supabase.GetAsync(
            "items", $"select=id,name,status&id=in.({idList})&is_active=eq.true");
        if (itemRows.Count != itemIds.Count)
        {
            return BadRequest(new { error = "One or more selected items could not be found." });
        }
        var alreadyBorrowed = itemRows
            .Where(r => r["status"]?.ToString() == "Borrowed")
            .Select(r => r["name"]?.ToString())
            .ToList();
        if (alreadyBorrowed.Count > 0)
        {
            return Conflict(new { error = $"Already borrowed: {string.Join(", ", alreadyBorrowed)}." });
        }

        var session = await _supabase.InsertAsync("borrow_sessions", new
        {
            student_id = studentId,
            method = method == "RFID" ? "RFID" : "Manual",
            time_out = DateTime.UtcNow
        });
        if (session == null || session["id"] is not JsonElement sessionIdEl)
        {
            return StatusCode(500, new { error = "Could not create the borrow session." });
        }
        var sessionId = sessionIdEl.GetInt64();

        foreach (var itemId in itemIds)
        {
            await _supabase.InsertAsync("borrow_session_items", new
            {
                session_id = sessionId,
                item_id = itemId,
                status = "Borrowed"
            });
            await _supabase.UpdateAsync("items", $"id=eq.{itemId}", new { status = "Borrowed" });
        }

        return Json(new { success = true });
    }

    // Return, gated behind a staff PIN (see chat: borrowing is
    // self-serve and trusted the same way an attendance tap is, but a
    // return needs a staff member to actually look at the equipment
    // coming back before it's marked available again - same PIN check
    // as VerifyAdminPin/the Admin tab gate, just re-used for a
    // different action instead of unlocking the admin area). Loops the
    // same per-item Return logic as ItemLendingController.Return.
    [HttpPost]
    public async Task<IActionResult> ReturnEquipment([FromForm] string pin, [FromForm] List<long> sessionItemIds)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return Json(new { ok = false, reason = "pin" });
        }
        if (sessionItemIds == null || sessionItemIds.Count == 0)
        {
            return BadRequest(new { error = "Select at least one item to return." });
        }

        // Same per-kiosk lockout as VerifyAdminPin - see chat.
        var kioskKey = KioskIdentifier();
        var lockedFor = await LoginAttemptGuard.GetLockoutSecondsAsync(_supabase, "return_pin", kioskKey);
        if (lockedFor != null)
        {
            return Json(new { ok = false, reason = "pin", message = $"Too many failed attempts. {LoginAttemptGuard.FormatWait(lockedFor.Value)}" });
        }

        var enteredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin.Trim()))).ToLowerInvariant();
        var staffRows = await _supabase.GetAsync(
            "staff_accounts", $"select=id&is_active=eq.true&pin_hash=eq.{enteredHash}&limit=1");
        if (staffRows.Count == 0)
        {
            var justLockedFor = await LoginAttemptGuard.RecordFailureAsync(_supabase, "return_pin", kioskKey);
            return Json(new
            {
                ok = false,
                reason = "pin",
                message = justLockedFor != null
                    ? $"Too many failed attempts. {LoginAttemptGuard.FormatWait(justLockedFor.Value)}"
                    : (string?)null
            });
        }
        await LoginAttemptGuard.RecordSuccessAsync(_supabase, "return_pin", kioskKey);

        foreach (var sessionItemId in sessionItemIds)
        {
            var rows = await _supabase.GetAsync(
                "borrow_session_items", $"select=id,item_id,status&id=eq.{sessionItemId}&limit=1");
            if (rows.Count == 0 || rows[0]["status"]?.ToString() == "Returned")
            {
                continue;
            }

            var itemId = ((JsonElement)rows[0]["item_id"]!).GetInt64();
            await _supabase.UpdateAsync("borrow_session_items", $"id=eq.{sessionItemId}", new
            {
                status = "Returned",
                time_in = DateTime.UtcNow
            });
            await _supabase.UpdateAsync("items", $"id=eq.{itemId}", new { status = "Available" });
        }

        return Json(new { ok = true });
    }
}
