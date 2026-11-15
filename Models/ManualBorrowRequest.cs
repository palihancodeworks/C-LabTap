namespace C_LabTap.Models;

// Used by ItemLendingController.CreateManual - a staff-entered borrow
// record for when there's no kiosk tap to create one automatically (a
// teacher borrowing equipment, or logging something after the fact).
// Exactly one of Lrn/BorrowerName ends up identifying the borrower,
// matching the borrow_sessions_borrower_check constraint in Supabase.
public class ManualBorrowRequest
{
    public string? BorrowerType { get; set; } // "Student" or "Staff"
    public string? Lrn { get; set; }
    public string? BorrowerName { get; set; }
    public List<long> ItemIds { get; set; } = new();
}
