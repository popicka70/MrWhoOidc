using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.MultiTenancy;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth.Services.Users;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for handling user registration creation and approval workflows.
/// </summary>
public interface IRegistrationWorkflowService
{
    Task<RegistrationResult> CreateAndMaybeApproveRegistrationAsync(
        string email,
        string? firstName,
        string? lastName,
        Guid? clientId,
        string? passwordHash,
        bool isExternalIdp,
        bool autoApprove,
        string? tenantSlug = null,
        string? tenantName = null,
        string? tenantDescription = null,
        CancellationToken cancellationToken = default,
        Guid? targetTenantId = null,
        bool isPlatformRegistration = false,
        bool autoConfirmEmail = false);

    Task<Guid> ApproveRegistrationAsync(MrWhoOidc.Auth.Persistence.Registration registration, Guid? approvingUserId = null, CancellationToken cancellationToken = default);
}

internal sealed class RegistrationWorkflowService : IRegistrationWorkflowService
{
    private readonly AuthDbContext _db;
    private readonly ILogger<RegistrationWorkflowService> _logger;
    private readonly IEmailConfirmationWorkflow _emailWorkflow;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAuditSink _audit;
    private readonly MrWhoOidc.Auth.Services.Users.IRegistrationService _domainService;

    public RegistrationWorkflowService(
        AuthDbContext db,
        ILogger<RegistrationWorkflowService> logger,
        IEmailConfirmationWorkflow emailWorkflow,
        ITenantAccessor tenantAccessor,
        IAuditSink audit,
        MrWhoOidc.Auth.Services.Users.IRegistrationService domainService)
    {
        _db = db;
        _logger = logger;
        _emailWorkflow = emailWorkflow;
        _tenantAccessor = tenantAccessor;
        _audit = audit;
        _domainService = domainService;
    }

    public async Task<RegistrationResult> CreateAndMaybeApproveRegistrationAsync(
        string email,
        string? firstName,
        string? lastName,
        Guid? clientId,
        string? passwordHash,
        bool isExternalIdp,
        bool autoApprove,
        string? tenantSlug = null,
        string? tenantName = null,
        string? tenantDescription = null,
        CancellationToken cancellationToken = default,
        Guid? targetTenantId = null,
        bool isPlatformRegistration = false,
        bool autoConfirmEmail = false)
    {
        var input = new MrWhoOidc.Auth.Services.Users.RegistrationInput(
            email,
            firstName,
            lastName,
            clientId,
            passwordHash,
            autoApprove,
            isExternalIdp,
            !string.IsNullOrWhiteSpace(tenantSlug) ? new MrWhoOidc.Auth.Services.Users.TenantCreationInput(tenantSlug, tenantName ?? tenantSlug, tenantDescription) : null,
            targetTenantId ?? _tenantAccessor.CurrentTenant?.TenantId,
            isPlatformRegistration,
            autoConfirmEmail
        );

        var result = await _domainService.CreateRegistrationAsync(input, cancellationToken);

        if (result.Outcome == RegistrationOutcome.Approved && result.CreatedUserId.HasValue)
        {
            var user = await _db.Users.FindAsync(new object[] { result.CreatedUserId.Value }, cancellationToken);
            if (user != null)
            {
                await HandlePostApprovalSideEffectsAsync(user, result.RegistrationId, clientId, cancellationToken);
            }
        }
        else if (result.Outcome == RegistrationOutcome.PendingCreated && result.RegistrationId.HasValue)
        {
            _logger.LogInformation(
                "Registration {RegistrationId} for {EmailHash} is pending admin approval. PlatformRegistration={PlatformRegistration}",
                result.RegistrationId.Value,
                _audit.HashValue(email),
                isPlatformRegistration);
        }
        else if (result.Outcome == RegistrationOutcome.PendingExisting)
        {
            _logger.LogInformation(
                "Registration remains pending because a request already exists for {EmailHash}",
                _audit.HashValue(email));
        }

        return result;
    }

    public async Task<Guid> ApproveRegistrationAsync(
        MrWhoOidc.Auth.Persistence.Registration registration,
        Guid? approvingUserId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _domainService.ApproveRegistrationAsync(registration.Id, approvingUserId, cancellationToken);

        if (result.CreatedUserId.HasValue)
        {
            var user = await _db.Users.FindAsync(new object[] { result.CreatedUserId.Value }, cancellationToken);
            if (user != null)
            {
                await HandlePostApprovalSideEffectsAsync(user, registration.Id, registration.ClientId, cancellationToken);
            }
            return result.CreatedUserId.Value;
        }

        throw new InvalidOperationException("Registration approval did not result in a created user.");
    }

    private async Task HandlePostApprovalSideEffectsAsync(
        MrWhoOidc.Auth.Persistence.User user,
        Guid? registrationId,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        // 1. Send confirmation email (skip if already verified, e.g. domain-claim auto-enrollment)
        if (!string.IsNullOrWhiteSpace(user.Email) && !user.EmailVerified)
        {
            try
            {
                var result = await _emailWorkflow.SendPrimaryAsync(user, cancellationToken);
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "Confirmation email sent for approved registration {RegistrationId} and user {UserId}",
                        registrationId,
                        user.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "Confirmation email was not sent for approved registration {RegistrationId} and user {UserId}. Status={Status}",
                        registrationId,
                        user.Id,
                        result.Status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to dispatch confirmation email for approved registration {RegistrationId} and user {UserId}",
                    registrationId,
                    user.Id);
            }
        }
        else
        {
            _logger.LogWarning(
                "Confirmation email skipped for approved registration {RegistrationId} because user {UserId} has no email address",
                registrationId,
                user.Id);
        }

        // 2. Audit client auto-assignment if applicable
        if (clientId.HasValue)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
            if (client != null && client.TenantId == user.TenantId)
            {
                _audit.Emit("registration.client.auto_assign", new
                {
                    at = DateTimeOffset.UtcNow,
                    tenant_id = user.TenantId,
                    user_id = user.Id,
                    user_email_hash = _audit.HashValue(user.Email),
                    client_id = client.ClientId,
                    client_record_id = client.Id,
                    realm_id = client.RealmId,
                    source = "registration.approve"
                });
            }
        }
    }
}
