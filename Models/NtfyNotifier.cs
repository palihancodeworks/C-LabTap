namespace C_LabTap.Models;

// Sends a push notification to the ntfy phone app (see chat: "we
// gonna do 2 but we will use the ntfy app tht i have on my phone")
// whenever something that looks like an attack happens - right now,
// just a brute-force lockout (see LoginAttemptGuard.RecordFailureAsync,
// the one place this is actually called from).
//
// Kept as a small static helper instead of a normal DI service on
// purpose: LoginAttemptGuard is itself static and already has 20+
// call sites across AccountController/LogbookController, so adding a
// notifier here means Program.cs configures it once at startup and
// nothing in those controllers has to change.
public static class NtfyNotifier
{
    private static HttpClient? _http;
    private static string? _topic;

    public static void Configure(HttpClient http, string topic)
    {
        _http = http;
        _topic = topic;
    }

    // Best-effort only: if ntfy.sh is slow, unreachable, or not
    // configured, this must never throw or block whatever flow (like
    // a login lockout) triggered it.
    public static async Task SendAsync(string title, string message)
    {
        if (_http == null || string.IsNullOrWhiteSpace(_topic)) return;
        try
        {
            await PostAsync(title, message);
        }
        catch
        {
            // Swallowed on purpose - see comment above SendAsync.
        }
    }

    private static async Task PostAsync(string title, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _topic)
        {
            Content = new StringContent(message)
        };
        // ntfy's own headers for the notification title/importance -
        // see https://docs.ntfy.sh/publish/. Title must stay plain
        // ASCII (no emoji/accents) since it's sent as an HTTP header.
        request.Headers.Add("Title", title);
        request.Headers.Add("Priority", "high");
        request.Headers.Add("Tags", "warning");
        var response = await _http!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
