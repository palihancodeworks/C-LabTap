using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Models;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

public class ItemLendingController : Controller
{
    private readonly SupabaseService _supabase;
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    public ItemLendingController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        if (IsAjax) return PartialView("_Content");

        ViewData["Title"] = "Item Lending";
        ViewData["Active"] = "ItemLending";
        return View();
    }

    private static string? GetStr(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string FormatTimestamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
        {
            return dt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
        }
        return "";
    }

    // Turns one borrow_sessions row (with its embedded student + items)
    // into the flat shape the table renders - one entry per borrow
    // SESSION, with its items kept as a list so the view can stack them
    // (a student who borrowed 2 items and returned only 1 shows both,
    // each with its own Status/Time In - see Views/ItemLending/_Content).
    private static object ShapeSession(Dictionary<string, object?> row)
    {
        var id = ((JsonElement)row["id"]!).GetInt64();
        var method = row["method"]?.ToString() ?? "";
        var timeOut = FormatTimestamp(row["time_out"]?.ToString());

        string borrowerName;
        string gradeSection;

        if (row["students"] is JsonElement studentEl && studentEl.ValueKind == JsonValueKind.Object)
        {
            borrowerName = GetStr(studentEl, "full_name") ?? "";
            string? grade = null;
            if (studentEl.TryGetProperty("grade_level", out var g) && g.ValueKind == JsonValueKind.Number)
            {
                grade = g.GetInt32().ToString();
            }
            var section = GetStr(studentEl, "section");
            var parts = new[] { grade, section }.Where(p => !string.IsNullOrEmpty(p));
            gradeSection = string.Join(" - ", parts);
        }
        else
        {
            var bn = row["borrower_name"]?.ToString();
            borrowerName = string.IsNullOrEmpty(bn) ? "Staff" : bn;
            gradeSection = "Staff";
        }

        var items = new List<object>();
        if (row["borrow_session_items"] is JsonElement itemsEl && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                var sessionItemId = item.TryGetProperty("id", out var siId) ? siId.GetInt64() : 0;
                var itemStatus = GetStr(item, "status") ?? "Borrowed";
                var timeIn = item.TryGetProperty("time_in", out var ti) ? FormatTimestamp(ti.GetString()) : "";
                string itemName = "", itemCode = "";
                if (item.TryGetProperty("items", out var itemInfo) && itemInfo.ValueKind == JsonValueKind.Object)
                {
                    itemName = GetStr(itemInfo, "name") ?? "";
                    itemCode = GetStr(itemInfo, "item_code") ?? "";
                }
                items.Add(new { sessionItemId, name = itemName, itemCode, status = itemStatus, timeIn });
            }
        }

        return new { id, borrowerName, gradeSection, method, timeOut, items };
    }

    private static bool SessionHasItemStatus(Dictionary<string, object?> row, string status)
    {
        if (row["borrow_session_items"] is not JsonElement itemsEl || itemsEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var item in itemsEl.EnumerateArray())
        {
            if (item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == status)
            {
                return true;
            }
        }
        return false;
    }

    // One row per borrow SESSION. Grade/Category describe the borrowing
    // STUDENT, not the item, and only a student-linked session has
    // either - a manually-logged staff borrow never matches when one of
    // those is set. They're resolved to a plain student-id list first so
    // the sessions query itself only ever needs simple, same-table-safe
    // PostgREST filters (in.(), eq., ilike.) instead of risky filtering
    // across the embedded students/items relationships.
    [HttpGet]
    public async Task<IActionResult> List(string? search, string? category, string? grade, string? status, string? method, string? dateStart, string? dateEnd, int page = 1, int pageSize = 6)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 6;

        // "Staff" isn't a real class_category - it means "no linked
        // student at all" (a manually-logged staff/teacher borrow, see
        // CreateManual), so it skips the student lookup entirely and
        // filters borrow_sessions directly instead (see below).
        var isStaffCategory = string.Equals(category?.Trim(), "Staff", StringComparison.OrdinalIgnoreCase);
        var gradeOrCategoryActive = (!string.IsNullOrWhiteSpace(category) && category != "All" && !isStaffCategory)
            || int.TryParse(grade, out _);
        var searchActive = !string.IsNullOrWhiteSpace(search);

        List<long> studentIdFilter = new();
        if (gradeOrCategoryActive || searchActive)
        {
            var studentQuery = "select=id&is_active=eq.true";
            if (!string.IsNullOrWhiteSpace(category) && category != "All")
            {
                studentQuery += $"&class_category=eq.{Uri.EscapeDataString(category.Trim())}";
            }
            if (int.TryParse(grade, out var gradeNum))
            {
                studentQuery += $"&grade_level=eq.{gradeNum}";
            }
            if (searchActive)
            {
                studentQuery += $"&full_name=ilike.*{Uri.EscapeDataString(search!.Trim())}*";
            }

            var matches = await _supabase.GetAsync("students", studentQuery);
            studentIdFilter = matches.Select(m => ((JsonElement)m["id"]!).GetInt64()).ToList();

            if (gradeOrCategoryActive && studentIdFilter.Count == 0)
            {
                // No student fits Grade/Category, and a staff borrow never
                // has either - nothing can match, so skip the real query.
                return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 1 });
            }
        }

        var query = "select=id,time_out,method,borrower_name," +
            "students(full_name,grade_level,section)," +
            "borrow_session_items(id,status,time_in,items(name,item_code))";

        if (!string.IsNullOrWhiteSpace(method) && method != "All")
        {
            query += $"&method=eq.{Uri.EscapeDataString(method.Trim())}";
        }
        if (DateTime.TryParse(dateStart, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var startDate))
        {
            var startUtc = startDate.Date.ToUniversalTime().ToString("o");
            query += $"&time_out=gte.{Uri.EscapeDataString(startUtc)}";
        }
        if (DateTime.TryParse(dateEnd, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var endDate))
        {
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime().ToString("o");
            query += $"&time_out=lt.{Uri.EscapeDataString(endUtc)}";
        }

        if (isStaffCategory)
        {
            query += "&student_id=is.null";
        }
        else if (gradeOrCategoryActive)
        {
            // Narrowed to a specific set of students - only their sessions
            // can match (the empty-list case already returned above).
            query += $"&student_id=in.({string.Join(",", studentIdFilter)})";
        }
        else if (searchActive)
        {
            var term = Uri.EscapeDataString(search!.Trim());
            query += studentIdFilter.Count > 0
                ? $"&or=(borrower_name.ilike.*{term}*,student_id.in.({string.Join(",", studentIdFilter)}))"
                : $"&borrower_name.ilike.*{term}*";
        }

        query += "&order=time_out.desc";

        // Status describes an individual ITEM within a session, not the
        // session as a whole - one borrow batch can have some items back
        // already and others still out. Filtering it at the database
        // query level would mean PostgREST also had to apply it to the
        // embedded borrow_session_items themselves, hiding the very other
        // items in that session this page is supposed to show. So every
        // OTHER filter above still narrows things down at the database;
        // "does this session have at least one item in this state" is
        // checked here instead, against a wider batch (capped well above
        // anything this one lab will ever produce) so the page math
        // afterwards still comes out exact.
        var wantsStatusFilter = !string.IsNullOrWhiteSpace(status) && status != "All";
        List<Dictionary<string, object?>> rows;
        int total;

        if (wantsStatusFilter)
        {
            var (allRows, _) = await _supabase.GetPagedAsync("borrow_sessions", query, 1, 500);
            var filtered = allRows.Where(r => SessionHasItemStatus(r, status!)).ToList();
            total = filtered.Count;
            rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        else
        {
            (rows, total) = await _supabase.GetPagedAsync("borrow_sessions", query, page, pageSize);
        }

        var sessions = rows.Select(ShapeSession).ToList();

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 1;
        if (totalPages < 1) totalPages = 1;

        return Json(new { rows = sessions, total, page, pageSize, totalPages });
    }

    // Powers the stat cards. "Borrowed Today" counts individual items
    // taken out today (not sessions - a 3-item batch counts as 3);
    // "Currently Borrowed" is the same live count Register Item shows,
    // from the item's own status column, so the two pages never disagree.
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var startUtc = DateTime.Now.Date.ToUniversalTime().ToString("o");
        var endUtc = DateTime.Now.Date.AddDays(1).ToUniversalTime().ToString("o");

        var (_, borrowedToday) = await _supabase.GetPagedAsync(
            "borrow_session_items",
            $"select=id&created_at=gte.{Uri.EscapeDataString(startUtc)}&created_at=lt.{Uri.EscapeDataString(endUtc)}",
            1, 1);

        var (_, currentlyBorrowed) = await _supabase.GetPagedAsync(
            "items", "select=id&is_active=eq.true&status=eq.Borrowed", 1, 1);

        return Json(new { borrowedToday, currentlyBorrowed });
    }

    // Feeds the "Log a Borrow" modal's item checklist - only units that
    // are actually free to go out.
    [HttpGet]
    public async Task<IActionResult> AvailableItems()
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

    // Feeds the "Log a Borrow" modal's student combobox - typing a
    // couple characters of a name or LRN returns a short list of active
    // students to pick from, instead of someone having to know and
    // type an exact LRN.
    [HttpGet]
    public async Task<IActionResult> SearchStudents(string? term)
    {
        var t = term?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length < 2)
        {
            return Json(Array.Empty<object>());
        }

        var escaped = Uri.EscapeDataString(t);
        var rows = await _supabase.GetAsync(
            "students",
            $"select=id,full_name,lrn,grade_level,section&is_active=eq.true&or=(full_name.ilike.*{escaped}*,lrn.ilike.*{escaped}*)&order=full_name.asc&limit=8");

        var results = rows.Select(r =>
        {
            string? grade = null;
            if (r["grade_level"] is JsonElement g && g.ValueKind == JsonValueKind.Number)
            {
                grade = g.GetInt32().ToString();
            }
            var section = r["section"]?.ToString();
            var parts = new[] { grade, section }.Where(p => !string.IsNullOrEmpty(p));

            return new
            {
                id = ((JsonElement)r["id"]!).GetInt64(),
                fullName = r["full_name"]?.ToString() ?? "",
                lrn = r["lrn"]?.ToString() ?? "",
                gradeSection = string.Join(" - ", parts)
            };
        });

        return Json(results);
    }

    // Staff-side "Log a Borrow" - stands in for the kiosk Equipment tab,
    // which doesn't exist yet (see chat), so this is currently the only
    // way a borrow_sessions row gets created. Covers both a student
    // (looked up by LRN, so it links to their real record) and a
    // staff/teacher borrow (just a typed name, no student row to point
    // at - see the allow_staff_borrow_sessions migration).
    [HttpPost]
    public async Task<IActionResult> CreateManual([FromForm] ManualBorrowRequest request)
    {
        if (request.ItemIds == null || request.ItemIds.Count == 0)
        {
            return BadRequest(new { error = "Select at least one item to log as borrowed." });
        }

        long? studentId = null;
        string? borrowerName = null;

        if (string.Equals(request.BorrowerType, "Staff", StringComparison.OrdinalIgnoreCase))
        {
            borrowerName = request.BorrowerName?.Trim();
            if (string.IsNullOrWhiteSpace(borrowerName))
            {
                return BadRequest(new { error = "Enter the staff/teacher's name." });
            }
        }
        else
        {
            var lrn = request.Lrn?.Trim();
            if (string.IsNullOrWhiteSpace(lrn))
            {
                return BadRequest(new { error = "Enter the student's LRN." });
            }

            var students = await _supabase.GetAsync(
                "students", $"select=id,is_active&lrn=eq.{Uri.EscapeDataString(lrn)}&limit=1");
            var isActive = students.Count > 0 && students[0]["is_active"] is JsonElement activeEl
                && activeEl.ValueKind == JsonValueKind.True;
            if (!isActive)
            {
                return NotFound(new { error = "No active student found with that LRN." });
            }
            studentId = ((JsonElement)students[0]["id"]!).GetInt64();
        }

        // Re-check availability right before creating the session - one
        // unit can only be out to one borrower at a time, and this closes
        // the gap between the checklist loading and the form being
        // submitted (another borrow logged in between, say).
        var idList = string.Join(",", request.ItemIds);
        var itemRows = await _supabase.GetAsync(
            "items", $"select=id,name,status&id=in.({idList})&is_active=eq.true");

        if (itemRows.Count != request.ItemIds.Count)
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
            borrower_name = borrowerName,
            method = "Staff",
            time_out = DateTime.UtcNow
        });

        if (session == null || session["id"] is not JsonElement sessionIdEl)
        {
            return StatusCode(500, new { error = "Could not create the borrow session." });
        }
        var sessionId = sessionIdEl.GetInt64();

        foreach (var itemId in request.ItemIds)
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

    // Marks one item within a session as back - flips that
    // borrow_session_items row to Returned and the item's own status
    // back to Available, so Register Item's "Currently Borrowed" count
    // and its Delete-while-borrowed guard both stay correct.
    [HttpPost]
    public async Task<IActionResult> Return([FromForm] long sessionItemId)
    {
        if (sessionItemId <= 0)
        {
            return BadRequest(new { error = "Missing item." });
        }

        var rows = await _supabase.GetAsync(
            "borrow_session_items", $"select=id,item_id,status&id=eq.{sessionItemId}&limit=1");
        if (rows.Count == 0)
        {
            return NotFound(new { error = "Borrow record not found." });
        }
        if (rows[0]["status"]?.ToString() == "Returned")
        {
            return Json(new { success = true });
        }

        var itemId = ((JsonElement)rows[0]["item_id"]!).GetInt64();

        await _supabase.UpdateAsync("borrow_session_items", $"id=eq.{sessionItemId}", new
        {
            status = "Returned",
            time_in = DateTime.UtcNow
        });
        await _supabase.UpdateAsync("items", $"id=eq.{itemId}", new { status = "Available" });

        return Json(new { success = true });
    }
}
