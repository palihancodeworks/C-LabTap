using System.Net.Http.Json;

namespace C_LabTap.Services;

// Thin wrapper around Supabase's PostgREST API (the auto-generated REST
// layer over the Postgres tables), called using the service_role key set
// up in Program.cs. That key bypasses RLS, so this service is only ever
// injected into server-side code (controllers, other services) - never
// into anything the browser can reach directly.
public class SupabaseService
{
    private readonly HttpClient _http;

    public SupabaseService(HttpClient http)
    {
        _http = http;
    }

    // Fetches rows from a table. queryString is a raw PostgREST query
    // string, e.g. "select=*&status=eq.Available" (no leading "?").
    public async Task<List<Dictionary<string, object?>>> GetAsync(string table, string? queryString = null)
    {
        var url = string.IsNullOrEmpty(queryString) ? table : $"{table}?{queryString}";
        var result = await _http.GetFromJsonAsync<List<Dictionary<string, object?>>>(url);
        return result ?? new List<Dictionary<string, object?>>();
    }

    // Fetches one page of rows plus the total row count that matches
    // queryString (ignoring limit/offset) - what admin tables use to
    // render "Page X of Y" pagination. queryString should carry every
    // filter EXCEPT limit/offset/select-order-of-paging (order is fine);
    // this method appends limit/offset itself based on page/pageSize.
    // Uses PostgREST's "Prefer: count=exact" + the Content-Range response
    // header rather than a separate count query.
    public async Task<(List<Dictionary<string, object?>> Rows, int TotalCount)> GetPagedAsync(
        string table, string queryString, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        var offset = (page - 1) * pageSize;

        var fullQuery = string.IsNullOrEmpty(queryString)
            ? $"limit={pageSize}&offset={offset}"
            : $"{queryString}&limit={pageSize}&offset={offset}";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{table}?{fullQuery}");
        request.Headers.Add("Prefer", "count=exact");

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var total = 0;
        if (response.Content.Headers.TryGetValues("Content-Range", out var crValues))
        {
            // PostgREST format: "0-19/137" (or "*/0" when there are no rows).
            var cr = crValues.FirstOrDefault();
            var slash = cr?.IndexOf('/') ?? -1;
            if (slash >= 0 && int.TryParse(cr![(slash + 1)..], out var parsedTotal))
            {
                total = parsedTotal;
            }
        }

        var rows = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>() ?? new();
        return (rows, total);
    }

    // Inserts one row and returns the row Supabase echoes back
    // (Program.cs sets "Prefer: return=representation" for this).
    public async Task<Dictionary<string, object?>?> InsertAsync(string table, object payload)
    {
        var response = await _http.PostAsJsonAsync(table, payload);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>();
        return rows?.FirstOrDefault();
    }

    // Updates row(s) matching a PostgREST filter, e.g. "id=eq.5".
    public async Task UpdateAsync(string table, string filter, object payload)
    {
        var response = await _http.PatchAsJsonAsync($"{table}?{filter}", payload);
        response.EnsureSuccessStatusCode();
    }

    // Deletes row(s) matching a PostgREST filter, e.g. "id=eq.5".
    public async Task DeleteAsync(string table, string filter)
    {
        var response = await _http.DeleteAsync($"{table}?{filter}");
        response.EnsureSuccessStatusCode();
    }
}
