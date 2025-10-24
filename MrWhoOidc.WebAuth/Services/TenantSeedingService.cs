using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for on-demand tenant seeding with sample data
/// </summary>
public interface ITenantSeedingService
{
    Task<TenantSeedResult> SeedSampleTenantAsync(string tenantSlug, string tenantName, string? adminEmail = null, string? adminPassword = null, CancellationToken ct = default);
}

public class TenantSeedingService : ITenantSeedingService
{
    private readonly AuthDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<TenantSeedingService> _logger;

    public TenantSeedingService(AuthDbContext db, IPasswordHasher passwordHasher, ILogger<TenantSeedingService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task<TenantSeedResult> SeedSampleTenantAsync(
        string tenantSlug,
        string tenantName,
        string? adminEmail = null,
        string? adminPassword = null,
        CancellationToken ct = default)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(tenantSlug))
            return TenantSeedResult.Failure("Tenant slug is required");

        if (string.IsNullOrWhiteSpace(tenantName))
            return TenantSeedResult.Failure("Tenant name is required");

        // Check if tenant already exists
        var existingTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == tenantSlug, ct);
        if (existingTenant != null)
            return TenantSeedResult.Failure($"Tenant with slug '{tenantSlug}' already exists");

        // Default credentials
        adminEmail ??= $"admin@{tenantSlug}.local";
        adminPassword ??= "Admin123!";

        try
        {
            // Create tenant
            var tenant = new Tenant
            {
                Slug = tenantSlug,
                Name = tenantName,
                IssuerUri = $"https://localhost:8443/t/{tenantSlug}",
                Status = TenantStatus.Active,
                MaxUsers = 100000,
                MaxClients = 1000,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created tenant {TenantSlug} (ID: {TenantId})", tenantSlug, tenant.Id);

            // Create default realm
            var defaultRealm = new Realm
            {
                Name = "default",
                DisplayName = "Default Realm",
                TenantId = tenant.Id,
                AllowUnconfirmedLogin = true
            };
            _db.Realms.Add(defaultRealm);

            // Create admin realm
            var adminRealm = new Realm
            {
                Name = "admin",
                DisplayName = "Admin Realm",
                TenantId = tenant.Id,
                AllowUnconfirmedLogin = true
            };
            _db.Realms.Add(adminRealm);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created realms for tenant {TenantSlug}", tenantSlug);

            // Create tenant-admin role in default realm (used for tenant admin authorization)
            var tenantAdminRole = new Role
            {
                Name = "tenant-admin",
                RealmId = defaultRealm.Id,
                TenantId = tenant.Id,
                IsActive = true
            };
            _db.Roles.Add(tenantAdminRole);

            // Create admin role in admin realm (legacy, kept for compatibility)
            var adminRole = new Role
            {
                Name = "admin",
                RealmId = adminRealm.Id,
                TenantId = tenant.Id,
                IsActive = true
            };
            _db.Roles.Add(adminRole);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created tenant-admin and admin roles for tenant {TenantSlug}", tenantSlug);

            // Create admin user
            var adminUser = new User
            {
                Username = adminEmail.Split('@')[0],
                Email = adminEmail,
                NormalizedEmail = adminEmail.ToUpperInvariant(),
                EmailVerified = true,
                EmailVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = _passwordHasher.Hash(adminPassword),
                HashAlgorithm = "argon2id",
                TenantId = tenant.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Users.Add(adminUser);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created admin user {AdminEmail} for tenant {TenantSlug}", adminEmail, tenantSlug);

            // Create admin client (for the admin UI)
            var adminClient = new Client
            {
                ClientId = $"{tenantSlug}-admin",
                ClientName = $"{tenantName} Admin Portal",
                TenantId = tenant.Id,
                RealmId = adminRealm.Id,
                RequirePkce = true,
                RequireConsent = false,
                AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    $"https://localhost:8443/t/{tenantSlug}/signin-oidc",
                    $"http://localhost:8443/t/{tenantSlug}/signin-oidc"
                }),
                AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    $"https://localhost:8443/t/{tenantSlug}/signout-callback-oidc",
                    $"https://localhost:8443/t/{tenantSlug}/",
                    $"http://localhost:8443/t/{tenantSlug}/signout-callback-oidc",
                    $"http://localhost:8443/t/{tenantSlug}/"
                })
            };

            // Create sample web client
            var webClient = new Client
            {
                ClientId = $"{tenantSlug}-web",
                ClientName = $"{tenantName} Web Application",
                TenantId = tenant.Id,
                RealmId = defaultRealm.Id,
                RequirePkce = true,
                RequireConsent = false,
                AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    "https://localhost:5001/signin-oidc",
                    "http://localhost:5001/signin-oidc"
                }),
                AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    "https://localhost:5001/signout-callback-oidc",
                    "https://localhost:5001/",
                    "http://localhost:5001/signout-callback-oidc",
                    "http://localhost:5001/"
                })
            };

            _db.Clients.Add(adminClient);
            _db.Clients.Add(webClient);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created admin client {AdminClientId} and web client {WebClientId} for tenant {TenantSlug}",
                adminClient.ClientId, webClient.ClientId, tenantSlug);

            // Ensure clients have IDs after save
            if (adminClient.Id == Guid.Empty || webClient.Id == Guid.Empty)
            {
                _logger.LogError("Client IDs not generated for tenant {TenantSlug}. Admin: {AdminId}, Web: {WebId}",
                    tenantSlug, adminClient.Id, webClient.Id);
                return TenantSeedResult.Failure("Failed to create clients - IDs not generated");
            }

            _logger.LogInformation("Client IDs confirmed: Admin={AdminId}, Web={WebId}", adminClient.Id, webClient.Id);

            // Assign tenant-admin role to admin user (in default realm)
            var tenantAdminRoleAssignment = new UserRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = tenantAdminRole.Id,
                ClientId = adminClient.Id,
                RealmId = defaultRealm.Id,
                IsActive = true
            };
            _db.UserRoleAssignments.Add(tenantAdminRoleAssignment);

            // Also assign legacy admin role (in admin realm)
            var adminRoleAssignment = new UserRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                ClientId = adminClient.Id,
                RealmId = adminRealm.Id,
                IsActive = true
            };
            _db.UserRoleAssignments.Add(adminRoleAssignment);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Assigned tenant-admin and admin roles to user {AdminEmail} for tenant {TenantSlug}", adminEmail, tenantSlug);

            // Create standard scopes (client-scope associations)
            var scopes = new[] { "openid", "profile", "email", "roles", "offline_access" };
            foreach (var scopeName in scopes)
            {
                // Check if scope already exists globally
                var scope = await _db.Scopes.FirstOrDefaultAsync(s => s.Name == scopeName, ct);
                if (scope == null)
                {
                    scope = new Scope
                    {
                        Name = scopeName,
                        Description = $"{scopeName} scope",
                        IsExposed = true
                    };
                    _db.Scopes.Add(scope);
                }

                // Associate scope with both clients
                _db.ClientScopes.Add(new ClientScope { ClientId = adminClient.Id, ScopeName = scopeName });
                _db.ClientScopes.Add(new ClientScope { ClientId = webClient.Id, ScopeName = scopeName });
            }
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created {ScopeCount} standard scopes for tenant {TenantSlug}", scopes.Length, tenantSlug);

            return TenantSeedResult.Success(
                tenant.Id,
                tenantSlug,
                tenantName,
                adminEmail,
                adminPassword,
                adminClient.ClientId,
                webClient.ClientId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed tenant {TenantSlug}", tenantSlug);
            return TenantSeedResult.Failure($"Failed to seed tenant: {ex.Message}");
        }
    }
}

public class TenantSeedResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? TenantId { get; init; }
    public string? TenantSlug { get; init; }
    public string? TenantName { get; init; }
    public string? AdminEmail { get; init; }
    public string? AdminPassword { get; init; }
    public string? AdminClientId { get; init; }
    public string? WebClientId { get; init; }

    public static TenantSeedResult Success(
        Guid tenantId,
        string tenantSlug,
        string tenantName,
        string adminEmail,
        string adminPassword,
        string adminClientId,
        string webClientId)
    {
        return new TenantSeedResult
        {
            IsSuccess = true,
            TenantId = tenantId,
            TenantSlug = tenantSlug,
            TenantName = tenantName,
            AdminEmail = adminEmail,
            AdminPassword = adminPassword,
            AdminClientId = adminClientId,
            WebClientId = webClientId
        };
    }

    public static TenantSeedResult Failure(string errorMessage)
    {
        return new TenantSeedResult
        {
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
    }
}
