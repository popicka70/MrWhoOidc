using System;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services.Delegation;

namespace MrWhoOidc.Auth.Services.Delegation;

/// <summary>
/// Authorization service for delegated access grants.
/// Evaluates whether a delegate may perform a specific capability on a resource
/// on behalf of the delegator within a tenant.
/// Implements AD-3: Delegate capabilities, not roles.
/// Follows evaluation order defined in Section 6.7.
/// </summary>
public interface IDelegatedAccessAuthorizationService
{
    /// <summary>
    /// Authorize a delegated operation following the evaluation order:
    /// 1. Resolve actor UserAccountId from trusted claims.
    /// 2. Load grant by ID and tenant.
    /// 3. Verify status is Active and current time is within window.
    /// 4. Verify actor equals DelegateUserAccountId.
    /// 5. Verify delegator and delegate memberships are active/unexpired.
    /// 6. Verify target tenant is active.
    /// 7. Verify capability is delegable and present in grant.
    /// 8. Verify resource matches constraints.
    /// Returns EffectiveAccessContext on success.
    /// </summary>
    Task<EffectiveAccessContext> AuthorizeAsync(
        ClaimsPrincipal actor,
        Guid grantId,
        Guid clientId,
        string capability,
        DelegatedResource resource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of delegated access authorization.
/// Uses optimistic concurrency reads and short-lived cache patterns.
/// Denies by default (AD-4) for any failed invariant.
/// </summary>
internal sealed class DelegatedAccessAuthorizationService(
    AuthDbContext dbContext,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserTenantMembershipService membershipService,
    IAuditSink auditSink,
    IOptions<AuthOptions> authOptions,
    ILogger<DelegatedAccessAuthorizationService> logger)
    : IDelegatedAccessAuthorizationService
{
    public async Task<EffectiveAccessContext> AuthorizeAsync(
        ClaimsPrincipal actor,
        Guid grantId,
        Guid clientId,
        string capability,
        DelegatedResource resource,
        CancellationToken cancellationToken = default)
    {
        if (!authOptions.Value.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled.");
        }

        // Step 1: Resolve actor UserAccountId from trusted claims
        var actorId = ResolveUserAccountId(actor);

        // Step 2: Load grant by ID
        var grant = await dbContext.DelegatedAccessGrants.AsNoTracking()
            .Where(x => x.Id == grantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "grant_not_found"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new NotFoundError("Delegated access grant not found.");
        }

        if (!grant.ClientId.HasValue || grant.ClientId.Value != clientId)
        {
            auditSink.Emit("delegated_access.denied", new Dictionary<string, object?>
            {
                ["grant_id"] = grantId.ToString(),
                ["tenant_id"] = grant.TenantId.ToString(),
                ["client_id"] = clientId.ToString(),
                ["actor_id"] = auditSink.HashValue(actorId.ToString()),
                ["capability"] = capability,
                ["resource_type"] = resource.Type,
                ["resource_id"] = resource.Id,
                ["outcome"] = "denied",
                ["reason"] = "client_mismatch"
            });
            throw new MismatchError("Client is not permitted by the delegated access grant.");
        }

        // Step 3: Verify status is Active and current time is within window
        if (grant.Status != DelegatedAccessGrantStatus.Active)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "grant_not_active"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new StatusError($"Grant is not active. Current status: {grant.Status}.");
        }

        if (grant.StartsAt is not null && grant.StartsAt > DateTimeOffset.UtcNow)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "grant_not_started"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new StatusError($"Grant has not yet started (StartsAt {grant.StartsAt:O}).");
        }

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "grant_expired"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new ExpiredError($"Grant has expired at {grant.ExpiresAt:O}.");
        }

