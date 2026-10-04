using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// A tenant the ticket holder proved access to. <paramref name="UserId"/> is the id of the global UserAccount
/// that verified the password, and must match the account the ticket is redeemed for.
/// </summary>
public sealed record VerifiedTenantUser(Guid TenantId, Guid UserId);

public interface ITenantCredentialTicketStore
{
    TenantCredentialTicket CreateTicket(string email, IReadOnlyCollection<VerifiedTenantUser> verifiedUsers);
    TenantCredentialTicket? GetTicket(string ticketId);
    void RemoveTicket(string ticketId);
}

public sealed record TenantCredentialTicket(string TicketId, string EmailHash, long IssuedAtUnixSeconds, IReadOnlyCollection<VerifiedTenantUser> VerifiedUsers)
{
    /// <summary>Full-length comparison: a truncated hash allowed an offline collision onto another member's email.</summary>
    public bool MatchesEmail(string? email)
        => !string.IsNullOrEmpty(email) && string.Equals(EmailHash, TenantCredentialTicketStore.HashEmail(email), StringComparison.Ordinal);
}

internal sealed class TenantCredentialTicketStore(IHttpContextAccessor httpContextAccessor, ILogger<TenantCredentialTicketStore> logger)
    : ITenantCredentialTicketStore
{
    private const string TicketPrefix = "TenantCredentialTicket:";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public TenantCredentialTicket CreateTicket(string email, IReadOnlyCollection<VerifiedTenantUser> verifiedUsers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(verifiedUsers);
        if (verifiedUsers.Count == 0)
        {
            throw new InvalidOperationException("Cannot create a credential ticket without verified tenant memberships.");
        }

        var emailHash = HashEmail(email);
        var ticket = new TenantCredentialTicket(Guid.NewGuid().ToString("N"), emailHash, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), verifiedUsers);
        Store(ticket);
        logger.LogDebug("Created tenant credential ticket {TicketId} for email hash {EmailHash}", ticket.TicketId, emailHash[..8]);
        return ticket;
    }

    public TenantCredentialTicket? GetTicket(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return null;
        }

        var json = Session.GetString(TicketPrefix + ticketId);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var ticket = JsonSerializer.Deserialize<TenantCredentialTicket>(json);
            if (ticket is null)
            {
                return null;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(ticket.IssuedAtUnixSeconds);
            if (DateTimeOffset.UtcNow - issuedAt > Lifetime)
            {
                logger.LogInformation("Tenant credential ticket {TicketId} expired", ticketId);
                RemoveTicket(ticketId);
                return null;
            }

            return ticket;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize tenant credential ticket {TicketId}", ticketId);
            RemoveTicket(ticketId);
            return null;
        }
    }

    public void RemoveTicket(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return;
        }

        Session.Remove(TicketPrefix + ticketId);
    }

    private void Store(TenantCredentialTicket ticket)
    {
        var json = JsonSerializer.Serialize(ticket);
        Session.SetString(TicketPrefix + ticket.TicketId, json);
    }

    private ISession Session
    {
        get
        {
            var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HttpContext for credential ticket store");
            if (!context.Session.IsAvailable)
            {
                context.Session.LoadAsync().GetAwaiter().GetResult();
            }

            return context.Session;
        }
    }

    internal static string HashEmail(string email)
    {
        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(email.Trim().ToLowerInvariant());
    }
}

