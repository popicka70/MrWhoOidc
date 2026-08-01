using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services.Delegation;

/// <summary>
/// Service for creating, accepting, declining, and revoking delegated access grants.
/// Implements AD-5: Use durable records with immediate revocation.
/// All database mutations use optimistic concurrency via the Version field.
/// Emits audit events via IAuditSink per Section 6.12.
/// </summary>
public interface IDelegatedAccessGrantService
{
    /// <summary>
    /// Creates a new DelegatedAccessGrant in PendingAcceptance status.
    /// Validates that delegator != delegate, both have active memberships,
    /// and all requested capabilities are delegable.
    /// Creates a DelegatedAccessInvitationToken with a cryptographically random token hash.
    /// Emits delegated_access.created audit event.
    /// </summary>
    Task<DelegatedAccessGrant> CreateGrantAsync(
        Guid tenantId,
        Guid clientId,
        Guid delegatorId,
        Guid delegateId,
        List<string> capabilities,
        string purpose,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts a grant invitation by token hash. Atomically transitions
    /// the grant from PendingAcceptance to Active using the Version concurrency token.
    /// Marks the invitation token as consumed.
    /// Emits delegated_access.accepted audit event.
    /// </summary>
    Task<DelegatedAccessGrant> AcceptGrantAsync(
        string token,
        Guid delegateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declines a grant invitation by token hash. Atomically transitions
    /// the grant from PendingAcceptance to Declined.
    /// Marks the invitation token as consumed.
    /// Emits delegated_access.declined audit event.
    /// </summary>
    Task<DelegatedAccessGrant> DeclineGrantAsync(
        string token,
        Guid delegateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a grant by ID. Verifies the revoker is either the delegator
    /// or an authorized administrator. Transitions status to Revoked.
    /// Emits delegated_access.revoked audit event.
    /// </summary>
    Task<DelegatedAccessGrant> RevokeGrantAsync(
        Guid grantId,
        Guid revokerId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of the delegated access grant lifecycle service.
/// Uses the AuthDbContext for persistence and IDelegableCapabilityCatalog for capability validation.
/// </summary>
internal sealed class DelegatedAccessGrantService(
    AuthDbContext dbContext,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserTenantMembershipService membershipService,
    IAuditSink auditSink,
    IEmailSender emailSender,
    IUserAccountService userAccountService,
    Microsoft.Extensions.Options.IOptions<DelegationOptions> delegationOptions,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions,
    ILogger<DelegatedAccessGrantService> logger)
    : IDelegatedAccessGrantService
{
    // Public methods ----------------------------------------------------------

    public async Task<DelegatedAccessGrant> CreateGrantAsync(
        Guid tenantId,
        Guid clientId,
        Guid delegatorId,
        Guid delegateId,
        List<string> capabilities,
        string purpose,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled via EnableDelegatedAccess flag.");
        }

        // Validate preconditions
        if (delegatorId == delegateId)
        {
            throw new ArgumentError("delegatorId and delegateId must differ; self-delegation is not permitted.");
        }

        if (capabilities.Count == 0)
        {
            throw new ArgumentError("capabilities must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentError("purpose must be non-empty bounded text.");
        }

        var client = await dbContext.Clients.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == clientId && candidate.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (client is null)
        {
            throw new NotFoundError("Client not found in the target tenant.");
        }

        // Verify both parties have active UserTenantMembership in the target tenant
        var delegatorMembership = await membershipService.GetMembershipAsync(delegatorId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (delegatorMembership is null)
        {
            throw new NotFoundError("Delegator has no membership in the target tenant.");
        }
        if (delegatorMembership.Status != TenantMembershipStatus.Active
            || delegatorMembership.ExpiresAt is not null && delegatorMembership.ExpiresAt <= now)
        {
            throw new MembershipError("Delegator membership is not active.");
        }

        var delegateMembership = await membershipService.GetMembershipAsync(delegateId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (delegateMembership is null)
        {
            throw new NotFoundError("Delegate has no membership in the target tenant.");
        }
        if (delegateMembership.Status != TenantMembershipStatus.Active
            || delegateMembership.ExpiresAt is not null && delegateMembership.ExpiresAt <= now)
        {
            throw new MembershipError("Delegate membership is not active.");
        }

        // Verify all requested capabilities are delegable
        foreach (var cap in capabilities)
        {
            var definition = capabilityCatalog.GetDefinition(cap);
            if (definition is null || !definition.IsDelegable)
            {
                throw new ArgumentError($"Capability '{cap}' is not delegable or unknown.");
            }
            if (expiresAt - now > definition.MaximumGrantLifetime)
            {
                throw new ArgumentError(
                    $"Capability '{cap}' cannot be delegated for longer than {definition.MaximumGrantLifetime}.");
            }
        }

        // Apply configuration-based lifetime bounds
        var config = delegationOptions.Value;
        var maxLifetime = TimeSpan.FromMinutes(config.MaximumGrantLifetimeMinutes);
        var acceptanceWindow = TimeSpan.FromMinutes(config.AcceptanceWindowMinutes);

        // Validate expiresAt against configured maximum
        var grantLifetime = expiresAt - now;
        if (grantLifetime > maxLifetime)
        {
            throw new ArgumentError(
                $"Grant lifetime {grantLifetime} exceeds maximum configured lifetime of {maxLifetime}.");
        }

        if (grantLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentError("Grant expiry must be in the future.");
        }

        var acceptanceExpiresAt = now.Add(acceptanceWindow);
        if (acceptanceExpiresAt > expiresAt) acceptanceExpiresAt = expiresAt;

        // Create the grant with PendingAcceptance status
        var grant = new DelegatedAccessGrant
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            ClientId = clientId,
            DelegatorUserAccountId = delegatorId,
            DelegateUserAccountId = delegateId,
            Status = DelegatedAccessGrantStatus.PendingAcceptance,
            CapabilitiesJson = NormalizeCapabilitiesJson(capabilities),
            ResourceConstraintsJson = BuildResourceConstraintsJson(capabilities, delegatorId),
            Purpose = purpose,
            CreatedAt = now,
            AcceptanceExpiresAt = acceptanceExpiresAt,
            ExpiresAt = expiresAt,
            Version = GuidHelper.NewId()
        };

        // Create grant and invitation in one unit of work.
        var rawToken = CryptoHelper.GenerateSecureRandomString(32);
        var tokenHash = CryptoHelper.ComputeSha256Base64(rawToken);

        var invitationToken = new DelegatedAccessInvitationToken
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            GrantId = grant.Id,
            TokenHash = tokenHash,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = acceptanceExpiresAt
        };

        dbContext.DelegatedAccessGrants.Add(grant);
        dbContext.DelegatedAccessInvitationTokens.Add(invitationToken);
        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Emit audit event: delegated_access.created
        var auditPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grant.Id.ToString(),
            ["tenant_id"] = tenantId.ToString(),
            ["client_id"] = clientId.ToString(),
            ["oidc_client_id"] = client.ClientId,
            ["actor_id"] = auditSink.HashValue(delegatorId.ToString()),
            ["subject_id"] = auditSink.HashValue(delegateId.ToString()),
            ["delegator_id"] = delegatorId.ToString(),
            ["delegate_id"] = delegateId.ToString(),
            ["capabilities"] = capabilities,
            ["purpose"] = purpose,
            ["expires_at"] = expiresAt.ToUniversalTime().ToString("O"),
            ["outcome"] = "created",
            ["reason"] = null
        };

        auditSink.Emit("delegated_access.created", auditPayload);

        logger.LogInformation("Created delegated access grant {GrantId} with invitation token for delegator {DelegatorId} delegate {DelegateId} in tenant {TenantId}.",
            grant.Id, delegatorId, delegateId, tenantId);

        // Send email notification to the delegate about the invitation
        var delegatorDetails = await ResolveUserNotificationDetailsAsync(delegatorId, cancellationToken)
            .ConfigureAwait(false);
        var delegateDetails = await ResolveUserNotificationDetailsAsync(delegateId, cancellationToken)
            .ConfigureAwait(false);
        var tenantName = await ResolveTenantDisplayNameAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var invitationLink = BuildInvitationLinkPath(rawToken);

        if (delegateDetails.Email is not null)
        {
            var message = BuildInvitationCreatedMessage(
                delegatorDetails.Name ?? "a user",
                delegateDetails.Name ?? "you",
                delegateDetails.Email,
                tenantName,
                invitationLink,
                grant.AcceptanceExpiresAt);
            await SendNotificationAsync(message, cancellationToken);
        }
        else
        {
            logger.LogWarning("Delegate {DelegateId} has no email address; skipping invitation notification.",
                delegateId);
        }

        return grant;
    }

    public async Task<DelegatedAccessGrant> AcceptGrantAsync(
        string token,
        Guid delegateId,
        CancellationToken cancellationToken = default)
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled via EnableDelegatedAccess flag.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentError("token must not be empty.");
        }

        var tokenHash = CryptoHelper.ComputeSha256Base64(token);

        // Find the invitation token by hash
        var invitationToken = await dbContext.DelegatedAccessInvitationTokens
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);

        if (invitationToken is null)
        {
            throw new NotFoundError("Invitation token not found.");
        }

        // Verify it's not consumed/revoked and hasn't expired
        if (invitationToken.ConsumedAt is not null)
        {
            throw new ConflictError("Invitation token is already consumed.");
        }

        if (invitationToken.RevokedAt is not null)
        {
            throw new ConflictError("Invitation token has been revoked.");
        }

        if (invitationToken.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new ExpiredError("Invitation token has expired.");
        }

        // Verify delegateId matches the grant's DelegateUserAccountId
        var grant = await dbContext.DelegatedAccessGrants
            .Where(x => x.Id == invitationToken.GrantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            throw new NotFoundError("Grant associated with invitation token not found.");
        }

        if (grant.DelegateUserAccountId != delegateId)
        {
            throw new MismatchError("Delegate ID does not match grant's delegate.");
        }

        await ValidatePendingGrantAsync(grant, cancellationToken).ConfigureAwait(false);

        // Atomically transition status from PendingAcceptance to Active using Version concurrency token
        grant.Status = DelegatedAccessGrantStatus.Active;
        grant.AcceptedAt = DateTimeOffset.UtcNow;
        grant.StartsAt = DateTimeOffset.UtcNow;
        grant.Version = GuidHelper.NewId();

        // Mark invitation token as consumed atomically with grant update
        invitationToken.ConsumedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Emit audit event: delegated_access.accepted
        var auditPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grant.Id.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["client_id"] = grant.ClientId?.ToString(),
            ["actor_id"] = auditSink.HashValue(delegateId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["delegate_id"] = delegateId.ToString(),
            ["delegator_id"] = grant.DelegatorUserAccountId.ToString(),
            ["outcome"] = "accepted",
            ["reason"] = null
        };

        auditSink.Emit("delegated_access.accepted", auditPayload);

        logger.LogInformation("Delegated access grant {GrantId} accepted by delegate {DelegateId}.",
            grant.Id, delegateId);

        // Send email notification to the delegator about the acceptance
        var delegatorDetails = await ResolveUserNotificationDetailsAsync(grant.DelegatorUserAccountId, cancellationToken)
            .ConfigureAwait(false);
        var delegateDetails = await ResolveUserNotificationDetailsAsync(delegateId, cancellationToken)
            .ConfigureAwait(false);
        var tenantName = await ResolveTenantDisplayNameAsync(grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (delegatorDetails.Email is not null)
        {
            var message = BuildAcceptedMessage(
                delegatorDetails.Name ?? "You",
                delegateDetails.Name ?? "a user",
                delegatorDetails.Email,
                tenantName);
            await SendNotificationAsync(message, cancellationToken);
        }
        else
        {
            logger.LogWarning("Delegator {DelegatorId} has no email address; skipping acceptance notification.",
                grant.DelegatorUserAccountId);
        }

        return grant;
    }

    public async Task<DelegatedAccessGrant> DeclineGrantAsync(
        string token,
        Guid delegateId,
        CancellationToken cancellationToken = default)
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled via EnableDelegatedAccess flag.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentError("token must not be empty.");
        }

        var tokenHash = CryptoHelper.ComputeSha256Base64(token);

        var invitationToken = await dbContext.DelegatedAccessInvitationTokens
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);

