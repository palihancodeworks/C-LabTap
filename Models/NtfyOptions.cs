namespace C_LabTap.Models;

// Bound from the "Ntfy" section of appsettings.*.json - the ntfy.sh
// (or self-hosted ntfy) topic that security alerts get pushed to (see
// NtfyNotifier). ServerUrl defaults to the public https://ntfy.sh
// server if left blank; most setups only need to fill in Topic.
public class NtfyOptions
{
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = string.Empty;
}
