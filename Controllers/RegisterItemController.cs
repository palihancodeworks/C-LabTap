using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Models;
using C_LabTap.Services;

namespace C_LabTap.Controllers;

public class RegisterItemController : Controller
{
    private readonly SupabaseService _supabase;
    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";
    // Create/Update stay open to Staff (routine data entry) - only
    // deleting an item record is Admin-only (see chat: same reasoning
    // as RegisterStudentController.IsAdmin).
    private bool IsAdmin => string.Equals(User.FindFirst(ClaimTypes.Role)?.Value, "Admin", StringComparison.OrdinalIgnoreCase);

    public RegisterItemController(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public IActionResult Index()
    {
        if (IsAjax) return PartialView("_Content");

        ViewData["Title"] = "Register Item";
        ViewData["Active"] = "RegisterItem";
        return View();
    }

    // Powers the Product filter dropdown with the distinct item names
    // that actually exist, same pattern as Attendance's Section filter -
    // instead of a static/hardcoded list that drifts from reality.
    [HttpGet]
    public async Task<IActionResult> FilterOptions()
    {
        var rows = await _supabase.GetAsync("items", "select=name&is_active=eq.true");
        var names = rows
            .Select(r => r["name"]?.ToString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Json(new { products = names });
    }

    // Only "Available"/"Borrowed" are stored (the column has a check
    // constraint); anything else, including blank, is treated as no filter.
    private static string? NormalizeStatus(string? status)
    {
        var s = status?.Trim();
        if (string.Equals(s, "Available", StringComparison.OrdinalIgnoreCase)) return "Available";
        if (string.Equals(s, "Borrowed", StringComparison.OrdinalIgnoreCase)) return "Borrowed";
        return null;
    }

    // Feeds the Registered table, paginated server-side. There's no
    // Archived view anymore - Delete still soft-deletes (is_active =
    // false) below so borrow history stays intact, but bringing a unit
    // back now needs a direct DB fix instead of an Activate button.
    [HttpGet]
    public async Task<IActionResult> List(string? search, string? category, string? product, string? status, string? dateStart, string? dateEnd, int page = 1, int pageSize = 6)
    {
        // See SuspiciousInputDetector - flags SQL-looking text typed
        // into this search box and pings ntfy (see chat).
        await SuspiciousInputDetector.CheckAndAlertAsync(search, "Item Inventory search");

        var query = "select=id,name,category,item_code,status,created_at&is_active=eq.true";

        if (!string.IsNullOrWhiteSpace(category) && category != "All")
        {
            query += $"&category=eq.{Uri.EscapeDataString(category.Trim())}";
        }
        if (!string.IsNullOrWhiteSpace(product) && product != "All")
        {
            query += $"&name=eq.{Uri.EscapeDataString(product.Trim())}";
        }
        if (NormalizeStatus(status) is string statusFilter)
        {
            query += $"&status=eq.{statusFilter}";
        }

        // Date Start/End filter by registration date (created_at), same
        // local-to-UTC day-boundary handling as the other registries.
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
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = Uri.EscapeDataString(search.Trim());
            query += $"&or=(name.ilike.*{term}*,item_code.ilike.*{term}*)";
        }

        query += "&order=created_at.desc";

        var (rows, total) = await _supabase.GetPagedAsync("items", query, page, pageSize);

        var items = rows.Select(row =>
        {
            var id = ((JsonElement)row["id"]!).GetInt64();
            var name = row["name"]?.ToString() ?? "";
            var itemCode = row["item_code"]?.ToString() ?? "";
            var itemCategory = row["category"]?.ToString();
            var itemStatus = row["status"]?.ToString();

            var registeredOn = "";
            if (row["created_at"]?.ToString() is string createdRaw &&
                DateTime.TryParse(createdRaw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var createdAt))
            {
                registeredOn = createdAt.ToLocalTime().ToString("M/d/yyyy h:mm tt");
            }

            return new
            {
                id,
                name,
                itemCode,
                category = itemCategory,
                status = itemStatus,
                registeredOn
            };
        }).ToList();

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 1;
        if (totalPages < 1) totalPages = 1;

        return Json(new { rows = items, total, page, pageSize, totalPages });
    }

    // Powers the stat cards: how many active units exist right now, and
    // how many of those are currently out on loan.
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var (_, totalRegistered) = await _supabase.GetPagedAsync(
            "items", "select=id&is_active=eq.true", 1, 1);
        var (_, borrowed) = await _supabase.GetPagedAsync(
            "items", "select=id&is_active=eq.true&status=eq.Borrowed", 1, 1);
        return Json(new { totalRegistered, borrowed });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromForm] ItemRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ItemCodeBase))
        {
            return BadRequest(new { error = "Item name and item code are required." });
        }

        var quantity = request.Quantity is > 0 ? request.Quantity.Value : 1;
        if (quantity > 100)
        {
            return BadRequest(new { error = "Register at most 100 units at a time." });
        }

        var prefix = request.ItemCodeBase.Trim();

        // Items are one row PER PHYSICAL UNIT. Find the highest existing
        // suffix for this prefix so registering more of the same item
        // continues the sequence instead of colliding with codes that
        // already exist (e.g. adding 2 more Xiaomi tablets after TBLT-X01..05
        // starts at TBLT-X06). Pulling every matching row and taking the max
        // in C# - rather than "order=item_code.desc&limit=1" - because
        // Postgres/PostgREST sorts item_code as TEXT: once a prefix passes
        // 99 units, "...99" text-sorts ABOVE "...100" (the character '9' >
        // '1'), so the single-row "last code" query silently picks the
        // wrong one and the next batch re-creates an already-existing code
        // (see chat - this is what broke registering more Stylus Pens after
        // two batches of 50 totalled 100).
        var existing = await _supabase.GetAsync(
            "items",
            $"select=item_code&item_code=like.{Uri.EscapeDataString(prefix)}*");

        int nextSuffix = 1;
        foreach (var row in existing)
        {
            var code = row["item_code"]?.ToString() ?? "";
            var tail = code.Length > prefix.Length ? code[prefix.Length..] : "";
            if (int.TryParse(tail, out var num) && num + 1 > nextSuffix)
            {
                nextSuffix = num + 1;
            }
        }

        var createdCodes = new List<string>();
        for (int i = 0; i < quantity; i++)
        {
            var code = $"{prefix}{(nextSuffix + i):00}";
            try
            {
                await _supabase.InsertAsync("items", new
                {
                    name = request.Name.Trim(),
                    category = (request.Category ?? "").Trim(),
                    item_code = code
                });
                createdCodes.Add(code);
            }
            catch (HttpRequestException)
            {
                return Conflict(new
                {
                    error = $"Item code {code} already exists - try a different base code.",
                    created = createdCodes
                });
            }
        }

        return Json(new { success = true, codes = createdCodes });
    }

    // Edits an already-registered unit's name, category and item code.
    // Status isn't editable here - it's system-managed by the borrow/
    // return flow (ItemLendingController), not something an admin should
    // hand-flip from this form.
    [HttpPost]
    public async Task<IActionResult> Update([FromForm] ItemRegistrationRequest request)
    {
        if (request.Id is null || request.Id <= 0)
        {
            return BadRequest(new { error = "Missing item id." });
        }
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ItemCodeBase))
        {
            return BadRequest(new { error = "Item name and item code are required." });
        }

        try
        {
            await _supabase.UpdateAsync("items", $"id=eq.{request.Id.Value}", new
            {
                name = request.Name.Trim(),
                category = (request.Category ?? "").Trim(),
                item_code = request.ItemCodeBase.Trim()
            });
            return Json(new { success = true });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not update this item - that item code may already be in use." });
        }
    }

    // Soft delete, same pattern as RegisterStudentController.Delete: marks
    // the item inactive (and timestamps it, same as a student's
    // archived_at) instead of removing the row, so borrow history
    // referencing it (borrow_session_items) stays intact. A unit that's
    // currently out on loan can't be archived - it has to come back first,
    // otherwise the active borrow session would be left pointing at a
    // "removed" item.
    [HttpPost]
    public async Task<IActionResult> Delete([FromForm] long id)
    {
        if (!IsAdmin)
        {
            return StatusCode(403, new { error = "Only an Admin can delete an item record." });
        }

        if (id <= 0)
        {
            return BadRequest(new { error = "Missing item id." });
        }

        var existing = await _supabase.GetAsync("items", $"select=status&id=eq.{id}&limit=1");
        if (existing.Count > 0 && existing[0]["status"]?.ToString() == "Borrowed")
        {
            return Conflict(new { error = "This item is currently borrowed - it can't be deleted until it's returned." });
        }

        try
        {
            await _supabase.UpdateAsync("items", $"id=eq.{id}", new { is_active = false, archived_at = DateTime.UtcNow });
            return Json(new { success = true });
        }
        catch (HttpRequestException)
        {
            return Conflict(new { error = "Could not delete this item." });
        }
    }
}
