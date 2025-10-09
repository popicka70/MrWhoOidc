using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.UnitTests.Helpers;

/// <summary>
/// Mock implementation of ITenantSettingsService for unit tests.
/// Returns platform defaults only.
/// </summary>
public class MockTenantSettingsService : ITenantSettingsService
{
    private readonly TenantSettings _settings;

    public MockTenantSettingsService()
    {
        // Return sensible defaults for tests
        _settings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                RequirePkce = false
            },
            Auth = new AuthTenantSettings
            {
                RequireMfa = false,
                AllowRefreshTokenIntrospection = true,
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = 6,
                    RequireUppercase = false,
                    RequireLowercase = false,
                    RequireDigit = false,
                    RequireSpecialChar = false
                }
            },
            QrLogin = new QrLoginTenantSettings
            {
                Enabled = true,
                SessionLifetimeSeconds = 600
            },
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = 3600,  // 1 hour
                IdTokenLifetimeSeconds = 3600,      // 1 hour
                RefreshTokenLifetimeSeconds = 1296000, // 15 days
                AuthorizationCodeLifetimeSeconds = 300 // 5 minutes
            }
        };
    }

    public MockTenantSettingsService(TenantSettings settings)
    {
        _settings = settings;
    }

    public Task<TenantSettings?> GetTenantSettingsAsync(Guid tenantId)
    {
        return Task.FromResult<TenantSettings?>(_settings);
    }

    public Task<TenantSettings> GetCurrentTenantSettingsAsync()
    {
        return Task.FromResult(_settings);
    }

    public Task<bool> UpdateTenantSettingsAsync(Guid tenantId, TenantSettings settings)
    {
        throw new NotImplementedException("Update not supported in mock");
    }

    public TenantSettings GetPlatformDefaults()
    {
        return _settings;
    }

    /// <summary>
    /// Sets the password policy for testing
    /// </summary>
    public void SetPasswordPolicy(PasswordPolicySettings policy)
    {
        if (_settings.Auth is null)
        {
            _settings.Auth = new AuthTenantSettings();
        }
        _settings.Auth.PasswordPolicy = policy;
    }

    /// <summary>
    /// Creates a mock with custom token lifetimes for testing
    /// </summary>
    public static MockTenantSettingsService WithTokenLifetimes(
        int? accessTokenSeconds = null,
        int? idTokenSeconds = null,
        int? refreshTokenSeconds = null)
    {
        var settings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = accessTokenSeconds ?? 3600,
                IdTokenLifetimeSeconds = idTokenSeconds ?? 3600,
                RefreshTokenLifetimeSeconds = refreshTokenSeconds ?? 1296000,
                AuthorizationCodeLifetimeSeconds = 300
            }
        };
        return new MockTenantSettingsService(settings);
    }
}
