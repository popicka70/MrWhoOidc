using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Result of user provisioning/linking operation.
/// </summary>
public sealed class UserProvisioningResult
{
    public bool Success { get; init; }
    public Guid? UserId { get; init; }
    public string? Outcome { get; init; }
    public bool RequiresConfirmation { get; init; }
    public ConfirmModel? ConfirmationModel { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Handles user provisioning and linking for external OIDC identities.
/// </summary>
public interface IExternalOidcUserProvisioner
{
    Task<UserProvisioningResult> ProvisionOrLinkUserAsync(
        string provider,
        string issuer,
        string subject,
        string? email,
        string? name,
        string? returnUrl,
        string? clientId,
        string? correlationId,
        string? correlationHandle,
        IReadOnlyDictionary<string, string> mappedClaims,
        CancellationToken cancellationToken);
}

internal sealed class ExternalOidcUserProvisioner : IExternalOidcUserProvisioner
{
    private readonly AuthDbContext _db;
    private readonly ILogger<ExternalOidcUserProvisioner> _logger;
    private readonly IRegistrationService _registrationService;
    private readonly IEmailConfirmationWorkflow _emailWorkflow;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IUserAccountProvisioner _accountProvisioner;
    private readonly IClientStore _clientStore;
    private readonly IAuditSink _audit;

    public ExternalOidcUserProvisioner(
        AuthDbContext db,
        ILogger<ExternalOidcUserProvisioner> logger,
        IRegistrationService registrationService,
        IEmailConfirmationWorkflow emailWorkflow,
        ITenantAccessor tenantAccessor,
        IUserAccountProvisioner accountProvisioner,
        IClientStore clientStore,
        IAuditSink audit)
    {
        _db = db;
        _logger = logger;
        _registrationService = registrationService;
        _emailWorkflow = emailWorkflow;
        _tenantAccessor = tenantAccessor;
        _accountProvisioner = accountProvisioner;
        _clientStore = clientStore;
        _audit = audit;
    }

    public async Task<UserProvisioningResult> ProvisionOrLinkUserAsync(
        string provider,
        string issuer,
        string subject,
        string? email,
        string? name,
        string? returnUrl,
        string? clientId,
        string? correlationId,
        string? correlationHandle,
        IReadOnlyDictionary<string, string> mappedClaims,
        CancellationToken cancellationToken)
    {
        var ext = await _db.ExternalIdentities
            .FirstOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, cancellationToken);

        if (ext is not null)
        {
            ext.LastSeenAt = DateTimeOffset.UtcNow;
            ext.ClaimsJson = BuildClaimsJson(email, name);
            await _db.SaveChangesAsync(cancellationToken);

            return new UserProvisioningResult
            {
                Success = true,
                UserId = ext.UserId,
                Outcome = "linked"
            };
        }

        var userEmail = mappedClaims.TryGetValue("email", out var me) ? me : email;
        var userName = mappedClaims.TryGetValue("name", out var mn) ? mn : name;

        // Prefer the validated clientId from the external state (captured at flow start).
        // Fall back to returnUrl parsing only when needed, and always validate via the client store.
        var clientPublicId = !string.IsNullOrWhiteSpace(clientId)
            ? clientId
            : ExternalOidcUrlHelpers.TryGetClientIdFromReturnUrl(returnUrl);

        var clientEntity = string.IsNullOrWhiteSpace(clientPublicId)
            ? null
            : await _clientStore.FindByClientIdAsync(clientPublicId, cancellationToken);

        // Enforce tenant boundaries when a tenant context exists.
        if (clientEntity is not null && _tenantAccessor.CurrentTenant is { TenantId: var currentTenantId } && clientEntity.TenantId != currentTenantId)
        {
            clientEntity = null;
        }

        var allowAutoProvision = clientEntity?.AllowExternalAutoProvision ?? true;
        var allowEmailLinking = clientEntity?.AllowExternalEmailLinking ?? true;
        var requireEmailConfirm = clientEntity?.RequireEmailLinkConfirmation ?? true;

        if (allowEmailLinking && !string.IsNullOrWhiteSpace(userEmail))
        {
            var existingUser = await FindUserByEmailAsync(userEmail!, cancellationToken);
            if (existingUser is not null)
            {
                if (requireEmailConfirm)
                {
                    return new UserProvisioningResult
                    {
                        Success = true,
                        RequiresConfirmation = true,
                        ConfirmationModel = new ConfirmModel
                        {
                            Provider = provider,
                            Issuer = issuer,
                            Subject = subject,
                            TargetUserId = existingUser.Id,
                            ReturnUrl = returnUrl,
                            ClientId = clientId,
                            CorrelationId = correlationId,
                            Email = userEmail,
                            Name = userName
                        },
                        Outcome = "requires_confirm"
                    };
                }
                else
                {
                    var newExt = new ExternalIdentity
                    {
                        Issuer = issuer,
                        Subject = subject,
                        UserId = existingUser.Id,
                        ProviderName = provider,
                        ClaimsJson = BuildClaimsJson(userEmail, userName),
                        CreatedAt = DateTimeOffset.UtcNow,
                        LastSeenAt = DateTimeOffset.UtcNow
                    };
                    _db.ExternalIdentities.Add(newExt);
                    await _db.SaveChangesAsync(cancellationToken);
                    await _accountProvisioner.EnsureAsync(existingUser, existingUser.TenantId, clientEntity?.RealmId, isTenantAdmin: false, cancellationToken);

                    return new UserProvisioningResult
                    {
                        Success = true,
                        UserId = existingUser.Id,
                        Outcome = "linked_immediate"
                    };
                }
            }
        }

        if (allowAutoProvision)
        {
            // Check client's auto-approval setting
            var autoApprovalMode = clientEntity?.AutoApprovalMode ?? AutoApprovalMode.No;
            var shouldAutoApprove = autoApprovalMode == AutoApprovalMode.All || autoApprovalMode == AutoApprovalMode.OnlyExternalIdp;

            if (shouldAutoApprove)
            {
                // Create registration and auto-approve it
                try
                {
                    // Only associate a client when it opts in to auto-assign.
                    var registrationClientId = clientEntity?.AutoAssignNewUsersToClient == true ? clientEntity.Id : (Guid?)null;

                    var userId = await _registrationService.CreateAndMaybeApproveRegistrationAsync(
                        userEmail ?? $"{provider}:{subject}",
                        null, // firstName - can be parsed from name if needed
                        null, // lastName
                        registrationClientId,
                        null, // no password for external IdP users
                        isExternalIdp: true,
                        autoApprove: true,
                        tenantSlug: null,
                        tenantName: null,
                        tenantDescription: null,
                        cancellationToken);

                    if (userId.HasValue)
                    {
                        // Link external identity to the newly created and approved user
                        var newExt = new ExternalIdentity
                        {
                            Issuer = issuer,
                            Subject = subject,
                            UserId = userId.Value,
                            ProviderName = provider,
                            ClaimsJson = BuildClaimsJson(userEmail, userName),
                            CreatedAt = DateTimeOffset.UtcNow,
                            LastSeenAt = DateTimeOffset.UtcNow
                        };
                        _db.ExternalIdentities.Add(newExt);
                        await _db.SaveChangesAsync(cancellationToken);

                        var autoApprovedUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
                        if (autoApprovedUser is not null)
                        {
                            await _accountProvisioner.EnsureAsync(autoApprovedUser, autoApprovedUser.TenantId, clientEntity?.RealmId, isTenantAdmin: false, cancellationToken);
                        }

                        _logger.LogInformation("Auto-approved registration for external IdP user {Email} from provider {Provider}",
                            userEmail, provider);

                        return new UserProvisioningResult
                        {
                            Success = true,
                            UserId = userId.Value,
                            Outcome = "auto_approved"
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-approval failed for {Email}, falling back to standard provisioning", userEmail);
                    // Fall through to standard auto-provisioning if something goes wrong
                }
            }

            // Standard auto-provisioning (no registration record, direct user creation)
            var autoProvisionedUserId = await AutoProvisionUserAsync(provider, issuer, subject, userEmail, userName, clientEntity, cancellationToken);
            return new UserProvisioningResult
            {
                Success = true,
                UserId = autoProvisionedUserId,
                Outcome = "auto_provisioned"
            };
        }

        return new UserProvisioningResult
        {
            Success = false,
            ErrorCode = "policy_denied",
            ErrorMessage = "External sign-in is not allowed by client policy.",
            Outcome = "policy_denied"
        };
    }

    private async Task<Guid> AutoProvisionUserAsync(
        string provider,
        string issuer,
        string subject,
        string? email,
        string? name,
        Client? clientEntity,
        CancellationToken cancellationToken)
    {
        var baseUsername = !string.IsNullOrEmpty(email) ? email : $"{provider}:{subject}";
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        var usernameCandidate = normalizedEmail ?? baseUsername;

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == usernameCandidate || (normalizedEmail != null && u.NormalizedEmail == normalizedEmail),
            cancellationToken);

        var tenantId = clientEntity?.TenantId
            ?? _tenantAccessor.CurrentTenant?.TenantId
            ?? Guid.Empty;
        var userWasCreated = false;

        if (user is null)
        {
            string? emailForUser = email;
            if (!string.IsNullOrEmpty(email))
            {
                try
                {
                    emailForUser = EmailNormalizer.FormatForStorage(email, required: true, out var normalizedFromFormat);
                    normalizedEmail = normalizedFromFormat ?? normalizedEmail;
                    usernameCandidate = normalizedEmail ?? usernameCandidate;
                }
                catch (ValidationException)
                {
                    emailForUser = null;
                }
            }

            user = new User
            {
                TenantId = tenantId,
                Username = usernameCandidate,
                Email = emailForUser,
                Name = name ?? (emailForUser ?? baseUsername)
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken);
            await _accountProvisioner.EnsureAsync(user, tenantId, clientEntity?.RealmId, isTenantAdmin: false, cancellationToken);
            userWasCreated = true;

            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                try
                {
                    await _emailWorkflow.SendPrimaryAsync(user, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispatch confirmation email for externally provisioned user {UserId}", user.Id);
                }
            }
        }

        var ext = new ExternalIdentity
        {
            Issuer = issuer,
            Subject = subject,
            UserId = user.Id,
            ProviderName = provider,
            ClaimsJson = BuildClaimsJson(email, name),
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        _db.ExternalIdentities.Add(ext);
        await _db.SaveChangesAsync(cancellationToken);

        // Auto-assign only for newly created users, and only when the resolved client opts in.
        if (userWasCreated && clientEntity is not null && clientEntity.AutoAssignNewUsersToClient && user.TenantId == clientEntity.TenantId)
        {
            var exists = await _db.UserClientAssignments.AnyAsync(
                a => a.UserId == user.Id && a.ClientId == clientEntity.Id && a.RealmId == clientEntity.RealmId,
                cancellationToken);
            if (!exists)
            {
                _db.UserClientAssignments.Add(new UserClientAssignment
                {
                    UserId = user.Id,
                    ClientId = clientEntity.Id,
                    RealmId = clientEntity.RealmId,
                    IsActive = true
                });
                await _db.SaveChangesAsync(cancellationToken);

                _audit.Emit("external.client.auto_assign", new
                {
                    at = DateTimeOffset.UtcNow,
                    provider,
                    tenant_id = user.TenantId,
                    user_id = user.Id,
                    user_email_hash = _audit.HashValue(user.Email),
                    client_id = clientEntity.ClientId,
                    client_record_id = clientEntity.Id,
                    realm_id = clientEntity.RealmId,
                    source = "external.auto_provision"
                });
            }
        }

        if (!userWasCreated)
        {
            await _accountProvisioner.EnsureAsync(user, user.TenantId, clientEntity?.RealmId, isTenantAdmin: false, cancellationToken);
        }

        return user.Id;
    }

    private async Task<User?> FindUserByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is not null)
            return user;

        var alt = await _db.UserAlternativeEmails.AsNoTracking()
            .FirstOrDefaultAsync(a => a.NormalizedEmail == normalized && a.IsVerified, ct);
        if (alt is not null)
        {
            return await _db.Users.FirstOrDefaultAsync(u => u.Id == alt.UserId, ct);
        }

        return null;
    }

    private static string? BuildClaimsJson(string? email, string? name)
    {
        if (email is null && name is null)
            return null;
        return JsonSerializer.Serialize(new { email, name });
    }
}
