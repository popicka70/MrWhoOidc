using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

internal sealed class SmtpEmailSender(IOptions<MailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogDebug("SMTP email disabled. Skipping send for {Subject}", message.Subject);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            logger.LogWarning("SMTP host not configured. Unable to send email {Subject}", message.Subject);
            return;
        }

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);
        }

        using var mail = new MailMessage
        {
            Subject = message.Subject,
            From = new MailAddress(settings.FromAddress, settings.FromName ?? settings.FromAddress)
        };

        mail.To.Add(new MailAddress(message.To.Email, message.To.Name));

        if (!string.IsNullOrWhiteSpace(message.HtmlBody) && !string.IsNullOrWhiteSpace(message.TextBody))
        {
            mail.Body = message.HtmlBody;
            mail.IsBodyHtml = true;
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, null, MediaTypeNames.Text.Plain));
        }
        else if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            mail.Body = message.HtmlBody;
            mail.IsBodyHtml = true;
        }
        else
        {
            mail.Body = message.TextBody ?? string.Empty;
            mail.IsBodyHtml = false;
        }

        try
        {
            await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Sent email {Subject} to {Recipient}", message.Subject, message.To.Email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email {Subject} to {Recipient}", message.Subject, message.To.Email);
            throw;
        }
    }
}
