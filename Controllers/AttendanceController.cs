using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

public class AttendanceController : Controller
{
    private readonly SupabaseService _supabase;
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    public AttendanceController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        if (IsAjax) return PartialView("_Content");

        ViewData["Title"] = "Student Attendance";
        ViewData["Active"] = "Attendance";
        return View();
    }

    // Powers the Section filter dropdown with the sections that actually
    // exist among registered students, instead of a static/hardcoded list.
    [HttpGet]
    public async Task<IActionResult> FilterOptions()
    {
        var rows = await _supabase.GetAsync("students", "select=section");
        var names = rows
            .Select(r => r["section"]?.ToString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Json(new { sections = names });
    }

    // Powers the two header stat cards. "Entries Today" counts today's
    // time-in events; "Active Occupants" counts students whose most
    // recent event today is a time-in with no time-out after it yet -
    // i.e. still checked in as of right now. Both are scoped to today
    // only (a student who timed in yesterday and never timed out isn't
    // counted as an occupant here - there's no multi-day session
    // tracking in this schema). Soft-deleted students are excluded,
    // same as the List() action above.
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var today = DateTime.Now.Date;
        var startUtc = today.ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var query = "select=student_id,event_type,occurred_at,students(is_active)" +
                    $"&occurred_at=gte.{Uri.EscapeDataString(startUtc)}" +
                    $"&occurred_at=lt.{Uri.EscapeDataString(endUtc)}" +
                    "&order=occurred_at.asc&limit=5000";

        var rows = await _supabase.GetAsync("attendance_log", query);

        var entriesToday = 0;
        // Rows arrive oldest-first, so the last write for a given
        // student id in this loop is that student's latest event today.
        var latestEventByStudent = new Dictionary<long, string>();

        foreach (var row in rows)
        {
            var studentActive = true;
            if (row.TryGetValue("students", out var studentVal) &&
                studentVal is JsonElement studentEl &&
                studentEl.ValueKind == JsonValueKind.Object &&
                studentEl.TryGetProperty("is_active", out var activeEl) &&
                (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False))
            {
                studentActive = activeEl.GetBoolean();
            }
            if (!studentActive) continue;

            var eventType = row["event_type"]?.ToString() ?? "";
            if (eventType == "time_in") entriesToday++;

            if (row.TryGetValue("student_id", out var studentIdVal) &&
                studentIdVal is JsonElement studentIdEl &&
                studentIdEl.ValueKind == JsonValueKind.Number)
            {
                latestEventByStudent[studentIdEl.GetInt64()] = eventType;
            }
        }

        var activeOccupants = latestEventByStudent.Values.Count(v => v == "time_in");

        return Json(new { entriesToday, activeOccupants });
    }

    // Feeds the attendance table. Date range/method/status are pushed down
    // as PostgREST filters on attendance_log itself; grade/section/search
    // depend on the joined student, so those are applied here in C# after
    // the embedded rows come back - and so is the page/pageSize slice,
    // since it has to happen after all the other filtering. Rows whose
    // student is currently inactive (soft-deleted - see
    // RegisterStudentController.Delete) are dropped: a deleted student's
    // attendance history is kept in the database but hidden from this
    // list until they show up again at the Logbook and get reactivated.
    [HttpGet]
    public async Task<IActionResult> List(string? search, string? dateStart, string? dateEnd, string? grade, string? section, string? gender, string? category, string? method, string? status, int page = 1, int pageSize = 8)
    {
        var query = "select=event_type,method,occurred_at,students(full_name,lrn,grade_level,section,gender,class_category,is_active)" +
                    "&order=occurred_at.desc&limit=2000";

        if (DateTime.TryParse(dateStart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
        {
            var startUtc = startDate.Date.ToUniversalTime().ToString("o");
            query += $"&occurred_at=gte.{Uri.EscapeDataString(startUtc)}";
        }
        if (DateTime.TryParse(dateEnd, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime().ToString("o");
            query += $"&occurred_at=lt.{Uri.EscapeDataString(endUtc)}";
        }
        if (!string.IsNullOrWhiteSpace(method) && method != "All")
        {
            query += $"&method=eq.{Uri.EscapeDataString(method)}";
        }
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
        {
            query += $"&event_type=eq.{Uri.EscapeDataString(status)}";
        }

        var rows = await _supabase.GetAsync("attendance_log", query);

        int? gradeFilter = int.TryParse(grade, out var gradeNum) ? gradeNum : null;
        string? sectionFilter = string.IsNullOrWhiteSpace(section) || section == "All" ? null : section.Trim();
        string? searchFilter = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        string? genderFilter = string.IsNullOrWhiteSpace(gender) || gender == "All" ? null : gender.Trim();
        string? categoryFilter = string.Equals(category?.Trim(), "STE", StringComparison.OrdinalIgnoreCase) ? "STE"
            : string.Equals(category?.Trim(), "Regular", StringComparison.OrdinalIgnoreCase) ? "Regular" : null;

        var records = new List<object>();

        foreach (var row in rows)
        {
            var fullName = "";
            var lrn = "";
            string? sectionName = null;
            int? studentGrade = null;
            string? studentGender = null;
            string? studentCategory = null;
            var studentActive = true;

            if (row.TryGetValue("students", out var studentVal) &&
                studentVal is JsonElement studentEl &&
                studentEl.ValueKind == JsonValueKind.Object)
            {
                if (studentEl.TryGetProperty("full_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    fullName = nameEl.GetString() ?? "";
                }
                if (studentEl.TryGetProperty("lrn", out var lrnEl) && lrnEl.ValueKind == JsonValueKind.String)
                {
                    lrn = lrnEl.GetString() ?? "";
                }
                if (studentEl.TryGetProperty("section", out var secNameEl) && secNameEl.ValueKind == JsonValueKind.String)
                {
                    sectionName = secNameEl.GetString();
                }
                if (studentEl.TryGetProperty("gender", out var genderEl) && genderEl.ValueKind == JsonValueKind.String)
                {
                    studentGender = genderEl.GetString();
                }
                if (studentEl.TryGetProperty("class_category", out var categoryEl) && categoryEl.ValueKind == JsonValueKind.String)
                {
                    studentCategory = categoryEl.GetString();
                }
                if (studentEl.TryGetProperty("grade_level", out var gradeEl) && gradeEl.ValueKind == JsonValueKind.Number)
                {
                    studentGrade = gradeEl.GetInt32();
                }
                if (studentEl.TryGetProperty("is_active", out var activeEl) &&
                    (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False))
                {
                    studentActive = activeEl.GetBoolean();
                }
            }
            else
            {
                // No joined student row at all (shouldn't normally happen -
                // students are soft-deleted now, not removed) - skip it
                // rather than show a blank record.
                continue;
            }

            if (!studentActive) continue;
            if (gradeFilter.HasValue && studentGrade != gradeFilter.Value) continue;
            if (sectionFilter != null && !string.Equals(sectionName, sectionFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (genderFilter != null && !string.Equals(studentGender, genderFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (categoryFilter != null && !string.Equals(studentCategory, categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (searchFilter != null &&
                fullName.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                lrn.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var gradeSection = "—";
            if (sectionName != null && studentGrade.HasValue) gradeSection = $"{sectionName}{studentGrade.Value}";
            else if (studentGrade.HasValue) gradeSection = $"Grade {studentGrade.Value}";

            var eventType = row["event_type"]?.ToString();
            var statusDisplay = eventType == "time_in" ? "Time In" : eventType == "time_out" ? "Time Out" : (eventType ?? "");

            var timestamp = "";
            if (row["occurred_at"]?.ToString() is string occurredRaw &&
                DateTime.TryParse(occurredRaw, null,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var occurredAt))
            {
                timestamp = occurredAt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
            }

            records.Add(new
            {
                fullName,
                lrn,
                gradeSection,
                gender = studentGender,
                classCategory = studentCategory,
                method = row["method"]?.ToString() ?? "",
                status = statusDisplay,
                timestamp
            });
        }

        var total = records.Count;
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        var pageRows = records.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        if (totalPages < 1) totalPages = 1;

        return Json(new { rows = pageRows, total, page, pageSize, totalPages });
    }
}
