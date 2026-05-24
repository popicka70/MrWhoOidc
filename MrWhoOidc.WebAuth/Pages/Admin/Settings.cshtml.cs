using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.WebAuth.Pages.Admin;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly ITenantSettingsService _settingsService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ICliClientService _cliClientService;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    private readonly ILogger<SettingsModel> _logger;
    private readonly AuthDbContext _db;

    public SettingsModel(
        ITenantSettingsService settingsService,
        ITenantAccessor tenantAccessor,
        ICliClientService cliClientService,
        IMultiTenancyOptions multiTenancyOptions,
        ILogger<SettingsModel> logger,
        AuthDbContext db)
    {
        _settingsService = settingsService;
        _tenantAccessor = tenantAccessor;
        _cliClientService = cliClientService;
        _multiTenancyOptions = multiTenancyOptions;
        _logger = logger;
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string TenantSlug { get; set; } = string.Empty;

    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public bool IsMultiTenantMode { get; set; }
    public string? SuccessMessage { get; set; }
    public string? CurrentCliClientId { get; set; }
    public string? CurrentCliServerUrl { get; set; }
    public TenantSettings? PlatformDefaults { get; set; }

    public List<SelectListItem> DynamicClientRegistrationRealmOptions { get; private set; } = new();
    public List<SelectListItem> RegistrationModeOptions { get; } = new()
    {
        new() { Value = nameof(TenantUserRegistrationMode.PlatformOnly), Text = "Platform registration only" },
        new() { Value = nameof(TenantUserRegistrationMode.TenantOnly), Text = "Tenant-specific registration only" },
        new() { Value = nameof(TenantUserRegistrationMode.PlatformAndTenant), Text = "Platform and tenant-specific registration" }
    };

    public class SettingsInput
    {
        // Auth settings (nullable backing fields for actual storage)
        private bool? _allowRefreshTokenIntrospection;
        private bool? _requireMfa;
        private bool? _cliAccessEnabled;
        private bool? _passwordRequireUppercase;
        private bool? _passwordRequireLowercase;
        private bool? _passwordRequireDigit;
        private bool? _passwordRequireSpecialChar;
        private bool? _qrLoginEnabled;

        // UI binding properties (non-nullable for checkboxes)
        public bool AllowRefreshTokenIntrospection
        {
            get => _allowRefreshTokenIntrospection ?? false;
            set => _allowRefreshTokenIntrospection = value ? true : null;
        }

        public bool RequireMfa
        {
            get => _requireMfa ?? false;
            set => _requireMfa = value ? true : null;
        }

        public bool CliAccessEnabled
        {
            get => _cliAccessEnabled ?? false;
            set => _cliAccessEnabled = value ? true : null;
        }

        // Dynamic Client Registration
        public Guid? DynamicClientRegistrationRealmId { get; set; }

        // Password policy
        public int? PasswordMinLength { get; set; }

        public bool PasswordRequireUppercase
        {
            get => _passwordRequireUppercase ?? false;
            set => _passwordRequireUppercase = value ? true : null;
        }

        public bool PasswordRequireLowercase
        {
            get => _passwordRequireLowercase ?? false;
            set => _passwordRequireLowercase = value ? true : null;
        }

        public bool PasswordRequireDigit
        {
            get => _passwordRequireDigit ?? false;
            set => _passwordRequireDigit = value ? true : null;
        }

        public bool PasswordRequireSpecialChar
        {
            get => _passwordRequireSpecialChar ?? false;
            set => _passwordRequireSpecialChar = value ? true : null;
        }

        // QR Login
        public bool QrLoginEnabled
        {
            get => _qrLoginEnabled ?? false;
            set => _qrLoginEnabled = value ? true : null;
        }

        public int? QrSessionLifetimeSeconds { get; set; }

        // User registration
        public TenantUserRegistrationMode RegistrationMode { get; set; } = TenantUserRegistrationMode.PlatformOnly;

        [StringLength(120)]
        public string? RegistrationHeadline { get; set; }

        [StringLength(500)]
        public string? RegistrationIntroText { get; set; }

        [StringLength(500)]
        [Url(ErrorMessage = "Please enter a valid URL")]
        public string? RegistrationHeroImageUrl { get; set; }

        // Token lifetimes
        public int? AccessTokenLifetimeSeconds { get; set; }
        public int? RefreshTokenLifetimeSeconds { get; set; }
        public int? AuthorizationCodeLifetimeSeconds { get; set; }
        public int? IdTokenLifetimeSeconds { get; set; }

        // Methods to get nullable values for storage
        public bool? GetAllowRefreshTokenIntrospection() => _allowRefreshTokenIntrospection;
        public bool? GetRequireMfa() => _requireMfa;
        public bool? GetCliAccessEnabled() => _cliAccessEnabled;
        public bool? GetPasswordRequireUppercase() => _passwordRequireUppercase;
        public bool? GetPasswordRequireLowercase() => _passwordRequireLowercase;
        public bool? GetPasswordRequireDigit() => _passwordRequireDigit;
        public bool? GetPasswordRequireSpecialChar() => _passwordRequireSpecialChar;
        public bool? GetQrLoginEnabled() => _qrLoginEnabled;

        // Methods to set nullable values from loaded settings
        public void SetAllowRefreshTokenIntrospection(bool? value) => _allowRefreshTokenIntrospection = value;
        public void SetRequireMfa(bool? value) => _requireMfa = value;
        public void SetCliAccessEnabled(bool? value) => _cliAccessEnabled = value;
        public void SetPasswordRequireUppercase(bool? value) => _passwordRequireUppercase = value;
        public void SetPasswordRequireLowercase(bool? value) => _passwordRequireLowercase = value;
        public void SetPasswordRequireDigit(bool? value) => _passwordRequireDigit = value;
        public void SetPasswordRequireSpecialChar(bool? value) => _passwordRequireSpecialChar = value;
        public void SetQrLoginEnabled(bool? value) => _qrLoginEnabled = value;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        IsMultiTenantMode = _multiTenancyOptions.Enabled;
        PlatformDefaults = _settingsService.GetPlatformDefaults();

        var tenantContext = _tenantAccessor.CurrentTenant;
        if (tenantContext == null)
        {
            _logger.LogWarning("No tenant context available");
            return RedirectToPage("/admin/clients", new { tenantSlug = TenantSlug });
        }

        await LoadRealmOptionsAsync(tenantContext.TenantId);
        CurrentCliClientId = await _cliClientService.GetCliClientIdAsync(tenantContext.TenantId);
        CurrentCliServerUrl = tenantContext.IssuerUri?.TrimEnd('/');

        // Load current tenant settings overrides (not merged)
        var settingsOverrides = await GetTenantSettingsOverridesAsync(tenantContext.TenantId);
        PopulateForm(settingsOverrides);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsMultiTenantMode = _multiTenancyOptions.Enabled;
        PlatformDefaults = _settingsService.GetPlatformDefaults();

        if (!IsMultiTenantMode)
        {
            _logger.LogWarning("Settings update attempted in single-tenant mode");
            ModelState.AddModelError(string.Empty, "Settings overrides are only available in multi-tenant mode.");
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var tenantContext = _tenantAccessor.CurrentTenant;
        if (tenantContext == null)
        {
            _logger.LogWarning("No tenant context available for settings update");
            ModelState.AddModelError(string.Empty, "Unable to determine tenant context.");
            return Page();
        }

        await LoadRealmOptionsAsync(tenantContext.TenantId);
        CurrentCliClientId = await _cliClientService.GetCliClientIdAsync(tenantContext.TenantId);
        CurrentCliServerUrl = tenantContext.IssuerUri?.TrimEnd('/');

        if (Input.DynamicClientRegistrationRealmId != null)
        {
            var exists = await _db.Realms.AnyAsync(
                r => r.TenantId == tenantContext.TenantId && r.Id == Input.DynamicClientRegistrationRealmId.Value);

            if (!exists)
            {
                ModelState.AddModelError(nameof(Input.DynamicClientRegistrationRealmId), "Selected realm does not exist for this tenant.");
                return Page();
            }
        }

        // Build settings object from input
        var settings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                AllowRefreshTokenIntrospection = Input.GetAllowRefreshTokenIntrospection(),
                RequireMfa = Input.GetRequireMfa(),
                CliAccessEnabled = Input.GetCliAccessEnabled(),
                DynamicClientRegistrationRealmId = Input.DynamicClientRegistrationRealmId,
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = Input.PasswordMinLength,
                    RequireUppercase = Input.GetPasswordRequireUppercase(),
                    RequireLowercase = Input.GetPasswordRequireLowercase(),
                    RequireDigit = Input.GetPasswordRequireDigit(),
                    RequireSpecialChar = Input.GetPasswordRequireSpecialChar()
                }
            },
            QrLogin = new QrLoginTenantSettings
            {
                Enabled = Input.GetQrLoginEnabled(),
                SessionLifetimeSeconds = Input.QrSessionLifetimeSeconds
            },
            Registration = new RegistrationTenantSettings
            {
                Mode = Input.RegistrationMode,
                Headline = NormalizeOptional(Input.RegistrationHeadline),
                IntroText = NormalizeOptional(Input.RegistrationIntroText),
                HeroImageUrl = NormalizeOptional(Input.RegistrationHeroImageUrl)
            },
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = Input.AccessTokenLifetimeSeconds,
                RefreshTokenLifetimeSeconds = Input.RefreshTokenLifetimeSeconds,
                AuthorizationCodeLifetimeSeconds = Input.AuthorizationCodeLifetimeSeconds,
                IdTokenLifetimeSeconds = Input.IdTokenLifetimeSeconds
            }
        };

        if (Input.CliAccessEnabled)
        {
            var client = await _cliClientService.EnableCliAccessAsync(tenantContext.TenantId, tenantContext.Slug);
            CurrentCliClientId = client.ClientId;
        }
        else
        {
            await _cliClientService.DisableCliAccessAsync(tenantContext.TenantId, tenantContext.Slug);
            CurrentCliClientId = null;
        }

        var success = await _settingsService.UpdateTenantSettingsAsync(tenantContext.TenantId, settings);
        if (!success)
        {
            _logger.LogWarning("Failed to update settings for tenant: {TenantId}", tenantContext.TenantId);
            ModelState.AddModelError(string.Empty, "Failed to update settings.");
            return Page();
        }

        _logger.LogInformation(
            "Settings updated for tenant {TenantSlug} (ID: {TenantId})",
            tenantContext.Slug, tenantContext.TenantId);

        SuccessMessage = "Settings saved successfully!";
        return Page();
    }

    private void PopulateForm(TenantSettings settings)
    {
        Input = new SettingsInput();

        Input.SetAllowRefreshTokenIntrospection(settings.Auth?.AllowRefreshTokenIntrospection);
        Input.SetRequireMfa(settings.Auth?.RequireMfa);
        Input.SetCliAccessEnabled(settings.Auth?.CliAccessEnabled);
        Input.DynamicClientRegistrationRealmId = settings.Auth?.DynamicClientRegistrationRealmId;
        Input.PasswordMinLength = settings.Auth?.PasswordPolicy?.MinLength;
        Input.SetPasswordRequireUppercase(settings.Auth?.PasswordPolicy?.RequireUppercase);
        Input.SetPasswordRequireLowercase(settings.Auth?.PasswordPolicy?.RequireLowercase);
        Input.SetPasswordRequireDigit(settings.Auth?.PasswordPolicy?.RequireDigit);
        Input.SetPasswordRequireSpecialChar(settings.Auth?.PasswordPolicy?.RequireSpecialChar);
        Input.SetQrLoginEnabled(settings.QrLogin?.Enabled);
        Input.QrSessionLifetimeSeconds = settings.QrLogin?.SessionLifetimeSeconds;
        Input.RegistrationMode = settings.Registration?.Mode ?? TenantUserRegistrationMode.PlatformOnly;
        Input.RegistrationHeadline = settings.Registration?.Headline;
        Input.RegistrationIntroText = settings.Registration?.IntroText;
        Input.RegistrationHeroImageUrl = settings.Registration?.HeroImageUrl;
        Input.AccessTokenLifetimeSeconds = settings.Tokens?.AccessTokenLifetimeSeconds;
        Input.RefreshTokenLifetimeSeconds = settings.Tokens?.RefreshTokenLifetimeSeconds;
        Input.AuthorizationCodeLifetimeSeconds = settings.Tokens?.AuthorizationCodeLifetimeSeconds;
        Input.IdTokenLifetimeSeconds = settings.Tokens?.IdTokenLifetimeSeconds;
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task LoadRealmOptionsAsync(Guid tenantId)
    {
        var realms = await _db.Realms
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.DisplayName ?? r.Name)
            .Select(r => new { r.Id, r.Name, r.DisplayName })
            .ToListAsync();

        var options = new List<SelectListItem>
        {
            new() { Value = "", Text = "Disabled (no realm)" }
        };

        foreach (var realm in realms)
        {
            options.Add(new SelectListItem
            {
                Value = realm.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(realm.DisplayName) ? realm.Name : $"{realm.DisplayName} ({realm.Name})"
            });
        }

        DynamicClientRegistrationRealmOptions = options;
    }

    private async Task<TenantSettings> GetTenantSettingsOverridesAsync(Guid tenantId)
    {
        var json = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.SettingsJson)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(json))
        {
            return new TenantSettings();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<TenantSettings>(json) ?? new TenantSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            return new TenantSettings();
        }
    }
}
