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

    // One row per day for the last `days` days (today included), with
    // entries/borrowed/returned counts - backs both the trend charts and
    // the printable table (and its Excel export, built client-side from
    // this same JSON - see Views/Report/_Content.cshtml). Capped at 90
    // days; two queries total (not one per day) with the grouping done
    // in C# against each row's own local date.
    [HttpGet]
    public async Task<IActionResult> Table(int days = 14)
    {
        if (days < 1) days = 1;
        if (days > 90) days = 90;

        var today = DateTime.Now.Date;
        var rangeStart = today.AddDays(-(days - 1));
        var startUtc = rangeStart.ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var entryRows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=occurred_at,students(is_active)&event_type=eq.time_in&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000"))
            .Where(IsActiveStudentRow)
            .ToList();

        var borrowRows = await _supabase.GetAsync(
            "borrow_session_items",
            $"select=created_at&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000");

        var returnRows = await _supabase.GetAsync(
            "borrow_session_items",
            $"select=time_in&status=eq.Returned&time_in=gte.{Uri.EscapeDataString(startUtc)}&time_in=lt.{Uri.EscapeDataString(endUtc)}&limit=10000");

        var entriesByDay = CountByLocalDate(entryRows, "occurred_at");
        var borrowedByDay = CountByLocalDate(borrowRows, "created_at");
        var returnedByDay = CountByLocalDate(returnRows, "time_in");

        var table = new List<object>();
        for (var d = rangeStart; d <= today; d = d.AddDays(1))
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

    // Most-borrowed items within the selected day range (by unit-borrow
    // count, same unit counted once per time it went out) - backs the
    // "Most Borrowed Items" chart, so it follows the same Range toggle as
    // every other chart on the page. Embeds the item's name so a
    // deleted/deactivated item still shows its last-known name instead
    // of dropping out of the count.
    [HttpGet]
    public async Task<IActionResult> TopItems(int limit = 6, int days = 14)
    {
        if (limit < 1) limit = 1;
        if (limit > 20) limit = 20;
        if (days < 1) days = 1;
        if (days > 90) days = 90;

        var today = DateTime.Now.Date;
        var startUtc = today.AddDays(-(days - 1)).ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var rows = await _supabase.GetAsync(
            "borrow_session_items",
            $"select=items(name)&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000");

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

    // Entries (time-ins) per hour of the day within the selected range,
    // all days combined - backs the "Busiest Hours" chart, so the lab can
    // see when it actually fills up. Same active-student filter and
    // local-time bucketing as the rest of this page.
    [HttpGet]
    public async Task<IActionResult> PeakHours(int days = 14)
    {
        if (days < 1) days = 1;
        if (days > 90) days = 90;

        var today = DateTime.Now.Date;
        var startUtc = today.AddDays(-(days - 1)).ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var rows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=occurred_at,students(is_active)&event_type=eq.time_in&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&limit=10000"))
            .Where(IsActiveStudentRow);

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
    public async Task<IActionResult> StudentList()
    {
        var rows = await _supabase.GetAsync(
            "students",
            "select=full_name,lrn,grade_level,section,gender,class_category&is_active=eq.true&order=grade_level,section,full_name&limit=5000");

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
    // selected day range - backs the "Attendance Log" checkbox. Same
    // active-student filter as every other attendance count on this
    // page, so a deactivated student's old taps don't show up here
    // either.
    [HttpGet]
    public async Task<IActionResult> AttendanceLog(int days = 14)
    {
        if (days < 1) days = 1;
        if (days > 90) days = 90;

        var today = DateTime.Now.Date;
        var startUtc = today.AddDays(-(days - 1)).ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var rows = (await _supabase.GetAsync(
            "attendance_log",
            $"select=event_type,method,occurred_at,students(full_name,is_active)&occurred_at=gte.{Uri.EscapeDataString(startUtc)}&occurred_at=lt.{Uri.EscapeDataString(endUtc)}&order=occurred_at.desc&limit=10000"))
            .Where(IsActiveStudentRow)
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

    // Raw borrow/return log rows within the selected day range - backs
    // the "Borrow/Return Log" checkbox. Borrower is either a student
    // (via the session's student_id) or a staff/teacher name entered at
    // the kiosk (borrower_name, method = Staff) - never both.
    [HttpGet]
    public async Task<IActionResult> BorrowLog(int days = 14)
    {
        if (days < 1) days = 1;
        if (days > 90) days = 90;

        var today = DateTime.Now.Date;
        var startUtc = today.AddDays(-(days - 1)).ToUniversalTime().ToString("o");
        var endUtc = today.AddDays(1).ToUniversalTime().ToString("o");

        var rows = await _supabase.GetAsync(
            "borrow_session_items",
            $"select=status,created_at,time_in,items(name),borrow_sessions(borrower_name,students(full_name))&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}&order=created_at.desc&limit=10000");

        var list = rows.Select(r =>
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
                itemName = GetNestedString(r, "items", "name"),
                borrowerName = borrowerName ?? "",
                status = GetString(r, "status"),
                borrowedAt = GetString(r, "created_at"),
                returnedAt = GetString(r, "time_in")
            };
        });

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
