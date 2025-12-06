using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.MultiTenancy;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for handling user registration creation and approval workflows.
/// </summary>
public interface IRegistrationService
{
    /// <summary>
    /// Creates a new user registration and optionally auto-approves it based on client settings.
    /// </summary>
    /// <param name="email">User's email address</param>
    /// <param name="firstName">Optional first name</param>
    /// <param name="lastName">Optional last name</param>
    /// <param name="clientId">Associated client ID</param>
    /// <param name="passwordHash">Optional password hash (for local registrations)</param>
    /// <param name="isExternalIdp">Whether registration comes from external IdP</param>
    /// <param name="autoApprove">Whether to immediately approve based on client policy</param>
    /// <param name="tenantSlug">Optional tenant slug for new tenant creation</param>
    /// <param name="tenantName">Optional tenant name for new tenant creation</param>
    /// <param name="tenantDescription">Optional tenant description for new tenant creation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created user ID if approved, null if pending manual approval</returns>
    Task<Guid?> CreateAndMaybeApproveRegistrationAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending registration and creates the user account.
    /// </summary>
    Task<Guid> ApproveRegistrationAsync(Registration registration, Guid? approvingUserId = null, CancellationToken cancellationToken = default);
}

internal sealed class RegistrationService : IRegistrationService
{
    private readonly AuthDbContext _db;
    private readonly ILogger<RegistrationService> _logger;
    private readonly IEmailConfirmationWorkflow _emailWorkflow;
    private readonly IUserAccountProvisioner _accountProvisioner;
    private readonly ITenantAccessor _tenantAccessor;