        if (invitationToken is null)
        {
            throw new NotFoundError("Invitation token not found.");
        }

        if (invitationToken.ConsumedAt is not null)
        {
            throw new ConflictError("Invitation token is already consumed.");
        }

        if (invitationToken.RevokedAt is not null)
        {
            throw new ConflictError("Invitation token has been revoked.");
        }

        if (invitationToken.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new ExpiredError("Invitation token has expired.");
        }

        var grant = await dbContext.DelegatedAccessGrants
            .Where(x => x.Id == invitationToken.GrantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            throw new NotFoundError("Grant associated with invitation token not found.");
        }

        if (grant.DelegateUserAccountId != delegateId)
        {
            throw new MismatchError("Delegate ID does not match grant's delegate.");
        }

        await ValidatePendingGrantAsync(grant, cancellationToken).ConfigureAwait(false);

        // Atomically transition status from PendingAcceptance to Declined
        grant.Status = DelegatedAccessGrantStatus.Declined;
        grant.DeclinedAt = DateTimeOffset.UtcNow;
        grant.Version = GuidHelper.NewId();

        // Mark invitation token as consumed atomically with grant update
        invitationToken.ConsumedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Emit audit event: delegated_access.declined
        var auditPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grant.Id.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["client_id"] = grant.ClientId?.ToString(),
            ["actor_id"] = auditSink.HashValue(delegateId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["delegate_id"] = delegateId.ToString(),
            ["delegator_id"] = grant.DelegatorUserAccountId.ToString(),
            ["outcome"] = "declined",
            ["reason"] = null
        };

