namespace C_LabTap.Models;

// Bound from the "Email" section of appsettings.*.json - the Gmail
// account (with an App Password, not its real password - see
// EmailService) that sends OTP codes for password/PIN/Google-link
// changes (see AccountController.RequestOtp).
public class EmailOptions
{
    public string GmailAddress { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
}