    public RegistrationService(AuthDbContext db, ILogger<RegistrationService> logger, IEmailConfirmationWorkflow emailWorkflow, IUserAccountProvisioner accountProvisioner, ITenantAccessor tenantAccessor)
    {
        _db = db;
        _logger = logger;
        _emailWorkflow = emailWorkflow;
        _accountProvisioner = accountProvisioner;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<Guid?> CreateAndMaybeApproveRegistrationAsync(
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
        CancellationToken cancellationToken = default)
    {
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ValidationException("Invalid email address.");
        }

        // Check if user already exists
        var userExists = await _db.Users.AsNoTracking()
            .AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (userExists)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        // Check for existing pending registration
        var pendingReg = await _db.Set<Registration>()
            .FirstOrDefaultAsync(r => r.NormalizedEmail == normalized && r.State == "pending", cancellationToken);
        if (pendingReg is not null)
        {
            _logger.LogInformation("Pending registration already exists for {Email}", email);
            return null; // Existing pending registration
        }

        // Validate tenant creation parameters if provided
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            if (string.IsNullOrWhiteSpace(tenantName))
            {
                throw new ValidationException("Tenant name is required when creating a new tenant.");
            }

            // Validate tenant slug format (URL-safe: lowercase, alphanumeric, hyphens)
            if (!System.Text.RegularExpressions.Regex.IsMatch(tenantSlug, @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$|^[a-z0-9]$"))
            {
                throw new ValidationException("Tenant slug must be URL-safe (lowercase letters, numbers, and hyphens only, cannot start or end with hyphen).");
            }

            // Check tenant slug uniqueness
            var existingTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken);
            if (existingTenant != null)
            {
                throw new ValidationException($"A tenant with slug '{tenantSlug}' already exists.");
            }

            // Create the tenant
            var tenant = new Tenant
            {
                Slug = tenantSlug,
                Name = tenantName,
                Description = tenantDescription,
                IssuerUri = $"https://localhost:8443/t/{tenantSlug}", // TODO: Make configurable
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync(cancellationToken);

            // Create default realm
            var defaultRealm = new Realm
            {
                TenantId = tenant.Id,
                Name = "default",
                DisplayName = "Default Realm",
                AllowUnconfirmedLogin = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Realms.Add(defaultRealm);

            // Create tenant-admin role
            var tenantAdminRole = new Role
            {
                TenantId = tenant.Id,
                RealmId = defaultRealm.Id,
                Name = "tenant-admin",
                IsActive = true
            };
            _db.Roles.Add(tenantAdminRole);

            await _db.SaveChangesAsync(cancellationToken);

            tenantId = tenant.Id;
            _logger.LogInformation("Created new tenant {TenantSlug} (ID: {TenantId}) for registration", tenantSlug, tenant.Id);
        }

        // Determine the tenant ID for this registration
        Guid registrationTenantId;
        if (tenantId.HasValue)
        {
            // New tenant was created
            registrationTenantId = tenantId.Value;
        }
        else
        {
            // Use current tenant from URL path context
            var currentTenant = _tenantAccessor.CurrentTenant;
            if (currentTenant == null)
            {
                throw new ValidationException("Cannot determine tenant for registration. Please access the registration page via a tenant-specific URL.");
            }
            registrationTenantId = currentTenant.TenantId;
        }

        var registration = new Registration
        {
            Email = email,
            NormalizedEmail = normalized,
            FirstName = firstName,
            LastName = lastName,
            ClientId = clientId,
            PasswordHash = passwordHash,
            State = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            IsTenantAdmin = tenantId.HasValue,
            TenantSlug = tenantSlug,
            TenantName = tenantName,
            TenantDescription = tenantDescription,
            TenantId = registrationTenantId
        };

        _db.Set<Registration>().Add(registration);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registration created for {Email}, AutoApprove={AutoApprove}, IsExternalIdp={IsExternalIdp}",
            email, autoApprove, isExternalIdp);

        if (autoApprove)
        {
            return await ApproveRegistrationAsync(registration, null, cancellationToken);
        }

        return null; // Pending manual approval
    }

    public async Task<Guid> ApproveRegistrationAsync(
        Registration registration,
        Guid? approvingUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(registration.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Registration {registration.Id} is not in pending state.");
        }

        var normalized = registration.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(registration.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ValidationException("Registration rejected because the email is invalid.");
        }

        string emailForUser;
        try
        {
            emailForUser = EmailNormalizer.FormatForStorage(registration.Email, required: true, out var normalizedFromFormat)
                ?? throw new ValidationException("Email is required.");
            normalized = normalizedFromFormat ?? normalized;
        }
        catch (ValidationException ex)
        {
            registration.State = "rejected";
            registration.RejectedAt = DateTimeOffset.UtcNow;
            registration.RejectedByUserId = approvingUserId;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"Registration rejected: {ex.Message}", ex);
        }

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (existing is not null)
        {
            registration.State = "rejected";
            registration.RejectedAt = DateTimeOffset.UtcNow;
            registration.RejectedByUserId = approvingUserId;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Registration rejected because a user with this email already exists.");
        }

        // Determine tenant for user
        Guid userTenantId;
        if (registration.IsTenantAdmin && registration.TenantId != Guid.Empty)
        {
            // User is tenant admin of newly created tenant
            userTenantId = registration.TenantId;
        }
        else if (registration.TenantId != Guid.Empty)
        {
            // Regular registration with tenant ID already set
            userTenantId = registration.TenantId;
        }
        else
        {
            // Fallback: use current tenant from context (shouldn't happen with fixed CreateAndMaybeApproveRegistrationAsync)
            var currentTenant = _tenantAccessor.CurrentTenant;
            if (currentTenant == null)
            {
                throw new InvalidOperationException("Cannot determine tenant for user creation. Registration has no tenant context.");
            }
            userTenantId = currentTenant.TenantId;
        }

        // Create user
        var user = new User
        {
            TenantId = userTenantId,
            Username = normalized,
            Email = emailForUser,
            EmailVerified = false,
            Name = string.Join(' ', new[] { registration.FirstName, registration.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)))
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
                _logger.LogWarning(ex, "Failed to dispatch confirmation email for user {UserId}", user.Id);
            }
        }

        // Assign tenant-admin role if this is a tenant admin registration
        Guid? defaultRealmId = null;
        Role? tenantAdminRole = null;
        if (registration.IsTenantAdmin)
        {
            tenantAdminRole = await _db.Roles.FirstOrDefaultAsync(
                r => r.TenantId == user.TenantId && r.Name == "tenant-admin",
                cancellationToken);
            defaultRealmId = tenantAdminRole?.RealmId;
        }

        await _accountProvisioner.EnsureAsync(user, userTenantId, defaultRealmId, registration.IsTenantAdmin, cancellationToken);

        if (registration.IsTenantAdmin && tenantAdminRole != null)
        {
            _db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
            {
                UserId = user.Id,
                RoleId = tenantAdminRole.Id,
                RealmId = tenantAdminRole.RealmId,
                IsActive = true
            });
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Assigned tenant-admin role to user {UserId} for tenant {TenantId}", user.Id, user.TenantId);
        }

        // Optional assign to client
        if (registration.ClientId is Guid clientId)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
            if (client is not null)
            {
                var exists = await _db.UserClientAssignments.AnyAsync(
                    a => a.UserId == user.Id && a.ClientId == client.Id && a.RealmId == client.RealmId,
                    cancellationToken);
                if (!exists)
                {
                    _db.UserClientAssignments.Add(new UserClientAssignment
                    {
                        UserId = user.Id,
                        ClientId = client.Id,
                        RealmId = client.RealmId,
                        IsActive = true
                    });
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        registration.State = "approved";
        registration.ApprovedAt = DateTimeOffset.UtcNow;
        registration.ApprovedByUserId = approvingUserId;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registration {RegistrationId} approved, User {UserId} created", registration.Id, user.Id);

        return user.Id;
    }
}
