using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

// Report page: a read-only rollup of the quick-view stat cards already
// shown on Attendance/Item Lending/Register Item/Register Student
// (minus "Active Occupants" - a live headcount, not something that
// belongs in a printed report - see chat), plus day-by-day trend data
// for the charts/table. Nothing here writes anything.
public class ReportController : Controller
{
    private readonly SupabaseService _supabase;
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    public ReportController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        if (IsAjax) return PartialView("_Content");

        ViewData["Title"] = "Report";
        ViewData["Active"] = "Report";
        return View();
    }

    // The six quick-view numbers, re-queried directly here (same
    // underlying queries as each page's own Stats() action) so the
    // Report page doesn't depend on hitting four different controllers.
    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime().ToString("o");
        var todayEndUtc = DateTime.Now.Date.AddDays(1).ToUniversalTime().ToString("o");

        // Same filter as Attendance's own Stats() - only counts a
        // time_in whose student is still active, so this card never
        // disagrees with the one on the Attendance page itself.
        var attendanceToday = await _supabase.GetAsync(
            "attendance_log",
            $"select=event_type,students(is_active)&event_type=eq.time_in&occurred_at=gte.{Uri.EscapeDataString(todayStartUtc)}&occurred_at=lt.{Uri.EscapeDataString(todayEndUtc)}&limit=5000");
        var entriesToday = attendanceToday.Count(IsActiveStudentRow);

        var (_, borrowedToday) = await _supabase.GetPagedAsync(
            "borrow_session_items",
            $"select=id&created_at=gte.{Uri.EscapeDataString(todayStartUtc)}&created_at=lt.{Uri.EscapeDataString(todayEndUtc)}",
            1, 1);

        var (_, currentlyBorrowed) = await _supabase.GetPagedAsync(
            "items", "select=id&is_active=eq.true&status=eq.Borrowed", 1, 1);

        var (_, itemsRegistered) = await _supabase.GetPagedAsync(
            "items", "select=id&is_active=eq.true", 1, 1);

        var (_, studentsNotLinked) = await _supabase.GetPagedAsync(
            "students", "select=id&is_active=eq.true&rfid_tag=is.null", 1, 1);

        var (_, totalStudents) = await _supabase.GetPagedAsync(
            "students", "select=id&is_active=eq.true", 1, 1);

        return Json(new
        {
            entriesToday,
            borrowedToday,
            currentlyBorrowed,
            itemsRegistered,
            studentsNotLinked,
            totalStudents
        });
    }

    // One row per day across the selected Date Start/Date End range
    // (both inclusive), with entries/borrowed/returned counts - backs
    // both the trend charts and the printable table (and its Excel
    // export, built client-side from this same JSON - see
    // Views/Report/_Content.cshtml). Capped at a 90-day span; two
    // queries total (not one per day) with the grouping done in C#
    // against each row's own local date.
    [HttpGet]
    public async Task<IActionResult> Table(string? dateStart = null, string? dateEnd = null, string? gender = null, string? grade = null, string? section = null, string? status = null, string? item = null)
    {
        var (rangeStart, rangeEndExclusive, _) = ResolveDateRange(dateStart, dateEnd);
        var genderFilter = NormalizeGender(gender);
        var gradeFilter = NormalizeGrade(grade);
        var sectionFilter = NormalizeSection(section);
        var statusFilter = NormalizeStatus(status);
        var itemFilter = NormalizeItem(item);

        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = rangeEndExclusive.ToUniversalTime().ToString("o");

        var entryRows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=occurred_at,students(is_active,gender,grade_level,section)&event_type=eq.time_in&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000"))
            .Where(IsActiveStudentRow)
            .Where(r => MatchesDemographic(GetEmbeddedObject(r, "students"), genderFilter, gradeFilter, sectionFilter))
            .ToList();

        // Status/Item (see chat: "wht if i only want borrow or return, or
        // specific item") are borrow-only axes - they just AND onto these
        // two queries like gender/grade/section do, and have no bearing on
        // entryRows above (attendance has no status or item). Repeating
        // "status=eq.X" is a second PostgREST filter on the same column,
        // ANDed with whatever's already there - harmless when it agrees
        // (Returned + Returned) and correctly yields nothing when it can't
        // (Returned + Borrowed, e.g. Status=Borrowed on the returned-count
        // query below).
        var borrowQuery = $"select=created_at,items(name),borrow_sessions(students(gender,grade_level,section))&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000";
        if (statusFilter != null) borrowQuery += $"&status=eq.{statusFilter}";
        var borrowRows = (await _supabase.GetAsync("borrow_session_items", borrowQuery))
            .Where(r => MatchesDemographic(GetSessionStudent(r), genderFilter, gradeFilter, sectionFilter))
            .Where(r => MatchesItem(GetEmbeddedObject(r, "items"), itemFilter))
            .ToList();

        var returnQuery = $"select=time_in,items(name),borrow_sessions(students(gender,grade_level,section))&status=eq.Returned&time_in=gte.{Uri.EscapeDataString(startUtc)}&time_in=lt.{Uri.EscapeDataString(endUtc)}&limit=10000";
        if (statusFilter != null) returnQuery += $"&status=eq.{statusFilter}";
        var returnRows = (await _supabase.GetAsync("borrow_session_items", returnQuery))
            .Where(r => MatchesDemographic(GetSessionStudent(r), genderFilter, gradeFilter, sectionFilter))
            .Where(r => MatchesItem(GetEmbeddedObject(r, "items"), itemFilter))
            .ToList();

        var entriesByDay = CountByLocalDate(entryRows, "occurred_at");
        var borrowedByDay = CountByLocalDate(borrowRows, "created_at");
        var returnedByDay = CountByLocalDate(returnRows, "time_in");

        var table = new List<object>();
        for (var d = rangeStart; d < rangeEndExclusive; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            table.Add(new
            {
                date = key,
                label = d.ToString("MMM d"),
                entries = entriesByDay.GetValueOrDefault(key, 0),
                borrowed = borrowedByDay.GetValueOrDefault(key, 0),
                returned = returnedByDay.GetValueOrDefault(key, 0)
            });
        }

        return Json(table);
    }

    // Most-borrowed items within the selected date range (by unit-
    // borrow count, same unit counted once per time it went out) -
    // backs the "Most Borrowed Items" chart, so it follows the same
    // Date Start/Date End filter as every other chart on the page.
    // Embeds the item's name so a deleted/deactivated item still shows
    // its last-known name instead of dropping out of the count.
    [HttpGet]
    public async Task<IActionResult> TopItems(int limit = 6, string? dateStart = null, string? dateEnd = null, string? gender = null, string? grade = null, string? section = null, string? status = null, string? item = null)
    {
        if (limit < 1) limit = 1;
        if (limit > 20) limit = 20;
        var (rangeStart, rangeEndExclusive, _) = ResolveDateRange(dateStart, dateEnd);
        var genderFilter = NormalizeGender(gender);
        var gradeFilter = NormalizeGrade(grade);
        var sectionFilter = NormalizeSection(section);
        var statusFilter = NormalizeStatus(status);
        var itemFilter = NormalizeItem(item);

        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = rangeEndExclusive.ToUniversalTime().ToString("o");

        var topItemsQuery = $"select=items(name),borrow_sessions(students(gender,grade_level,section))&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000";
        if (statusFilter != null) topItemsQuery += $"&status=eq.{statusFilter}";
        var rows = (await _supabase.GetAsync("borrow_session_items", topItemsQuery))
            .Where(r => MatchesDemographic(GetSessionStudent(r), genderFilter, gradeFilter, sectionFilter))
            .Where(r => MatchesItem(GetEmbeddedObject(r, "items"), itemFilter));

        var counts = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            if (row.TryGetValue("items", out var itemVal) &&
                itemVal is JsonElement itemEl &&
                itemEl.ValueKind == JsonValueKind.Object &&
                itemEl.TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString() ?? "Unknown";
                counts[name] = counts.GetValueOrDefault(name, 0) + 1;
            }
        }

        var top = counts
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => new { name = kv.Key, count = kv.Value });

        return Json(top);
    }

    // Entries (time-ins) per hour of the day within the selected date
    // range, all days combined - backs the "Busiest Hours" chart, so
    // the lab can see when it actually fills up. Same active-student
    // filter and local-time bucketing as the rest of this page.
    [HttpGet]
    public async Task<IActionResult> PeakHours(string? dateStart = null, string? dateEnd = null, string? gender = null, string? grade = null, string? section = null)
    {
        var (rangeStart, rangeEndExclusive, _) = ResolveDateRange(dateStart, dateEnd);
        var genderFilter = NormalizeGender(gender);
        var gradeFilter = NormalizeGrade(grade);
        var sectionFilter = NormalizeSection(section);

        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = rangeEndExclusive.ToUniversalTime().ToString("o");

        var rows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=occurred_at,students(is_active,gender,grade_level,section)&event_type=eq.time_in&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000"))
            .Where(IsActiveStudentRow)
            .Where(r => MatchesDemographic(GetEmbeddedObject(r, "students"), genderFilter, gradeFilter, sectionFilter));

        var counts = new int[24];
        foreach (var row in rows)
        {
            if (!row.TryGetValue("occurred_at", out var val) || val is not JsonElement el || el.ValueKind != JsonValueKind.String) continue;
            if (!DateTime.TryParse(el.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var utc)) continue;
            counts[utc.ToLocalTime().Hour]++;
        }

        return Json(counts.Select((count, hour) => new { hour, count }));
    }

    // Full current inventory (active items only) - backs the "Inventory"
    // checkbox for Export/Print. Item Code is intentionally left out -
    // it was hidden from the UI system-wide (see chat); it still lives
    // in the database, just not in anything the admin prints or exports.
    [HttpGet]
    public async Task<IActionResult> InventoryList()
    {
        var rows = await _supabase.GetAsync(
            "items",
            "select=name,category,status&is_active=eq.true&order=category,name&limit=5000");

        var list = rows.Select(r => new
        {
            name = GetString(r, "name"),
            category = GetString(r, "category"),
            status = GetString(r, "status")
        });

        return Json(list);
    }

    // Full current student roster (active students only) - backs the
    // "Students" checkbox for Export/Print.
    [HttpGet]
    public async Task<IActionResult> StudentList(string? gender = null, string? grade = null, string? section = null)
    {
        var query = "select=full_name,lrn,grade_level,section,gender,class_category&is_active=eq.true";
        var genderFilter = NormalizeGender(gender);
        if (genderFilter != null) query += $"&gender=eq.{genderFilter}";
        if (NormalizeGrade(grade) is int gradeFilter) query += $"&grade_level=eq.{gradeFilter}";
        if (NormalizeSection(section) is string sectionFilter) query += $"&section=ilike.{Uri.EscapeDataString(sectionFilter)}";
        query += "&order=grade_level,section,full_name&limit=5000";

        var rows = await _supabase.GetAsync("students", query);

        var list = rows.Select(r => new
        {
            fullName = GetString(r, "full_name"),
            lrn = GetString(r, "lrn"),
            gradeLevel = r.TryGetValue("grade_level", out var g) && g is JsonElement ge && ge.ValueKind == JsonValueKind.Number ? ge.GetInt32().ToString() : "",
            section = GetString(r, "section"),
            gender = GetString(r, "gender"),
            classCategory = GetString(r, "class_category")
        });

        return Json(list);
    }

    // Raw RFID attendance log rows (time in / time out) within the
    // selected date range - backs the "Attendance Log" checkbox. Same
    // active-student filter as every other attendance count on this
    // page, so a deactivated student's old taps don't show up here
    // either.
    [HttpGet]
    public async Task<IActionResult> AttendanceLog(string? dateStart = null, string? dateEnd = null, string? gender = null, string? grade = null, string? section = null)
    {
        var (rangeStart, rangeEndExclusive, _) = ResolveDateRange(dateStart, dateEnd);
        var genderFilter = NormalizeGender(gender);
        var gradeFilter = NormalizeGrade(grade);
        var sectionFilter = NormalizeSection(section);

        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = rangeEndExclusive.ToUniversalTime().ToString("o");

        var rows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=event_type,method,occurred_at,students(full_name,is_active,gender,grade_level,section)&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&order=occurred_at.desc&limit=10000"))
            .Where(IsActiveStudentRow)
            .Where(r => MatchesDemographic(GetEmbeddedObject(r, "students"), genderFilter, gradeFilter, sectionFilter))
            .ToList();

        var list = rows.Select(r => new
        {
            studentName = GetNestedString(r, "students", "full_name"),
            eventType = GetString(r, "event_type"),
            method = GetString(r, "method"),
            occurredAt = GetString(r, "occurred_at")
        });

        return Json(list);
    }

    // Raw borrow/return log rows within the selected date range - backs
    // the "Borrow/Return Log" checkbox. Borrower is either a student
    // (via the session's student_id) or a staff/teacher name entered at
    // the kiosk (borrower_name, method = Staff) - never both.
    //
    // Grouped by (session, item, status, returned at) before being
    // returned, each with a quantity - see chat: "a person borrowed 80
    // chairs and those chairs are putted in the report one by one".
    // borrow_session_items has one row per physical unit (80 chairs
    // borrowed together is 80 rows, one per chair), which used to go
    // straight into the report as 80 near-identical lines. Grouping by
    // session_id rather than by the borrowed timestamp itself is
    // deliberate: create_borrow inserts those 80 rows one at a time, so
    // their created_at values land a second or so apart even though
    // they're clearly the same borrow - session_id is the one thing
    // every row from that same borrow actually shares exactly. Rows
    // split into separate groups the moment they stop being identical in
    // every other way too (e.g. 60 chairs already returned, 20 still
    // out), which is correct - those really are two different lines.
    [HttpGet]
    public async Task<IActionResult> BorrowLog(string? dateStart = null, string? dateEnd = null, string? gender = null, string? grade = null, string? section = null, string? status = null, string? item = null)
    {
        var (rangeStart, rangeEndExclusive, _) = ResolveDateRange(dateStart, dateEnd);
        var genderFilter = NormalizeGender(gender);
        var gradeFilter = NormalizeGrade(grade);
        var sectionFilter = NormalizeSection(section);
        var statusFilter = NormalizeStatus(status);
        var itemFilter = NormalizeItem(item);

        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = rangeEndExclusive.ToUniversalTime().ToString("o");

        var borrowLogQuery = $"select=session_id,status,created_at,time_in,items(name),borrow_sessions(borrower_name,students(full_name,gender,grade_level,section))&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&order=created_at.desc&limit=10000";
        if (statusFilter != null) borrowLogQuery += $"&status=eq.{statusFilter}";
        var rows = (await _supabase.GetAsync("borrow_session_items", borrowLogQuery))
            .Where(r => MatchesDemographic(GetSessionStudent(r), genderFilter, gradeFilter, sectionFilter))
            .Where(r => MatchesItem(GetEmbeddedObject(r, "items"), itemFilter));

        var expanded = rows.Select(r =>
        {
            var session = r.TryGetValue("borrow_sessions", out var sVal) && sVal is JsonElement sEl && sEl.ValueKind == JsonValueKind.Object ? sEl : (JsonElement?)null;
            string? borrowerName = null;
            if (session is JsonElement se)
            {
                if (se.TryGetProperty("borrower_name", out var bn) && bn.ValueKind == JsonValueKind.String)
                {
                    borrowerName = bn.GetString();
                }
                if (string.IsNullOrEmpty(borrowerName) &&
                    se.TryGetProperty("students", out var stu) && stu.ValueKind == JsonValueKind.Object &&
                    stu.TryGetProperty("full_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                {
                    borrowerName = fn.GetString();
                }
            }

            return new
            {
                sessionId = r.TryGetValue("session_id", out var sid) && sid is JsonElement sidEl && sidEl.ValueKind == JsonValueKind.Number ? sidEl.GetInt64() : 0,
                itemName = GetNestedString(r, "items", "name"),
                borrowerName = borrowerName ?? "",
                status = GetString(r, "status"),
                borrowedAt = GetString(r, "created_at"),
                returnedAt = GetString(r, "time_in")
            };
        });

        var list = expanded
            .GroupBy(r => (r.sessionId, r.itemName, r.borrowerName, r.status, r.returnedAt))
            .Select(g => new
            {
                itemName = g.Key.itemName,
                borrowerName = g.Key.borrowerName,
                status = g.Key.status,
                // Earliest unit in the group, not just "the first row
                // seen" - the 80-row insert loop above means these can
                // differ by a second or two, and the earliest one is the
                // moment the borrow actually started.
                borrowedAt = g.Min(x => x.borrowedAt),
                returnedAt = g.Key.returnedAt,
                quantity = g.Count()
            })
            .OrderByDescending(r => r.borrowedAt);

        return Json(list);
    }

    private static string GetString(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var val) && val is JsonElement el && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? "";
        }
        return "";
    }

    private static string GetNestedString(Dictionary<string, object?> row, string objectKey, string propertyKey)
    {
        if (row.TryGetValue(objectKey, out var val) && val is JsonElement el && el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(propertyKey, out var propEl) && propEl.ValueKind == JsonValueKind.String)
        {
            return propEl.GetString() ?? "";
        }
        return "";
    }

    // Turns the Date Start/Date End filters (yyyy-MM-dd, local time)
    // into a half-open UTC range [RangeStart, RangeEndExclusive) plus
    // the inclusive day count - every day-ranged /Report action above
    // builds its query from this instead of counting back from today
    // with a `days` number (see chat: "remove [the preset 7/14/30/90
    // buttons] since this can be set in the date"). With nothing
    // parseable, falls back to the last 14 days - the same window the
    // old preset defaulted to. Still capped at a 90-day span, same
    // protection `days` used to have, by pulling RangeStart forward
    // rather than ever trimming the end the admin actually chose.
    private static (DateTime RangeStart, DateTime RangeEndExclusive, int SpanDays) ResolveDateRange(string? dateStart, string? dateEnd)
    {
        var today = DateTime.Now.Date;
        var start = DateTime.TryParse(dateStart, out var s) ? s.Date : today.AddDays(-13);
        var end = DateTime.TryParse(dateEnd, out var e) ? e.Date : today;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var span = (end - start).Days + 1;
        if (span > 90)
        {
            start = end.AddDays(-89);
            span = 90;
        }

        return (start, end.AddDays(1), span);
    }

    // Shared by every /Report action that can be scoped to a slice of
    // students (gender/grade/section - see chat: "all girls, this
    // section, this section with that grade, ... like all"). "All",
    // blank, or unparsable means no filter on that one axis; all three
    // combine (AND), same as Register Student's own Gender/Grade/
    // Section filter fields.
    private static string? NormalizeGender(string? gender)
    {
        var g = gender?.Trim();
        if (string.Equals(g, "Male", StringComparison.OrdinalIgnoreCase)) return "Male";
        if (string.Equals(g, "Female", StringComparison.OrdinalIgnoreCase)) return "Female";
        return null;
    }

    private static int? NormalizeGrade(string? grade)
    {
        return int.TryParse(grade, out var g) ? g : (int?)null;
    }

    private static string? NormalizeSection(string? section)
    {
        var s = section?.Trim();
        return string.IsNullOrEmpty(s) || string.Equals(s, "All", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    // "Borrowed"/"Returned"/All (see chat: "wht if i only want borrow or
    // return") - matches the borrow_session_items.status column exactly,
    // same two values that column ever holds.
    private static string? NormalizeStatus(string? status)
    {
        var s = status?.Trim();
        if (string.Equals(s, "Borrowed", StringComparison.OrdinalIgnoreCase)) return "Borrowed";
        if (string.Equals(s, "Returned", StringComparison.OrdinalIgnoreCase)) return "Returned";
        return null;
    }

    // A specific item's exact name, or All (see chat: "or specific item") -
    // same "All"/blank-means-no-filter convention as gender/grade/section.
    private static string? NormalizeItem(string? item)
    {
        var i = item?.Trim();
        return string.IsNullOrEmpty(i) || string.Equals(i, "All", StringComparison.OrdinalIgnoreCase) ? null : i;
    }

    // True when the embedded student (if any) matches every demographic
    // filter that's actually set. With nothing set, every row matches
    // (including one with no embedded student at all, e.g. a staff
    // borrow) - a demographic filter only starts excluding rows once at
    // least one of gender/grade/section is actually chosen, and a row
    // with no student then correctly fails to match it.
    private static bool MatchesDemographic(JsonElement? studentEl, string? gender, int? grade, string? section)
    {
        if (gender == null && grade == null && section == null) return true;
        if (studentEl is not JsonElement se || se.ValueKind != JsonValueKind.Object) return false;

        if (gender != null)
        {
            var g = se.TryGetProperty("gender", out var ge) && ge.ValueKind == JsonValueKind.String ? ge.GetString() : null;
            if (!string.Equals(g, gender, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (grade != null)
        {
            if (!(se.TryGetProperty("grade_level", out var gl) && gl.ValueKind == JsonValueKind.Number && gl.GetInt32() == grade.Value)) return false;
        }
        if (section != null)
        {
            var s = se.TryGetProperty("section", out var se2) && se2.ValueKind == JsonValueKind.String ? se2.GetString()?.Trim() : null;
            if (!string.Equals(s, section, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // True when the embedded item (if any) is the one filtered item, by
    // name, case-insensitive - same shape as MatchesDemographic above but
    // for the Item filter, which only ever has one condition to check.
    private static bool MatchesItem(JsonElement? itemsEl, string? itemFilter)
    {
        if (itemFilter == null) return true;
        if (itemsEl is not JsonElement ie || ie.ValueKind != JsonValueKind.Object) return false;
        var name = ie.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
        return string.Equals(name, itemFilter, StringComparison.OrdinalIgnoreCase);
    }

    // Reads an embedded PostgREST object off a raw row, or off an
    // already-unwrapped JsonElement (for a second hop of embedding).
    private static JsonElement? GetEmbeddedObject(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var val) && val is JsonElement el && el.ValueKind == JsonValueKind.Object) return el;
        return null;
    }

    private static JsonElement? GetEmbeddedObject(JsonElement parent, string key)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object) return el;
        return null;
    }

    // borrow_session_items -> borrow_sessions -> students, the two-hop
    // embed every borrow-related action above filters on. Null when the
    // session has no linked student (a staff/teacher borrow), which
    // MatchesDemographic then correctly treats as not matching once any
    // demographic filter is actually set.
    private static JsonElement? GetSessionStudent(Dictionary<string, object?> row)
    {
        var session = GetEmbeddedObject(row, "borrow_sessions");
        return session is JsonElement se ? GetEmbeddedObject(se, "students") : null;
    }

    // Same active-student check Attendance's own Stats()/List() apply -
    // a row with no embedded student (shouldn't happen, FK is not null)
    // is treated as active rather than silently dropped.
    private static bool IsActiveStudentRow(Dictionary<string, object?> row)
    {
        if (!row.TryGetValue("students", out var studentVal) ||
            studentVal is not JsonElement studentEl ||
            studentEl.ValueKind != JsonValueKind.Object ||
            !studentEl.TryGetProperty("is_active", out var activeEl) ||
            (activeEl.ValueKind != JsonValueKind.True && activeEl.ValueKind != JsonValueKind.False))
        {
            return true;
        }
        return activeEl.GetBoolean();
    }

    // Buckets a list of rows by the local-time date portion of the given
    // timestamp field (PostgREST rows are UTC ISO strings) into
    // "yyyy-MM-dd" -> count, so each day's count matches what the stat
    // cards/filters elsewhere already show in local time, not UTC.
    private static Dictionary<string, int> CountByLocalDate(List<Dictionary<string, object?>> rows, string field)
    {
        var result = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            if (!row.TryGetValue(field, out var val) || val is not JsonElement el || el.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!DateTime.TryParse(el.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var utc))
            {
                continue;
            }
            var key = utc.ToLocalTime().Date.ToString("yyyy-MM-dd");
            result[key] = result.GetValueOrDefault(key, 0) + 1;
        }
        return result;
    }
}