        auditSink.Emit("delegated_access.declined", auditPayload);

        logger.LogInformation("Delegated access grant {GrantId} declined by delegate {DelegateId}.",
            grant.Id, delegateId);

        // Send email notification to the delegator about the decline
        var delegatorDetails = await ResolveUserNotificationDetailsAsync(grant.DelegatorUserAccountId, cancellationToken)
            .ConfigureAwait(false);
        var delegateDetails = await ResolveUserNotificationDetailsAsync(delegateId, cancellationToken)
            .ConfigureAwait(false);
        var tenantName = await ResolveTenantDisplayNameAsync(grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (delegatorDetails.Email is not null)
        {
            var message = BuildDeclinedMessage(
                delegatorDetails.Name ?? "You",
                delegateDetails.Name ?? "a user",
                delegatorDetails.Email,
                tenantName);
            await SendNotificationAsync(message, cancellationToken);
        }
        else
        {
            logger.LogWarning("Delegator {DelegatorId} has no email address; skipping decline notification.",
                grant.DelegatorUserAccountId);
        }

        return grant;
    }

    public async Task<DelegatedAccessGrant> RevokeGrantAsync(
        Guid grantId,
        Guid revokerId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled via EnableDelegatedAccess flag.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentError("reason must be non-empty bounded text.");
        }

