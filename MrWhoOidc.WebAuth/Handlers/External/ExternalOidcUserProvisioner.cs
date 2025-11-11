using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Services;

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

    public ExternalOidcUserProvisioner(
        AuthDbContext db,
        ILogger<ExternalOidcUserProvisioner> logger,
    IRegistrationService registrationService,
    IEmailConfirmationWorkflow emailWorkflow,
        ITenantAccessor tenantAccessor)
    {
        _db = db;
        _logger = logger;
    _registrationService = registrationService;
    _emailWorkflow = emailWorkflow;
        _tenantAccessor = tenantAccessor;
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

        var clientPublicId = ExternalOidcUrlHelpers.TryGetClientIdFromReturnUrl(returnUrl);
        var clientEntity = await (string.IsNullOrWhiteSpace(clientPublicId)
            ? Task.FromResult<Client?>(null)
            : _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientPublicId, cancellationToken));

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
                    var userId = await _registrationService.CreateAndMaybeApproveRegistrationAsync(
                        userEmail ?? $"{provider}:{subject}",
                        null, // firstName - can be parsed from name if needed
                        null, // lastName
                        clientEntity?.Id,
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
            var autoProvisionedUserId = await AutoProvisionUserAsync(provider, issuer, subject, userEmail, userName, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var baseUsername = !string.IsNullOrEmpty(email) ? email : $"{provider}:{subject}";
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        var usernameCandidate = normalizedEmail ?? baseUsername;

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == usernameCandidate || (normalizedEmail != null && u.NormalizedEmail == normalizedEmail),
            cancellationToken);

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
                TenantId = _tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty,
                Username = usernameCandidate,
                Email = emailForUser,
                Name = name ?? (emailForUser ?? baseUsername),
                PasswordHash = string.Empty,
                HashAlgorithm = "external"
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken);

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
