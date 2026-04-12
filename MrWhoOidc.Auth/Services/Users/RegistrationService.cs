using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;

namespace MrWhoOidc.Auth.Services.Users;

/// <summary>
/// Implementation of IRegistrationService that handles domain-level registration logic.
/// </summary>
public class RegistrationService : IRegistrationService
{
    private readonly AuthDbContext _db;
    private readonly ILogger<RegistrationService> _logger;
    private readonly IIssuerBuilder _issuerBuilder;
    private readonly OidcOptions _oidcOptions;
    private readonly IUserAccountProvisioner _accountProvisioner;

    public RegistrationService(
        AuthDbContext db,
        ILogger<RegistrationService> logger,
        IIssuerBuilder issuerBuilder,
        IOptions<OidcOptions> oidcOptions,
        IUserAccountProvisioner accountProvisioner)
    {
        _db = db;
        _logger = logger;
        _issuerBuilder = issuerBuilder;
        _oidcOptions = oidcOptions.Value;
        _accountProvisioner = accountProvisioner;
    }

    /// <summary>
    /// Creates a new registration request.
    /// </summary>
    public async Task<RegistrationResult> CreateRegistrationAsync(RegistrationInput input, CancellationToken cancellationToken = default)
    {
        var normalized = EmailNormalizer.NormalizeForLookup(input.Email);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Invalid email address.", nameof(input.Email));
        }

        // Check if user already exists
        var existingUserId = await _db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingUserId.HasValue)
        {
            _logger.LogInformation("Registration skipped for existing user {EmailHash}", HashForLog(input.Email));
            return new RegistrationResult(
                RegistrationId: null,
                State: "existing_user",
                Outcome: RegistrationOutcome.ExistingUser,
                ExistingUserId: existingUserId.Value);
        }

        // Check for existing pending registration
        var pendingReg = await _db.Set<Registration>()
            .FirstOrDefaultAsync(r => r.NormalizedEmail == normalized && r.State == "pending", cancellationToken);
        if (pendingReg is not null)
        {
            _logger.LogInformation("Pending registration already exists for {EmailHash}", HashForLog(input.Email));
            return new RegistrationResult(
                RegistrationId: pendingReg.Id,
                State: pendingReg.State,
                Outcome: RegistrationOutcome.PendingExisting);
        }

        Guid? tenantId = null;
        if (input.TenantCreation != null)
        {
            tenantId = await CreateTenantInternalAsync(input.TenantCreation, cancellationToken);
        }

        Guid registrationTenantId = tenantId ?? input.TargetTenantId ?? Guid.Empty;
        if (registrationTenantId == Guid.Empty)
        {
            throw new InvalidOperationException("Target tenant ID is required if not creating a new tenant.");
        }

        var registration = new Registration
        {
            Email = input.Email,
            NormalizedEmail = normalized,
            FirstName = input.FirstName,
            LastName = input.LastName,
            ClientId = input.ClientId,
            PasswordHash = input.PasswordHash,
            State = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            IsTenantAdmin = tenantId.HasValue,
            TenantSlug = input.TenantCreation?.Slug,
            TenantName = input.TenantCreation?.Name,
            TenantDescription = input.TenantCreation?.Description,
            TenantId = registrationTenantId
        };

        _db.Set<Registration>().Add(registration);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registration created for {EmailHash}, AutoApprove={AutoApprove}, IsExternalIdp={IsExternalIdp}",
            HashForLog(input.Email), input.AutoApprove, input.IsExternalIdp);

        if (input.AutoApprove)
        {
            return await ApproveRegistrationAsync(registration.Id, null, cancellationToken);
        }

        return new RegistrationResult(
            RegistrationId: registration.Id,
            State: registration.State,
            Outcome: RegistrationOutcome.PendingCreated,
            CreatedTenantId: tenantId);
    }

    /// <summary>
    /// Approves an existing registration, creating the user and tenant if necessary.
    /// </summary>
    public async Task<RegistrationResult> ApproveRegistrationAsync(Guid registrationId, Guid? approvingUserId = null, CancellationToken cancellationToken = default)
    {
        var registration = await _db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == registrationId, cancellationToken);
        if (registration == null)
        {
            throw new InvalidOperationException($"Registration {registrationId} not found.");
        }

        if (!string.Equals(registration.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Registration {registration.Id} is not in pending state.");
        }

        var normalized = registration.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(registration.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            throw new InvalidOperationException("Registration rejected because the email is invalid.");
        }

        string emailForUser;
        try
        {
            emailForUser = EmailNormalizer.FormatForStorage(registration.Email, required: true, out var normalizedFromFormat)
                ?? throw new InvalidOperationException("Email is required.");
            normalized = normalizedFromFormat ?? normalized;
        }
        catch (Exception ex)
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

        Guid userTenantId = registration.TenantId;

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
            if (client is not null && client.TenantId == user.TenantId)
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

        return new RegistrationResult(
            RegistrationId: registration.Id,
            State: registration.State,
            Outcome: RegistrationOutcome.Approved,
            CreatedUserId: user.Id,
            CreatedTenantId: userTenantId);
    }

    private async Task<Guid> CreateTenantInternalAsync(TenantCreationInput input, CancellationToken cancellationToken)
    {
        // Validate tenant slug format (URL-safe: lowercase, alphanumeric, hyphens)
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Slug, @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$|^[a-z0-9]$"))
        {
            throw new ArgumentException("Tenant slug must be URL-safe (lowercase letters, numbers, and hyphens only, cannot start or end with hyphen).", nameof(input.Slug));
        }

        // Check tenant slug uniqueness
        var existingTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == input.Slug, cancellationToken);
        if (existingTenant != null)
        {
            throw new InvalidOperationException($"A tenant with slug '{input.Slug}' already exists.");
        }

        // Create the tenant
        var baseUrl =
            (!string.IsNullOrWhiteSpace(_oidcOptions.PublicBaseUrl) ? _oidcOptions.PublicBaseUrl.TrimEnd('/') : null)
            ?? (!string.IsNullOrWhiteSpace(_oidcOptions.Issuer) ? _oidcOptions.Issuer.TrimEnd('/') : null)
            ?? "https://localhost:8443"; // dev fallback

        var tenant = new Tenant
        {
            Slug = input.Slug,
            Name = input.Name,
            Description = input.Description,
            IssuerUri = _issuerBuilder.BuildIssuer(baseUrl, input.Slug).TrimEnd('/'),
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

        _logger.LogInformation("Created new tenant {TenantSlug} (ID: {TenantId}) for registration", input.Slug, tenant.Id);
        return tenant.Id;
    }

    private static string HashForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        var hash = value.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return $"[hash:{hash:X8}]";
    }
}
