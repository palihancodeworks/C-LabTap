using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Models;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

public class RegisterStudentController : Controller
{
    private readonly SupabaseService _supabase;
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";
    // Create/Update stay open to Staff (routine data entry) - only
    // deleting a student record is Admin-only (see chat: destructive,
    // lab-wide impact, unlike an edit which can just be edited again).
    private bool IsAdmin => string.Equals(User.FindFirst(ClaimTypes.Role)?.Value, "Admin", StringComparison.OrdinalIgnoreCase);

    public RegisterStudentController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        if (IsAjax) return PartialView("_Content");
        ViewData["Title"] = "Register Student";
        ViewData["Active"] = "RegisterStudent";
        return View();
    }

    // "Pearl10" (section name immediately followed by the grade number,
    // no separator) - the display format used across the admin pages.
    private static string? BuildGradeSectionDisplay(int? grade, string? section)
    {
        var sectionName = string.IsNullOrWhiteSpace(section) ? null : section.Trim();
        if (sectionName != null && grade.HasValue) return $"{sectionName}{grade.Value}";
        if (grade.HasValue) return $"Grade {grade.Value}";
        return null;
    }

    // Feeds the "Recently Registered" table, paginated server-side.
    // Only is_active students are listed - a "deleted" student is really
    // just marked inactive (see Delete below) and drops out of this list
    // until an admin brings them back from the Archived tab (Activate
    // below) - the Logbook no longer reactivates them on a tap (see
    // chat: archived = transferred out, their card/LRN just stops
    // working at the kiosk now).
    [HttpGet]
    public async Task<IActionResult> List(string? search, string? dateStart, string? dateEnd, string? grade, string? section, string? rfid, string? view, string? removedDate, string? gender, string? category, int page = 1, int pageSize = 6)
    {
        try
        {
        // See SuspiciousInputDetector - flags SQL-looking text typed
        // into this search box and pings ntfy (see chat).
        await SuspiciousInputDetector.CheckAndAlertAsync(search, "Register Student search");

        // "registered" (default) lists active students; "archived" lists
        // soft-deleted ones (is_active = false) so they can be found by
        // name/LRN and reactivated via Activate below (see chat - this is
        // the Archived tab, not a separate table: same row, same id, same
        // history either way).
        var isActiveFilter = view == "archived" ? "false" : "true";
        var query = $"select=id,full_name,lrn,rfid_tag,created_at,archived_at,grade_level,section,gender,class_category&is_active=eq.{isActiveFilter}";

        // Date Start/End filter by registration date (created_at), same
        // local-to-UTC day-boundary handling as Attendance's own date
        // filter.
        if (DateTime.TryParse(dateStart, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var startDate))
        {
            var startUtc = startDate.Date.ToUniversalTime().ToString("o");
            query += $"&created_at=gte.{Uri.EscapeDataString(startUtc)}";
        }
        if (DateTime.TryParse(dateEnd, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var endDate))
        {
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime().ToString("o");
            query += $"&created_at=lt.{Uri.EscapeDataString(endUtc)}";
        }
        // Archived tab only: "Date Removed" - students archived on that
        // local calendar day (same local-to-UTC day boundaries as above).
        if (view == "archived" &&
            DateTime.TryParse(removedDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var removedDay))
        {
            var removedStartUtc = removedDay.Date.ToUniversalTime().ToString("o");
            var removedEndUtc = removedDay.Date.AddDays(1).ToUniversalTime().ToString("o");
            query += $"&archived_at=gte.{Uri.EscapeDataString(removedStartUtc)}";
            query += $"&archived_at=lt.{Uri.EscapeDataString(removedEndUtc)}";
        }
        if (int.TryParse(grade, out var gradeFilter))
        {
            query += $"&grade_level=eq.{gradeFilter}";
        }
        if (!string.IsNullOrWhiteSpace(section) && section != "All")
        {
            // ilike with no % wildcards still does a case-insensitive
            // exact match - fine here since this comes from a dropdown
            // of existing section names, not free text.
            query += $"&section=ilike.{Uri.EscapeDataString(section.Trim())}";
        }
        if (NormalizeGender(gender) is string genderFilter)
        {
            query += $"&gender=eq.{genderFilter}";
        }
        if (NormalizeCategory(category) is string categoryFilter)
        {
            query += $"&class_category=eq.{categoryFilter}";
        }
        if (rfid == "Linked")
        {
            query += "&rfid_tag=not.is.null";
        }
        else if (rfid == "NotLinked")
        {
            query += "&rfid_tag=is.null";
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = Uri.EscapeDataString(PostgrestFilterHelper.StripReservedChars(search.Trim()));
            query += $"&or=(full_name.ilike.*{term}*,lrn.ilike.*{term}*)";
        }

        // Archived tab: most recently removed first (students removed
        // before "Date Removed" was tracked have no date and go last).
        query += view == "archived"
            ? "&order=archived_at.desc.nullslast,created_at.desc"
            : "&order=created_at.desc";

        var (rows, total) = await _supabase.GetPagedAsync("students", query, page, pageSize);

        var students = rows.Select(row =>
        {
            var id = ((JsonElement)row["id"]!).GetInt64();
            var fullName = row["full_name"]?.ToString() ?? "";
            var lrn = row["lrn"]?.ToString() ?? "";
            var rfidTag = row["rfid_tag"]?.ToString();
            var sectionName = row["section"]?.ToString();

            int? studentGrade = null;
            if (row.TryGetValue("grade_level", out var gradeVal) &&
                gradeVal is JsonElement gradeEl &&
                gradeEl.ValueKind == JsonValueKind.Number)
            {
                studentGrade = gradeEl.GetInt32();
            }

            var gradeSection = BuildGradeSectionDisplay(studentGrade, sectionName) ?? "—";

            var registeredOn = "";
            if (row["created_at"]?.ToString() is string createdRaw &&
                DateTime.TryParse(createdRaw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var createdAt))
            {
                registeredOn = createdAt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
            }

            // "Date Removed" on the Archived tab - when Delete archived them.
            var removedOn = "";
            if (row.TryGetValue("archived_at", out var archivedVal) &&
                archivedVal?.ToString() is string archivedRaw &&
                !string.IsNullOrWhiteSpace(archivedRaw) &&
                DateTime.TryParse(archivedRaw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var archivedAt))
            {
                removedOn = archivedAt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
            }

            return new
            {
                id,
                fullName,
                lrn,
                grade = studentGrade,
                section = sectionName,
                gender = row.TryGetValue("gender", out var genderVal) && genderVal is JsonElement genderEl && genderEl.ValueKind == JsonValueKind.String ? genderEl.GetString() : null,
                classCategory = row.TryGetValue("class_category", out var categoryVal) && categoryVal is JsonElement categoryEl && categoryEl.ValueKind == JsonValueKind.String ? categoryEl.GetString() : null,
                gradeSection,
                rfidTag,
                rfidLinked = !string.IsNullOrWhiteSpace(rfidTag),
                registeredOn,
                removedOn
            };
        }).ToList();

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 1;
        if (totalPages < 1) totalPages = 1;

        return Json(new { rows = students, total, page, pageSize, totalPages });
        }
        catch (HttpRequestException)
        {
            // Same reasoning as LogbookController.LookupStudent - a malformed
            // or rejected search value should look like "no results", not
            // crash with a generic error page (see chat: "serach for more
            // before i commit").
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 1 });
        }
    }

    // Powers the "Students Not Linked" stat card: active students with
    // no RFID tag on file yet.
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var (_, notLinked) = await _supabase.GetPagedAsync(
            "students", "select=id&is_active=eq.true&rfid_tag=is.null", 1, 1);
        var (_, totalRegistered) = await _supabase.GetPagedAsync(
            "students", "select=id&is_active=eq.true", 1, 1);
        return Json(new { notLinked, totalRegistered });
    }

    // Distinct grade -> section pairs across active students, grouped
    // client-side (see chat: Section's autocomplete should only offer
    // sections that already exist under the Grade the user just
    // picked, instead of every section ever used regardless of grade).
    // One small payload covers every grade so switching the Grade chip
    // re-filters instantly without a fetch per click.
    [HttpGet]
    public async Task<IActionResult> Sections()
    {
        // class_category included too - Grade 7 STE and Grade 7 Regular
        // don't share sections (see chat: picking STE + 7 offered
        // Regular-7's sections as well, which was wrong), so the
        // suggestion list has to be scoped by grade AND category, not
        // grade alone.
        var rows = await _supabase.GetAsync(
            "students", "select=grade_level,class_category,section&is_active=eq.true&order=grade_level,section");
        var seen = new HashSet<string>();
        var result = new List<object>();
        foreach (var row in rows)
        {
            var grade = row.GetValueOrDefault("grade_level")?.ToString();
            var category = row.GetValueOrDefault("class_category")?.ToString();
            var section = row.GetValueOrDefault("section")?.ToString();
            if (string.IsNullOrWhiteSpace(grade) || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(section)) continue;
            var key = grade + "|" + category.ToLowerInvariant() + "|" + section.ToLowerInvariant();
            if (!seen.Add(key)) continue;
            result.Add(new { grade, category, section });
        }
        return Json(result);
    }

    // Same person can't be registered twice under a different LRN: a
    // case-insensitive exact name match against ACTIVE students blocks
    // the save (removed/archived students don't count, so they can be
    // registered again). excludeId skips the student being edited or
    // reactivated. Names only contain letters, spaces . ' - (see
    // CheckLengths), so ilike has no wildcard characters to worry about.
    private async Task<bool> NameTakenAsync(string fullName, long? excludeId)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(fullName.Trim(), @"\s+", " ");
        var query = $"select=id&is_active=eq.true&full_name=ilike.{Uri.EscapeDataString(name)}&limit=2";
        if (excludeId.HasValue) query += $"&id=neq.{excludeId.Value}";
        var rows = await _supabase.GetAsync("students", query);
        return rows.Count > 0;
    }

    private static string DuplicateNameMessage(string fullName)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(fullName.Trim(), @"\s+", " ");
        return "\"" + name + "\" is already registered. The same student can't be registered twice.";
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromForm] StudentRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Lrn))
        {
            return BadRequest(new { error = "Full name and LRN are required." });
        }
        if (CheckLengths(request) is string lengthError)
        {
            return BadRequest(new { error = lengthError });
        }

        var lrn = request.Lrn.Trim();

        try
        {
            // A soft-deleted student (is_active = false - see Delete below)
            // keeps their row and their LRN, so a plain insert here would
            // hit Postgres's unique constraint on lrn and come back as a
            // confusing "already in use" conflict even though nobody else
            // is using it. Instead, mirror what the Logbook kiosk already
            // does when it sees a soft-deleted student's LRN/RFID again
            // (see LogbookController.RecordAttendance): reactivate that
            // same row with the newly submitted details rather than
            // inserting a second one.
            var existing = await _supabase.GetAsync(
                "students", $"select=id,is_active&lrn=eq.{Uri.EscapeDataString(lrn)}&limit=1");

            if (existing.Count > 0)
            {
                var existingId = ((JsonElement)existing[0]["id"]!).GetInt64();
                var existingActive = true;
                if (existing[0].TryGetValue("is_active", out var activeVal) &&
                    activeVal is JsonElement activeEl &&
                    (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False))
                {
                    existingActive = activeEl.GetBoolean();
                }

                if (existingActive)
                {
                    return Conflict(new { error = "Could not register this student - the LRN or RFID tag may already be in use." });
                }

                if (await NameTakenAsync(request.FullName, existingId))
                {
                    return Conflict(new { error = DuplicateNameMessage(request.FullName) });
                }

                await _supabase.UpdateAsync("students", $"id=eq.{existingId}", new
                {
                    full_name = request.FullName.Trim(),
                    lrn,
                    grade_level = request.Grade,
                    section = string.IsNullOrWhiteSpace(request.Section) ? null : request.Section.Trim(),
                    gender = NormalizeGender(request.Gender),
                    class_category = NormalizeCategory(request.ClassCategory),
                    rfid_tag = string.IsNullOrWhiteSpace(request.RfidTag) ? null : request.RfidTag.Trim(),
                    is_active = true,
                    archived_at = (DateTime?)null
                });
                return Json(new { success = true, reactivated = true, gradeSection = BuildGradeSectionDisplay(request.Grade, request.Section) });
            }

            if (await NameTakenAsync(request.FullName, null))
            {
                return Conflict(new { error = DuplicateNameMessage(request.FullName) });
            }

            await _supabase.InsertAsync("students", new
            {
                full_name = request.FullName.Trim(),
                lrn,
                grade_level = request.Grade,
                section = string.IsNullOrWhiteSpace(request.Section) ? null : request.Section.Trim(),
                gender = NormalizeGender(request.Gender),
                class_category = NormalizeCategory(request.ClassCategory),
                rfid_tag = string.IsNullOrWhiteSpace(request.RfidTag) ? null : request.RfidTag.Trim()
            });
            return Json(new { success = true, gradeSection = BuildGradeSectionDisplay(request.Grade, request.Section) });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not register this student - the LRN or RFID tag may already be in use." });
        }
    }

    // Same limits as the form (maxlength on the inputs) - enforced here
    // too so nothing longer can be saved by bypassing the page.
    private static string? CheckLengths(StudentRegistrationRequest request)
    {
        var fullName = request.FullName?.Trim() ?? "";
        // 50 for the name itself + room for a suffix like " VIII".
        if (fullName.Length > 56) return "Full name can be at most 50 characters (plus suffix).";
        if (!System.Text.RegularExpressions.Regex.IsMatch(fullName, @"^[\p{L}\p{M} .'\-]+$"))
            return "Names can only contain letters, spaces, and . - '";
        if ((request.Lrn?.Trim().Length ?? 0) != 12 || !(request.Lrn ?? "").Trim().All(char.IsDigit)) return "LRN must be exactly 12 digits.";
        if ((request.Section?.Trim().Length ?? 0) > 30) return "Section name can be at most 30 characters.";
        if (NormalizeGender(request.Gender) is null) return "Please choose a gender.";
        if (request.Grade is null) return "Please choose a grade.";
        if (NormalizeCategory(request.ClassCategory) is null) return "Please choose a class category (Regular or STE).";
        if (string.IsNullOrWhiteSpace(request.Section)) return "Please enter a section.";
        return null;
    }

    // Only "Male"/"Female" are stored (the column has a check constraint);
    // anything else, including blank, is saved as null.
    private static string? NormalizeGender(string? gender)
    {
        var g = gender?.Trim();
        if (string.Equals(g, "Male", StringComparison.OrdinalIgnoreCase)) return "Male";
        if (string.Equals(g, "Female", StringComparison.OrdinalIgnoreCase)) return "Female";
        return null;
    }

    // Only "STE"/"Regular" are stored (the column has a check constraint);
    // anything else, including blank, is treated as no category.
    private static string? NormalizeCategory(string? category)
    {
        var c = category?.Trim();
        if (string.Equals(c, "STE", StringComparison.OrdinalIgnoreCase)) return "STE";
        if (string.Equals(c, "Regular", StringComparison.OrdinalIgnoreCase)) return "Regular";
        return null;
    }

    // Edits an already-registered student's info (name, LRN, grade/section,
    // RFID tag).
    [HttpPost]
    public async Task<IActionResult> Update([FromForm] StudentRegistrationRequest request)
    {
        if (request.Id is null || request.Id <= 0)
        {
            return BadRequest(new { error = "Missing student id." });
        }
        if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Lrn))
        {
            return BadRequest(new { error = "Full name and LRN are required." });
        }
        if (CheckLengths(request) is string lengthError)
        {
            return BadRequest(new { error = lengthError });
        }

        try
        {
            if (await NameTakenAsync(request.FullName, request.Id.Value))
            {
                return Conflict(new { error = DuplicateNameMessage(request.FullName) });
            }

            await _supabase.UpdateAsync("students", $"id=eq.{request.Id.Value}", new
            {
                full_name = request.FullName.Trim(),
                lrn = request.Lrn.Trim(),
                grade_level = request.Grade,
                section = string.IsNullOrWhiteSpace(request.Section) ? null : request.Section.Trim(),
                gender = NormalizeGender(request.Gender),
                class_category = NormalizeCategory(request.ClassCategory),
                rfid_tag = string.IsNullOrWhiteSpace(request.RfidTag) ? null : request.RfidTag.Trim()
            });
            return Json(new { success = true, gradeSection = BuildGradeSectionDisplay(request.Grade, request.Section) });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not update this student - the LRN or RFID tag may already be in use." });
        }
    }

    // Soft delete: marks the student inactive instead of removing the row.
    // Their attendance history is kept (just hidden from the Attendance
    // list while inactive - see AttendanceController). This means
    // "transferred out," not "temporarily away" - their RFID card and
    // LRN stop working at the Logbook kiosk (see
    // LogbookController.RecordAttendance/LookupStudent) until an admin
    // deliberately brings them back via Activate below.
    [HttpPost]
    public async Task<IActionResult> Delete([FromForm] long id)
    {
        if (!IsAdmin)
        {
            return StatusCode(403, new { error = "Only an Admin can delete a student record." });
        }

        if (id <= 0)
        {
            return BadRequest(new { error = "Missing student id." });
        }

        try
        {
            await _supabase.UpdateAsync("students", $"id=eq.{id}", new { is_active = false, archived_at = DateTime.UtcNow });
            return Json(new { success = true });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not delete this student." });
        }
    }

    // Reactivates a soft-deleted student from the Archived tab - same
    // row, same id, so their past attendance/lending history is already
    // there (see chat: "he returns ... press activate ... his past
    // history will come back"). Mirrors the reactivation Create() already
    // does when the same LRN is re-submitted, just reachable by searching
    // a name instead of needing the LRN on hand.
    [HttpPost]
    public async Task<IActionResult> Activate([FromForm] long id)
    {
        if (id <= 0)
        {
            return BadRequest(new { error = "Missing student id." });
        }

        try
        {
            await _supabase.UpdateAsync("students", $"id=eq.{id}", new { is_active = true, archived_at = (DateTime?)null });
            return Json(new { success = true });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not activate this student." });
        }
    }
}
