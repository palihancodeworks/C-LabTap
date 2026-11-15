namespace C_LabTap.Models;

public class StudentRegistrationRequest
{
    public long? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Lrn { get; set; } = string.Empty;
    public int? Grade { get; set; }
    public string? Section { get; set; }
    public string? RfidTag { get; set; }
    public string? Gender { get; set; }
    // "STE" or "Regular"
    public string? ClassCategory { get; set; }
}
