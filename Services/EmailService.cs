using System.Net;
using System.Net.Mail;
using C_LabTap.Models;
using Microsoft.Extensions.Options;

namespace C_LabTap.Services;

// Sends OTP codes over Gmail's SMTP server, using the dedicated sending
// account's App Password (see EmailOptions / appsettings' "Email"
// section) - never the account's real password. Uses the built-in
// System.Net.Mail instead of a NuGet package, since this project can't
// run `dotnet restore` from this environment.
public class EmailService
{
    private readonly EmailOptions _options;

    public EmailService(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.GmailAddress) && !string.IsNullOrWhiteSpace(_options.AppPassword);

    public async Task SendOtpEmailAsync(string toEmail, string code, string purposeLabel)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email sending isn't configured yet (missing Gmail address/App Password in appsettings).");
        }

        var plainTextBody =
            $"Your C-LabTap verification code is: {code}\n\n" +
            $"Use this to confirm your request to {purposeLabel.ToLowerInvariant()}.\n" +
            "This code expires in 10 minutes. If you didn't request this, you can safely ignore this email.";

        // HTML body puts the code on its own bold, letter-spaced line so
        // it's easy to read and copy at a glance - plain text above is
        // kept as a fallback for clients that don't render HTML (and
        // it tends to help deliverability besides).
        var htmlBody = $@"
<div style=""font-family: 'Segoe UI', Roboto, Arial, sans-serif; max-width: 480px; margin: 0 auto; color: #1f2d24;"">
  <div style=""background: #16342a; padding: 20px 28px; border-radius: 12px 12px 0 0;"">
    <span style=""color: #e8c468; font-weight: 700; font-size: 1.05rem; letter-spacing: 0.5px;"">C-LabTap</span>
  </div>
  <div style=""border: 1px solid #e3e3e3; border-top: none; border-radius: 0 0 12px 12px; padding: 28px;"">
    <p style=""margin: 0 0 6px; font-size: 0.95rem;"">Hello,</p>
    <p style=""margin: 0 0 22px; font-size: 0.95rem; line-height: 1.5;"">
      Someone (hopefully you) asked to <strong>{purposeLabel}</strong> on the C-LabTap admin account.
      Enter the code below to confirm it's really you:
    </p>
    <div style=""text-align: center; margin: 0 0 22px;"">
      <span style=""display: inline-block; font-size: 2rem; font-weight: 800; letter-spacing: 10px; color: #16342a; background: #f4f1e8; border: 1.5px dashed #c9a94d; border-radius: 10px; padding: 14px 20px;"">
        {code}
      </span>
    </div>
    <p style=""margin: 0 0 4px; font-size: 0.85rem; color: #5a6b60;"">This code expires in 10 minutes.</p>
    <p style=""margin: 0; font-size: 0.85rem; color: #5a6b60;"">If you didn't request this, you can safely ignore this email - no change will be made without the code.</p>
  </div>
  <p style=""text-align: center; font-size: 0.75rem; color: #9aa39d; margin-top: 16px;"">
    Aurelio Arago Memorial National High School &middot; C-LabTap Computer Laboratory Logbook
  </p>
</div>";

        using var message = new MailMessage
        {
            From = new MailAddress(_options.GmailAddress, "C-LabTap"),
            Subject = $"C-LabTap verification code - {purposeLabel}",
            Body = plainTextBody,
            IsBodyHtml = false,
        };
        message.To.Add(toEmail);

        var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html");
        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_options.GmailAddress, _options.AppPassword),
        };

        await client.SendMailAsync(message);
    }

    // Sent once when an admin creates a staff account (and again if the
    // person tries to log in before confirming) - see EmailVerification.
    // A link rather than a typed code, because the recipient isn't signed
    // in anywhere yet and has no screen waiting for a code.
    public async Task SendVerificationEmailAsync(string toEmail, string fullName, string link)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email sending isn't configured yet (missing Gmail address/App Password in appsettings).");
        }

        var safeName = WebUtility.HtmlEncode(fullName);
        var safeLink = WebUtility.HtmlEncode(link);

        var plainTextBody =
            $"Hello {fullName},\n\n" +
            "An administrator created a C-LabTap account for this email address.\n" +
            "Open this link to confirm the address is yours, then log in with the password you were given:\n\n" +
            $"{link}\n\n" +
            "The link works once and expires in 24 hours. If you weren't expecting this, you can ignore this email.";

        var htmlBody = $@"
<div style=""font-family: 'Segoe UI', Roboto, Arial, sans-serif; max-width: 480px; margin: 0 auto; color: #1f2d24;"">
  <div style=""background: #16342a; padding: 20px 28px; border-radius: 12px 12px 0 0;"">
    <span style=""color: #e8c468; font-weight: 700; font-size: 1.05rem; letter-spacing: 0.5px;"">C-LabTap</span>
  </div>
  <div style=""border: 1px solid #e3e3e3; border-top: none; border-radius: 0 0 12px 12px; padding: 28px;"">
    <p style=""margin: 0 0 6px; font-size: 0.95rem;"">Hello {safeName},</p>
    <p style=""margin: 0 0 22px; font-size: 0.95rem; line-height: 1.5;"">
      An administrator created a C-LabTap account for this email address.
      Confirm the address is yours, then log in with the password you were given.
    </p>
    <div style=""text-align: center; margin: 0 0 22px;"">
      <a href=""{safeLink}"" style=""display: inline-block; background: #16342a; color: #ffffff; text-decoration: none; font-weight: 700; font-size: 0.95rem; border-radius: 999px; padding: 12px 28px;"">Verify email address</a>
    </div>
    <p style=""margin: 0 0 4px; font-size: 0.85rem; color: #5a6b60;"">The link works once and expires in 24 hours.</p>
    <p style=""margin: 0 0 14px; font-size: 0.85rem; color: #5a6b60;"">If you weren't expecting this, you can ignore this email.</p>
    <p style=""margin: 0; font-size: 0.78rem; color: #5a6b60; word-break: break-all;"">Button not working? Copy this address into your browser: {safeLink}</p>
  </div>
  <p style=""text-align: center; font-size: 0.75rem; color: #9aa39d; margin-top: 16px;"">
    Aurelio Arago Memorial National High School &middot; C-LabTap Computer Laboratory Logbook
  </p>
</div>";

        using var message = new MailMessage
        {
            From = new MailAddress(_options.GmailAddress, "C-LabTap"),
            Subject = "Verify your C-LabTap account email",
            Body = plainTextBody,
            IsBodyHtml = false,
        };
        message.To.Add(toEmail);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));

        using var client = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_options.GmailAddress, _options.AppPassword),
        };

        await client.SendMailAsync(message);
    }
}
