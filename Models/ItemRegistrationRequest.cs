namespace C_LabTap.Models;

public class ItemRegistrationRequest
{
    // Set only when editing an already-registered unit (RegisterItemController.Update);
    // null/0 means "register new" (RegisterItemController.Create).
    public long? Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }

    // On Create, this is a BASE/PREFIX (e.g. "TBLT-X") - the controller
    // appends a 2-digit sequence number to it per physical unit created
    // (TBLT-X01, TBLT-X02, ...). On Update there is exactly one unit being
    // edited, so this holds its full, literal item code instead (no
    // suffix appended).
    public string ItemCodeBase { get; set; } = string.Empty;
    public int? Quantity { get; set; }
}
