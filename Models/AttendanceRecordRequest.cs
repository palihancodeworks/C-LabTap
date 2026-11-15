namespace C_LabTap.Models;

public class AttendanceRecordRequest
{
    public string? Lrn { get; set; }
    public string? RfidTag { get; set; }
    public string Method { get; set; } = "Manual"; // "RFID" or "Manual"
}