        // Step 4: Verify actor equals DelegateUserAccountId
        if (grant.DelegateUserAccountId != actorId)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegate_mismatch"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new MismatchError(
                $"Actor {actorId} does not match grant delegate {grant.DelegateUserAccountId}.");
        }

        // Step 5: Verify delegator and delegate memberships are still active and unexpired
        var delegatorMembership = await membershipService.GetMembershipAsync(
            grant.DelegatorUserAccountId, grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (delegatorMembership is null)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegator_membership_missing"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new MembershipError("Delegator has no membership in the grant tenant.");
        }

        if (delegatorMembership.Status != TenantMembershipStatus.Active)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegator_membership_inactive"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new MembershipError("Delegator membership is not active.");
        }

        if (delegatorMembership.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegator_membership_expired"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new ExpiredMembershipError("Delegator membership has expired.");
        }

        var delegateMembership = await membershipService.GetMembershipAsync(
            grant.DelegateUserAccountId, grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (delegateMembership is null)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegate_membership_missing"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new MembershipError("Delegate has no membership in the grant tenant.");
        }

        if (delegateMembership.Status != TenantMembershipStatus.Active)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegate_membership_inactive"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new MembershipError("Delegate membership is not active.");
        }

        if (delegateMembership.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "delegate_membership_expired"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new ExpiredMembershipError("Delegate membership has expired.");
        }

        // Step 6: Verify target tenant is active
        var tenant = await dbContext.Tenants.AsNoTracking()
            .Where(x => x.Id == grant.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "tenant_not_found"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new NotFoundError("Grant tenant not found.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "tenant_inactive"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new TenantError($"Grant tenant is not active. Current status: {tenant.Status}.");
        }

        // Step 7: Verify capability is delegable and present in grant
        if (!capabilityCatalog.IsDelegable(capability))
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "capability_not_delegable"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new CapabilityError($"Capability '{capability}' is not delegable or unknown.");
        }

        var capabilitiesJson = System.Text.Json.JsonSerializer.Deserialize<List<string>>(grant.CapabilitiesJson);
        if (capabilitiesJson is null || !capabilitiesJson.Contains(capability))
        {
            var deniedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grantId.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "denied",
            ["reason"] = "capability_not_granted"
        };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new CapabilityError($"Capability '{capability}' is not present in the grant's capabilities.");
        }

        // Step 8: Resource-scoped capabilities require a complete, valid policy.
        var resourceDenialReason = ValidateResourcePolicy(
            grant.ResourceConstraintsJson,
            capability,
            resource,
            capabilityCatalog.GetDefinition(capability)!);
        if (resourceDenialReason is not null)
        {
            var deniedPayload = new Dictionary<string, object?>
            {
                ["grant_id"] = grantId.ToString(),
                ["tenant_id"] = grant.TenantId.ToString(),
                ["actor_id"] = auditSink.HashValue(actorId.ToString()),
                ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
                ["capability"] = capability,
                ["resource_type"] = resource.Type,
                ["resource_id"] = resource.Id,
                ["outcome"] = "denied",
                ["reason"] = resourceDenialReason
            };
            auditSink.Emit("delegated_access.denied", deniedPayload);
            throw new ResourceError("Resource is not permitted by the delegated access grant.");
        }

        // Emit audit event: delegated_access.used
        var usedPayload = new Dictionary<string, object?>
        {
            ["grant_id"] = grant.Id.ToString(),
            ["tenant_id"] = grant.TenantId.ToString(),
            ["actor_id"] = auditSink.HashValue(actorId.ToString()),
            ["subject_id"] = auditSink.HashValue(grant.DelegatorUserAccountId.ToString()),
            ["capability"] = capability,
            ["resource_type"] = resource.Type,
            ["resource_id"] = resource.Id,
            ["outcome"] = "success",
            ["reason"] = null
        };

        var usedAt = DateTimeOffset.UtcNow;
        var usageGrant = await dbContext.DelegatedAccessGrants
            .SingleAsync(candidate => candidate.Id == grant.Id, cancellationToken)
            .ConfigureAwait(false);
        usageGrant.LastUsedAt = usedAt;
        usageGrant.UseCount++;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        auditSink.Emit("delegated_access.used", usedPayload);

        // Step 9-11: All checks passed — return EffectiveAccessContext
        return new EffectiveAccessContext(
            actorId,
            grant.DelegatorUserAccountId,
            grant.TenantId,
            AccessContextKind.DelegatedAccess,
            null,       // SupportAccessSessionId
            grant.Id);  // DelegatedAccessGrantId
    }

    // Internal helpers --------------------------------------------------------

    private static string? ValidateResourcePolicy(
        string policyJson,
        string capability,
        DelegatedResource resource,
        DelegableCapabilityDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(resource.Type)
            || string.IsNullOrWhiteSpace(resource.Id)
            || !string.IsNullOrWhiteSpace(resource.ConstraintsJson))
        {
            return "invalid_resource";
        }

        try
        {
            using var document = JsonDocument.Parse(policyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(capability, out var policy)
                || policy.ValueKind != JsonValueKind.Object
                || !policy.TryGetProperty("allowedTypes", out var allowedTypes)
                || allowedTypes.ValueKind != JsonValueKind.Array
                || !policy.TryGetProperty("allowedIds", out var allowedIds)
                || allowedIds.ValueKind != JsonValueKind.Array)
            {
                return "resource_policy_invalid";
            }

            var typeAllowed = allowedTypes.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String
                && definition.AllowedResourceTypes.Contains(value.GetString()!)
                && string.Equals(value.GetString(), resource.Type, StringComparison.OrdinalIgnoreCase));
            if (!typeAllowed)
            {
                return "resource_type_not_allowed";
            }

            var idAllowed = allowedIds.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), resource.Id, StringComparison.Ordinal));
            return idAllowed ? null : "resource_id_not_allowed";
        }
        catch (JsonException)
        {
            return "resource_policy_invalid";
        }
    }

    /// <summary>
    /// Resolves the user account ID from a ClaimsPrincipal's trusted claims.
    /// Uses UserAccountId claim, then falls back to subject/NameIdentifier claims.
    /// </summary>
    /// <summary>
    /// Resolves the user account ID from a ClaimsPrincipal's trusted claims.
    /// Uses UserAccountId claim, then falls back to NameIdentifier/sub claims.
    /// Throws AuthorizationError if the principal is not authenticated or no valid claim is found.
    /// </summary>
    private static Guid ResolveUserAccountId(ClaimsPrincipal principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new AuthorizationError("Principal is not authenticated.");
        }

        // Try explicit user-account claim first
        var accountIdClaim = principal.FindFirstValue(UserClaimTypes.UserAccountId);
        if (accountIdClaim is not null)
        {
            if (Guid.TryParse(accountIdClaim, out var accountId))
            {
                return accountId;
            }
            throw new AuthorizationError("Cannot parse UserAccountId claim as GUID.");
        }

        // Fall back to NameIdentifier claim (the standard subject identifier)
        var subjectClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
        if (subjectClaim is not null)
        {
            if (Guid.TryParse(subjectClaim, out var subjectId))
            {
                return subjectId;
            }
            throw new AuthorizationError("Cannot parse NameIdentifier claim as GUID.");
        }

        throw new AuthorizationError("No recognized user-account claim found in principal.");
    }
}
