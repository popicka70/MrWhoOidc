namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for sending emails, optional integration.
/// </summary>
public interface IEmailService
{
    Task SendPasswordResetEmailAsync(string email, string resetUrl);
}
