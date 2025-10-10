using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Default implementation of tenant settings service with cascading logic.
/// </summary>
public class TenantSettingsService : ITenantSettingsService
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IConfiguration _configuration;
    private readonly HybridCache _cache;
    private readonly TenantSettings _platformDefaults;

    public TenantSettingsService(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IConfiguration configuration,
        HybridCache cache)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _configuration = configuration;
        _cache = cache;

        // Load platform defaults from appsettings.json once
        _platformDefaults = LoadPlatformDefaults();
    }

    public async Task<TenantSettings?> GetTenantSettingsAsync(Guid tenantId)
    {
        var cacheKey = $"tenant:settings:{tenantId}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string>
        {
            "tenant-settings",
            $"tenant:{tenantId}"
        };

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                var tenant = await _db.Tenants
                    .Where(t => t.Id == tenantId)
                    .Select(t => new { t.SettingsJson })
                    .FirstOrDefaultAsync(cancel);

                if (tenant == null)
                {
                    return null;
                }

                return MergeSettings(_platformDefaults, tenant.SettingsJson);
            },
            options,
            tags,
            CancellationToken.None
        ).ConfigureAwait(false);
    }

    public async Task<TenantSettings> GetCurrentTenantSettingsAsync()
    {
        var currentTenant = _tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            // No tenant context - return platform defaults
            return _platformDefaults;
        }

        var tenantSettings = await GetTenantSettingsAsync(currentTenant.TenantId);
        return tenantSettings ?? _platformDefaults;
    }

    public async Task<bool> UpdateTenantSettingsAsync(Guid tenantId, TenantSettings settings)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return false;
        }

        // Serialize settings to JSON
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        tenant.SettingsJson = json;
        await _db.SaveChangesAsync();

        // Invalidate tenant settings cache
        var cacheKey = $"tenant:settings:{tenantId}";
        await _cache.RemoveAsync(cacheKey).ConfigureAwait(false);

        return true;
    }

    public TenantSettings GetPlatformDefaults()
    {
        return _platformDefaults;
    }

    /// <summary>
    /// Loads platform default settings from appsettings.json.
    /// </summary>
    private TenantSettings LoadPlatformDefaults()
    {
        var settings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                Issuer = _configuration["Oidc:Issuer"],
                RequirePkce = _configuration.GetValue<bool?>("Oidc:RequirePkce"),
                CorsOrigins = _configuration.GetSection("Oidc:CorsOrigins").Get<List<string>>()
            },
            Auth = new AuthTenantSettings
            {
                AllowRefreshTokenIntrospection = _configuration.GetValue<bool?>("Auth:AllowRefreshTokenIntrospection"),
                RequireMfa = _configuration.GetValue<bool?>("Auth:RequireMfa"),
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = _configuration.GetValue<int?>("Auth:PasswordPolicy:MinLength"),
                    RequireUppercase = _configuration.GetValue<bool?>("Auth:PasswordPolicy:RequireUppercase"),
                    RequireLowercase = _configuration.GetValue<bool?>("Auth:PasswordPolicy:RequireLowercase"),
                    RequireDigit = _configuration.GetValue<bool?>("Auth:PasswordPolicy:RequireDigit"),
                    RequireSpecialChar = _configuration.GetValue<bool?>("Auth:PasswordPolicy:RequireSpecialChar")
                }
            },
            QrLogin = new QrLoginTenantSettings
            {
                Enabled = _configuration.GetValue<bool?>("QrLogin:Enabled"),
                SessionLifetimeSeconds = _configuration.GetValue<int?>("QrLogin:SessionLifetimeSeconds")
            },
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = _configuration.GetValue<int?>("Tokens:AccessTokenLifetimeSeconds"),
                RefreshTokenLifetimeSeconds = _configuration.GetValue<int?>("Tokens:RefreshTokenLifetimeSeconds"),
                AuthorizationCodeLifetimeSeconds = _configuration.GetValue<int?>("Tokens:AuthorizationCodeLifetimeSeconds"),
                IdTokenLifetimeSeconds = _configuration.GetValue<int?>("Tokens:IdTokenLifetimeSeconds")
            }
        };

        return settings;
    }

    /// <summary>
    /// Merges platform defaults with tenant-specific overrides.
    /// Tenant settings take precedence over platform defaults.
    /// </summary>
    private TenantSettings MergeSettings(TenantSettings platformDefaults, string? tenantSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(tenantSettingsJson))
        {
            return platformDefaults;
        }

        TenantSettings? tenantOverrides;
        try
        {
            tenantOverrides = JsonSerializer.Deserialize<TenantSettings>(tenantSettingsJson);
        }
        catch (JsonException)
        {
            // Invalid JSON - return platform defaults
            return platformDefaults;
        }

        if (tenantOverrides == null)
        {
            return platformDefaults;
        }

        // Merge logic: tenant overrides take precedence
        var merged = new TenantSettings
        {
            Oidc = MergeOidc(platformDefaults.Oidc, tenantOverrides.Oidc),
            Auth = MergeAuth(platformDefaults.Auth, tenantOverrides.Auth),
            QrLogin = MergeQrLogin(platformDefaults.QrLogin, tenantOverrides.QrLogin),
            Tokens = MergeTokens(platformDefaults.Tokens, tenantOverrides.Tokens)
        };

        return merged;
    }

    private OidcTenantSettings? MergeOidc(OidcTenantSettings? platform, OidcTenantSettings? tenant)
    {
        if (platform == null && tenant == null) return null;

        return new OidcTenantSettings
        {
            Issuer = tenant?.Issuer ?? platform?.Issuer,
            RequirePkce = tenant?.RequirePkce ?? platform?.RequirePkce,
            CorsOrigins = tenant?.CorsOrigins ?? platform?.CorsOrigins
        };
    }

    private AuthTenantSettings? MergeAuth(AuthTenantSettings? platform, AuthTenantSettings? tenant)
    {
        if (platform == null && tenant == null) return null;

        return new AuthTenantSettings
        {
            AllowRefreshTokenIntrospection = tenant?.AllowRefreshTokenIntrospection ?? platform?.AllowRefreshTokenIntrospection,
            RequireMfa = tenant?.RequireMfa ?? platform?.RequireMfa,
            PasswordPolicy = MergePasswordPolicy(platform?.PasswordPolicy, tenant?.PasswordPolicy)
        };
    }

    private PasswordPolicySettings? MergePasswordPolicy(PasswordPolicySettings? platform, PasswordPolicySettings? tenant)
    {
        if (platform == null && tenant == null) return null;

        return new PasswordPolicySettings
        {
            MinLength = tenant?.MinLength ?? platform?.MinLength,
            RequireUppercase = tenant?.RequireUppercase ?? platform?.RequireUppercase,
            RequireLowercase = tenant?.RequireLowercase ?? platform?.RequireLowercase,
            RequireDigit = tenant?.RequireDigit ?? platform?.RequireDigit,
            RequireSpecialChar = tenant?.RequireSpecialChar ?? platform?.RequireSpecialChar
        };
    }

    private QrLoginTenantSettings? MergeQrLogin(QrLoginTenantSettings? platform, QrLoginTenantSettings? tenant)
    {
        if (platform == null && tenant == null) return null;

        return new QrLoginTenantSettings
        {
            Enabled = tenant?.Enabled ?? platform?.Enabled,
            SessionLifetimeSeconds = tenant?.SessionLifetimeSeconds ?? platform?.SessionLifetimeSeconds
        };
    }

    private TokenTenantSettings? MergeTokens(TokenTenantSettings? platform, TokenTenantSettings? tenant)
    {
        if (platform == null && tenant == null) return null;

        return new TokenTenantSettings
        {
            AccessTokenLifetimeSeconds = tenant?.AccessTokenLifetimeSeconds ?? platform?.AccessTokenLifetimeSeconds,
            RefreshTokenLifetimeSeconds = tenant?.RefreshTokenLifetimeSeconds ?? platform?.RefreshTokenLifetimeSeconds,
            AuthorizationCodeLifetimeSeconds = tenant?.AuthorizationCodeLifetimeSeconds ?? platform?.AuthorizationCodeLifetimeSeconds,
            IdTokenLifetimeSeconds = tenant?.IdTokenLifetimeSeconds ?? platform?.IdTokenLifetimeSeconds
        };
    }
}
