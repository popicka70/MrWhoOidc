using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

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
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantSeedingService> _logger;
    private readonly IUserAccountProvisioner _accountProvisioner;
    private readonly OidcOptions _oidcOptions;
    private readonly IIssuerBuilder _issuerBuilder;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantSeedingService(
        AuthDbContext db,
        IPasswordHasher passwordHasher,
        ITenantService tenantService,
        ILogger<TenantSeedingService> logger,
        IUserAccountProvisioner accountProvisioner,
        IOptions<OidcOptions> oidcOptions,
        IIssuerBuilder issuerBuilder,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tenantService = tenantService;
        _logger = logger;
        _accountProvisioner = accountProvisioner;
        _oidcOptions = oidcOptions.Value;
        _issuerBuilder = issuerBuilder;
        _httpContextAccessor = httpContextAccessor;
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
            if (!await _tenantService.CanProvisionTenantAsync(1, ct))
            {
                return TenantSeedResult.Failure("Tenant limit reached for the current license tier.");
            }

            // Create tenant
            var baseUrl = ResolveBaseUrl();

            var tenant = new Tenant
            {
                Slug = tenantSlug,
                Name = tenantName,
                IssuerUri = _issuerBuilder.BuildIssuer(baseUrl, tenantSlug).TrimEnd('/'),
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

            // Default: dynamically registered clients go to the tenant default realm.
            tenant.SettingsJson = System.Text.Json.JsonSerializer.Serialize(
                new TenantSettings
                {
                    Auth = new AuthTenantSettings
                    {
                        DynamicClientRegistrationRealmId = defaultRealm.Id
                    }
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
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
                TenantId = tenant.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Users.Add(adminUser);
            await _db.SaveChangesAsync(ct);
            await _accountProvisioner.EnsureAsync(adminUser, tenant.Id, adminRealm.Id, isTenantAdmin: true, ct);

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
                    $"{baseUrl}/t/{tenantSlug}/signin-oidc"
                }),
                AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    $"{baseUrl}/t/{tenantSlug}/signout-callback-oidc",
                    $"{baseUrl}/t/{tenantSlug}/"
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
                    $"{_oidcOptions.SampleWebClientBaseUrl.TrimEnd('/')}/signin-oidc"
                }),
                AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    $"{_oidcOptions.SampleWebClientBaseUrl.TrimEnd('/')}/signout-callback-oidc",
                    $"{_oidcOptions.SampleWebClientBaseUrl.TrimEnd('/')}/"
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

            // Assign tenant-admin role to admin user (realm-scoped in default realm)
            var tenantAdminRoleAssignment = new UserRealmRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = tenantAdminRole.Id,
                RealmId = defaultRealm.Id,
                IsActive = true
            };
            _db.UserRealmRoleAssignments.Add(tenantAdminRoleAssignment);

            // Also assign admin role (realm-scoped in admin realm)
            var adminRoleAssignment = new UserRealmRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                RealmId = adminRealm.Id,
                IsActive = true
            };
            _db.UserRealmRoleAssignments.Add(adminRoleAssignment);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Assigned tenant-admin and admin roles to user {AdminEmail} for tenant {TenantSlug}", adminEmail, tenantSlug);

            // Create standard scopes (client-scope associations)
            var scopes = new[] { "openid", "profile", "email", "roles", "offline_access", "mrwhopdf" };
            var existingScopes = await _db.Scopes.AsNoTracking()
                .Where(s => scopes.Contains(s.Name))
                .Select(s => s.Name)
                .ToListAsync(ct);

            var existingScopeNames = existingScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var scopeName in scopes)
            {
                // Check if scope already exists globally
                if (!existingScopeNames.Contains(scopeName))
                {
                    var scope = new Scope
                    {
                        Name = scopeName,
                        Description = $"{scopeName} scope",
                        IsExposed = true,
                        IsGlobal = string.Equals(scopeName, "mrwhopdf", StringComparison.OrdinalIgnoreCase)
                    };
                    _db.Scopes.Add(scope);
                    existingScopeNames.Add(scopeName); // Add to avoid duplicates if creating multiple tenants concurrently (though DbContext is scoped)
                }

                // Associate scope with both clients
                _db.ClientScopes.Add(new ClientScope { ClientId = adminClient.Id, ScopeName = scopeName });
                _db.ClientScopes.Add(new ClientScope { ClientId = webClient.Id, ScopeName = scopeName });
            }
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Created {ScopeCount} standard scopes for tenant {TenantSlug}", scopes.Length, tenantSlug);

            var issuer = _issuerBuilder.BuildIssuer(baseUrl, tenantSlug);

            return TenantSeedResult.Success(
                tenant.Id,
                tenantSlug,
                tenantName,
                adminEmail,
                adminPassword,
                adminClient.ClientId,
                webClient.ClientId,
                loginUrl: $"{issuer}/Login",
                adminUrl: $"{issuer}/Admin/Users"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed tenant {TenantSlug}", tenantSlug);
            return TenantSeedResult.Failure($"Failed to seed tenant: {ex.Message}");
        }
    }

    private string ResolveBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        string? requestBaseUrl = null;
        if (request != null && request.Host.HasValue)
        {
            requestBaseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
        }

        return (!string.IsNullOrWhiteSpace(_oidcOptions.PublicBaseUrl) ? _oidcOptions.PublicBaseUrl.TrimEnd('/') : null)
            ?? (!string.IsNullOrWhiteSpace(_oidcOptions.Issuer) ? _oidcOptions.Issuer.TrimEnd('/') : null)
            ?? requestBaseUrl?.TrimEnd('/')
            ?? "https://localhost:8443";
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
    public string? LoginUrl { get; init; }
    public string? AdminUrl { get; init; }

    public static TenantSeedResult Success(
        Guid tenantId,
        string tenantSlug,
        string tenantName,
        string adminEmail,
        string adminPassword,
        string adminClientId,
        string webClientId,
        string? loginUrl = null,
        string? adminUrl = null)
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
            WebClientId = webClientId,
            LoginUrl = loginUrl,
            AdminUrl = adminUrl
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
