using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Services;

public sealed record EmailAddress(string Email, string? Name = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email : Name;
}

public sealed record EmailMessage
{
    public required EmailAddress To { get; init; }
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
}

/// <summary>
/// Service for sending emails.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an email message.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

internal sealed class NullEmailSender(ILogger<NullEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Email sending skipped: subject {Subject} to {Recipient}", message.Subject, message.To.Email);
        return Task.CompletedTask;
    }
}
