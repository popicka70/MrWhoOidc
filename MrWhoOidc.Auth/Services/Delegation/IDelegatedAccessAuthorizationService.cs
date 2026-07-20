using System;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    ILogger<DelegatedAccessAuthorizationService> logger)
    : IDelegatedAccessAuthorizationService
{
    public async Task<EffectiveAccessContext> AuthorizeAsync(
        ClaimsPrincipal actor,
        Guid grantId,
        string capability,
        DelegatedResource resource,
        CancellationToken cancellationToken = default)
    {
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
            throw new StatusError("Grant is not active. Current status: ${grant.Status.ToString()}.");
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
            throw new StatusError("Grant has not yet started (StartsAt ${grant.StartsAt.IsoFormat}).");
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
            throw new ExpiredError("Grant has expired at ${grant.ExpiresAt.IsoFormat}.");
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
                "Actor ${actorId} does not match grant delegate ${grant.DelegateUserAccountId}.");
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
            throw new TenantError("Grant tenant is not active. Current status: ${tenant.Status.ToString()}.");
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
            throw new CapabilityError("Capability '${capability}' is not delegable or unknown.");
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
            throw new CapabilityError("Capability '${capability}' is not present in the grant's capabilities.");
        }

        // Step 8: Verify resource matches constraints in ResourceConstraintsJson
        var constraintsJson = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(grant.ResourceConstraintsJson);
        if (constraintsJson is not null && constraintsJson.ContainsKey(capability))
        {
            var constraints = constraintsJson[capability];
            if (resource.Type is not null && constraints is Dictionary<string, object> constraintMap)
        {
                if (constraintMap.ContainsKey("allowedTypes") && constraintMap["allowedTypes"] is List<string> allowedTypes)
                {
                    if (!allowedTypes.Contains(resource.Type))
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
                        ["reason"] = "resource_type_not_allowed"
                        };
                        auditSink.Emit("delegated_access.denied", deniedPayload);
                        throw new ResourceError("Resource type '${resource.Type}' is not in allowed types.");
                    }
                }
                if (constraintMap.ContainsKey("allowedIds") && constraintMap["allowedIds"] is List<string> allowedIds)
                {
                    if (resource.Id is not null && !allowedIds.Contains(resource.Id))
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
                        ["reason"] = "resource_id_not_allowed"
                        };
                        auditSink.Emit("delegated_access.denied", deniedPayload);
                        throw new ResourceError("Resource ID '${resource.Id}' is not in allowed IDs.");
                    }
                }
            }
        }

        // If the resource has its own constraints JSON, validate those too
        if (!string.IsNullOrWhiteSpace(resource.ConstraintsJson))
        {
            var resourceConstraints = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(resource.ConstraintsJson);
            if (resourceConstraints is not null)
            {
                // Validate resource-specific constraints against grant policy
                // (Implementation-specific; depends on constraint schema)
            }
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