        var grant = await dbContext.DelegatedAccessGrants
            .Where(x => x.Id == grantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            throw new NotFoundError("Grant not found.");
        }

        // Either party may immediately terminate delegated authority.
        if (grant.DelegatorUserAccountId != revokerId
            && grant.DelegateUserAccountId != revokerId)
        {
            throw new AuthorizationError("Only the delegator or delegate can revoke this grant.");
        }

        // Grant must be in a revocable state (PendingAcceptance, Active, or Suspended)
        if (grant.Status == DelegatedAccessGrantStatus.Declined ||
            grant.Status == DelegatedAccessGrantStatus.Revoked ||
            grant.Status == DelegatedAccessGrantStatus.Expired)
        {
            throw new ConflictError("Grant is already in a terminal state and cannot be revoked.");
        }

        // Atomically transition to Revoked
        grant.Status = DelegatedAccessGrantStatus.Revoked;
        grant.RevokedAt = DateTimeOffset.UtcNow;
        grant.RevokedByUserAccountId = revokerId;
        grant.RevocationReason = reason;
        grant.Version = GuidHelper.NewId();

        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);

        // Emit audit event: delegated_access.revoked
        var auditPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grant.Id.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["client_id"] = grant.ClientId?.ToString(),
            ["actor_id"] = auditSink.HashValue(revokerId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["revoker_id"] = revokerId.ToString(),
            ["delegator_id"] = grant.DelegatorUserAccountId.ToString(),
            ["delegate_id"] = grant.DelegateUserAccountId.ToString(),
            ["outcome"] = "revoked",
            ["reason"] = reason
        };

        auditSink.Emit("delegated_access.revoked", auditPayload);

        logger.LogInformation("Delegated access grant {GrantId} revoked by {RevokerId}. Reason: {Reason}.",
            grant.Id, revokerId, reason);

        // Send email notifications to both delegator and delegate about the revocation
        var delegatorDetails = await ResolveUserNotificationDetailsAsync(grant.DelegatorUserAccountId, cancellationToken)
            .ConfigureAwait(false);
        var delegateDetails = await ResolveUserNotificationDetailsAsync(grant.DelegateUserAccountId, cancellationToken)
            .ConfigureAwait(false);
        var tenantName = await ResolveTenantDisplayNameAsync(grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        // Notify delegator
        if (delegatorDetails.Email is not null)
        {
            var delegatorMessage = BuildRevokedMessage(
                delegatorDetails.Name ?? "You",
                delegateDetails.Name ?? "the other party",
                delegatorDetails.Email,
                tenantName);
            await SendNotificationAsync(delegatorMessage, cancellationToken);
        }
        else
        {
            logger.LogWarning("Delegator {DelegatorId} has no email address; skipping revocation notification.",
                grant.DelegatorUserAccountId);
        }

        // Notify delegate
        if (delegateDetails.Email is not null)
        {
            var delegateMessage = BuildRevokedMessage(
                delegateDetails.Name ?? "You",
                delegatorDetails.Name ?? "the other party",
                delegateDetails.Email,
                tenantName);
            await SendNotificationAsync(delegateMessage, cancellationToken);
        }
        else
        {
            logger.LogWarning("Delegate {DelegateId} has no email address; skipping revocation notification.",
                grant.DelegateUserAccountId);
        }

        return grant;
    }

    // Internal helpers --------------------------------------------------------

    private async Task ValidatePendingGrantAsync(
        DelegatedAccessGrant grant,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (grant.Status != DelegatedAccessGrantStatus.PendingAcceptance)
        {
            throw new ConflictError("Grant is not pending acceptance.");
        }
        if (grant.AcceptanceExpiresAt <= now || grant.ExpiresAt <= now)
        {
            throw new ExpiredError("Grant invitation has expired.");
        }

        var clientExists = await dbContext.Clients.AsNoTracking()
            .AnyAsync(client => client.Id == grant.ClientId && client.TenantId == grant.TenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!clientExists)
        {
            throw new NotFoundError("Grant client is no longer available in the target tenant.");
        }

        var delegatorMembership = await membershipService.GetMembershipAsync(
            grant.DelegatorUserAccountId, grant.TenantId, cancellationToken).ConfigureAwait(false);
        var delegateMembership = await membershipService.GetMembershipAsync(
            grant.DelegateUserAccountId, grant.TenantId, cancellationToken).ConfigureAwait(false);
        if (!IsActiveMembership(delegatorMembership, now) || !IsActiveMembership(delegateMembership, now))
        {
            throw new MembershipError("Both parties must have active tenant memberships.");
        }
    }

    private static bool IsActiveMembership(UserTenantMembership? membership, DateTimeOffset now)
        => membership is not null
            && membership.Status == TenantMembershipStatus.Active
            && (membership.ExpiresAt is null || membership.ExpiresAt > now);

    /// <summary>
    /// Builds an invitation link path for the given raw token.
    /// The link follows the pattern: /account/delegated-access/invitations/{token}
    /// </summary>
    private static string BuildInvitationLinkPath(string rawToken)
    {
        return $"/account/delegated-access/invitations/{Uri.EscapeDataString(rawToken)}";
    }

    /// <summary>
    /// Builds the email message for invitation creation (sent to delegate).
    /// </summary>
    private static EmailMessage BuildInvitationCreatedMessage(
        string delegatorName,
        string delegateName,
        string delegateEmail,
        string tenantName,
        string invitationLink,
        DateTimeOffset expiresAt)
    {
        var displayDelegator = string.IsNullOrWhiteSpace(delegatorName) ? "a user" : delegatorName;
        var displayDelegate = string.IsNullOrWhiteSpace(delegateName) ? "you" : delegateName;
        var subject = $"Delegated access invitation from {displayDelegator}";
        var textBody = $"Hi {displayDelegate},\n\n" +
            $"{displayDelegator} has granted you delegated access in {tenantName}.\n\n" +
            "To accept or decline this invitation, visit the following link:\n" +
            $"{invitationLink}\n\n" +
            $"This invitation expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not expect this, ignore this message.\n\n" +
            "Only the invited user can accept or decline this invitation.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayDelegate)},</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(displayDelegator)} has granted you <strong>delegated access</strong> in " +
            $"<strong>{System.Net.WebUtility.HtmlEncode(tenantName)}</strong>.</p>" +
            "<p>To accept or decline this invitation, click the button below:</p>" +
            $"<p><a href=\"{invitationLink}\" style=\"display:inline-block;padding:10px 18px;background-color:#0d6efd;color:#ffffff;text-decoration:none;border-radius:4px;\">View invitation</a></p>" +
            $"<p>This invitation expires on {expiresAt:MMM d, yyyy HH:mm 'UTC'}. If you did not expect this, ignore this message.</p>" +
            "<p><small>Only the invited user can accept or decline this invitation.</small></p>";

        return new EmailMessage
        {
            To = new EmailAddress(delegateEmail, delegateName),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }

    /// <summary>
    /// Builds the email message for grant acceptance (sent to delegator).
    /// </summary>
    private static EmailMessage BuildAcceptedMessage(
        string delegatorName,
        string delegateName,
        string delegatorEmail,
        string tenantName)
    {
        var displayDelegator = string.IsNullOrWhiteSpace(delegatorName) ? "You" : delegatorName;
        var displayDelegate = string.IsNullOrWhiteSpace(delegateName) ? "a user" : delegateName;
        var subject = $"Delegated access accepted by {displayDelegate}";
        var textBody = $"Hi {displayDelegator},\n\n" +
            $"{displayDelegate} has accepted your delegated access grant in {tenantName}.\n\n" +
            "The grant is now active. The delegate may begin exercising the granted capabilities.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayDelegator)},</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(displayDelegate)} has <strong>accepted</strong> your delegated access grant in " +
            $"<strong>{System.Net.WebUtility.HtmlEncode(tenantName)}</strong>.</p>" +
            "<p>The grant is now active. The delegate may begin exercising the granted capabilities.</p>";

        return new EmailMessage
        {
            To = new EmailAddress(delegatorEmail, delegatorName),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }

    /// <summary>
    /// Builds the email message for grant decline (sent to delegator).
    /// </summary>
    private static EmailMessage BuildDeclinedMessage(
        string delegatorName,
        string delegateName,
        string delegatorEmail,
        string tenantName)
    {
        var displayDelegator = string.IsNullOrWhiteSpace(delegatorName) ? "You" : delegatorName;
        var displayDelegate = string.IsNullOrWhiteSpace(delegateName) ? "a user" : delegateName;
        var subject = $"Delegated access declined by {displayDelegate}";
        var textBody = $"Hi {displayDelegator},\n\n" +
            $"{displayDelegate} has declined your delegated access grant in {tenantName}.\n\n" +
            "The invitation has been closed and no further action is needed.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayDelegator)},</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(displayDelegate)} has <strong>declined</strong> your delegated access grant in " +
            $"<strong>{System.Net.WebUtility.HtmlEncode(tenantName)}</strong>.</p>" +
            "<p>The invitation has been closed and no further action is needed.</p>";

        return new EmailMessage
        {
            To = new EmailAddress(delegatorEmail, delegatorName),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }

    /// <summary>
    /// Builds the email message for grant revocation (sent to both parties).
    /// </summary>
    private static EmailMessage BuildRevokedMessage(
        string recipientName,
        string otherPartyName,
        string recipientEmail,
        string tenantName)
    {
        var displayRecipient = string.IsNullOrWhiteSpace(recipientName) ? "You" : recipientName;
        var displayOther = string.IsNullOrWhiteSpace(otherPartyName) ? "the other party" : otherPartyName;
        var subject = "Delegated access revoked";
        var textBody = $"Hi {displayRecipient},\n\n" +
            "Your delegated access grant has been revoked.\n\n" +
            $"{displayOther} has revoked the delegated access grant in {tenantName}.\n\n" +
            "The grant is no longer active and no further actions can be performed under it.";
        var htmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(displayRecipient)},</p>" +
            "<p>Your delegated access grant has been <strong>revoked</strong>.</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(displayOther)} has revoked the delegated access grant in " +
            $"<strong>{System.Net.WebUtility.HtmlEncode(tenantName)}</strong>.</p>" +
            "<p>The grant is no longer active and no further actions can be performed under it.</p>";

        return new EmailMessage
        {
            To = new EmailAddress(recipientEmail, recipientName),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        };
    }

    /// <summary>
    /// Sends an email notification, logging success or failure without failing the primary transaction.
    /// </summary>
    private async Task SendNotificationAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Email notification sent: subject {Subject} to {Recipient}",
                    message.Subject, message.To.Email);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to send email notification: subject {Subject} to {Recipient}: {ErrorMessage}",
                    message.Subject, message.To.Email, ex.Message);
        }
    }

    /// <summary>
    /// Resolves user account details (name, email) for notification purposes.
    /// Returns a tuple of (name, email). Name may be null if unset; email may be null if unset.
    /// </summary>
    private async Task<(string? Name, string? Email)> ResolveUserNotificationDetailsAsync(
        Guid userAccountId,
        CancellationToken cancellationToken)
    {
        var account = await userAccountService.GetByIdAsync(userAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning("UserAccount {UserAccountId} not found when resolving notification details; using fallback.",
                userAccountId);
            return (null, null);
        }
        return (account.Name, account.Email);
    }

    /// <summary>
    /// Resolves tenant display name for notification purposes.
    /// Returns the tenant slug or ID as string if slug is unavailable.
    /// </summary>
    private async Task<string> ResolveTenantDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            return "the tenant";
        }
        return string.IsNullOrWhiteSpace(tenant.Slug) ? tenantId.ToString() : tenant.Slug;
    }

    /// <summary>
    /// Normalizes and canonicalizes the capabilities list into a canonical JSON array.
    /// Sorted, deduplicated, and trimmed.
    /// </summary>
    private static string NormalizeCapabilitiesJson(List<string> capabilities)
    {
        var sorted = capabilities.Select(x => x.Trim()).OrderBy(x => x, StringComparer.Ordinal);
        var unique = sorted.Distinct(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(unique);
    }

    private static string BuildResourceConstraintsJson(IEnumerable<string> capabilities, Guid delegatorId)
    {
        var constraints = capabilities
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                capability => capability,
                _ => new ResourceConstraintPolicy(["user"], [delegatorId.ToString()]),
                StringComparer.Ordinal);

        return JsonSerializer.Serialize(constraints, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private sealed record ResourceConstraintPolicy(string[] AllowedTypes, string[] AllowedIds);
}
