using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Services;

public interface IEmailConfirmationWorkflow
{
    Task<EmailConfirmationCreateResult> SendPrimaryAsync(User user, CancellationToken cancellationToken = default);
    Task<EmailConfirmationCreateResult> SendAlternativeAsync(User user, UserAlternativeEmail alternative, CancellationToken cancellationToken = default);
}

internal sealed class EmailConfirmationWorkflow(
    IEmailConfirmationService confirmationService,
    IEmailSender emailSender,
    IOptions<OidcOptions> oidcOptions,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantAccessor tenantAccessor,
    AuthDbContext db,
    ILogger<EmailConfirmationWorkflow> logger) : IEmailConfirmationWorkflow
{
    private readonly OidcOptions _oidc = oidcOptions.Value;

    public async Task<EmailConfirmationCreateResult> SendPrimaryAsync(User user, CancellationToken cancellationToken = default)
    {
        var result = await confirmationService.CreatePrimaryConfirmationAsync(user, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogSkipped(result.Status, user.Email);
            return result;
        }

        var link = await BuildConfirmationLinkAsync(user.TenantId, result.Token!, cancellationToken).ConfigureAwait(false);
        var message = BuildPrimaryMessage(user, link, result.ExpiresAt!.Value);
        await emailSender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<EmailConfirmationCreateResult> SendAlternativeAsync(User user, UserAlternativeEmail alternative, CancellationToken cancellationToken = default)
    {
        var result = await confirmationService.CreateAlternativeConfirmationAsync(user, alternative, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogSkipped(result.Status, alternative.Email);
            return result;
        }

        var link = await BuildConfirmationLinkAsync(user.TenantId, result.Token!, cancellationToken).ConfigureAwait(false);
        var message = BuildAlternativeMessage(user, alternative.Email, link, result.ExpiresAt!.Value);
        await emailSender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private void LogSkipped(EmailConfirmationCreateStatus status, string? email)
    {
        switch (status)
        {
            case EmailConfirmationCreateStatus.AlreadyVerified:
                logger.LogDebug("Email confirmation skipped because address {Email} is already verified", email);
                break;
            case EmailConfirmationCreateStatus.EmailMissing:
            case EmailConfirmationCreateStatus.AlternativeMissing:
                logger.LogDebug("Email confirmation skipped because address was missing for {Email}", email);
                break;
            default:
                logger.LogDebug("Email confirmation skipped for {Email} with status {Status}", email, status);
                break;
        }
    }

    private async Task<string> BuildConfirmationLinkAsync(Guid tenantId, string token, CancellationToken cancellationToken)
    {
        var baseUrl = (_oidc.PublicBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://localhost"; // fallback for local dev when not configured
        }

        var tenantSegment = await ResolveTenantSegmentAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var path = string.Concat(tenantSegment, "/account/confirm-email");
        return $"{baseUrl}{path}?token={Uri.EscapeDataString(token)}";
    }

    private async Task<string> ResolveTenantSegmentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!multiTenancyOptions.Enabled)
        {
            return string.Empty;
        }

        var current = tenantAccessor.CurrentTenant;
        if (current is not null && current.TenantId == tenantId)
        {
            return $"/t/{current.Slug}";
        }

        var slug = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return slug is null ? string.Empty : $"/t/{slug}";
    }

    private static EmailMessage BuildPrimaryMessage(User user, string link, DateTimeOffset expiresAt)
    {
        var displayName = string.IsNullOrWhiteSpace(user.Name) ? user.Email ?? "there" : user.Name;
        var subject = "Confirm your email address";
        var textBody = $"Hi {displayName},\n\nThanks for signing in to MrWhoOidc. Confirm your email by visiting {link}.\n\nThis link expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not request this, ignore this message.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayName)},</p>" +
                       "<p>Thanks for signing in to MrWhoOidc. Click the button below to confirm your email address.</p>" +
                       $"<p><a href=\"{link}\" style=\"display:inline-block;padding:10px 18px;background-color:#0d6efd;color:#ffffff;text-decoration:none;border-radius:4px;\">Confirm email</a></p>" +
                       $"<p>This link expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not request this, ignore this message.</p>";

        return new EmailMessage
        {
            To = new EmailAddress(user.Email ?? string.Empty, user.Name),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }

    private static EmailMessage BuildAlternativeMessage(User user, string email, string link, DateTimeOffset expiresAt)
    {
        var displayName = string.IsNullOrWhiteSpace(user.Name) ? email : user.Name;
        var subject = "Verify your additional email address";
        var textBody = $"Hi {displayName},\n\nA new email address was added to your MrWhoOidc account. Verify it by visiting {link}.\n\nThis link expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not request this change, remove the email from your account.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayName)},</p>" +
                       "<p>A new email address was added to your MrWhoOidc account. Click the button below to verify it.</p>" +
                       $"<p><a href=\"{link}\" style=\"display:inline-block;padding:10px 18px;background-color:#0d6efd;color:#ffffff;text-decoration:none;border-radius:4px;\">Verify email</a></p>" +
                       $"<p>This link expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not request this change, remove the email from your account.</p>";

        return new EmailMessage
        {
            To = new EmailAddress(email, user.Name),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }
}
