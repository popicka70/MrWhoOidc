using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.WebAuth.Pages.Admin;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly ITenantSettingsService _settingsService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    private readonly ILogger<SettingsModel> _logger;

    public SettingsModel(
        ITenantSettingsService settingsService,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        ILogger<SettingsModel> logger)
    {
        _settingsService = settingsService;
        _tenantAccessor = tenantAccessor;
        _multiTenancyOptions = multiTenancyOptions;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string TenantSlug { get; set; } = string.Empty;

    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public bool IsMultiTenantMode { get; set; }
    public string? SuccessMessage { get; set; }
    public TenantSettings? PlatformDefaults { get; set; }

    public class SettingsInput
    {
        // Auth settings (nullable backing fields for actual storage)
        private bool? _allowRefreshTokenIntrospection;
        private bool? _requireMfa;
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

        // Token lifetimes
        public int? AccessTokenLifetimeSeconds { get; set; }
        public int? RefreshTokenLifetimeSeconds { get; set; }
        public int? AuthorizationCodeLifetimeSeconds { get; set; }
        public int? IdTokenLifetimeSeconds { get; set; }

        // Methods to get nullable values for storage
        public bool? GetAllowRefreshTokenIntrospection() => _allowRefreshTokenIntrospection;
        public bool? GetRequireMfa() => _requireMfa;
        public bool? GetPasswordRequireUppercase() => _passwordRequireUppercase;
        public bool? GetPasswordRequireLowercase() => _passwordRequireLowercase;
        public bool? GetPasswordRequireDigit() => _passwordRequireDigit;
        public bool? GetPasswordRequireSpecialChar() => _passwordRequireSpecialChar;
        public bool? GetQrLoginEnabled() => _qrLoginEnabled;

        // Methods to set nullable values from loaded settings
        public void SetAllowRefreshTokenIntrospection(bool? value) => _allowRefreshTokenIntrospection = value;
        public void SetRequireMfa(bool? value) => _requireMfa = value;
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
            return RedirectToPage("/Admin/Clients/Index", new { tenantSlug = TenantSlug });
        }

        // Load current tenant settings
        var settings = await _settingsService.GetTenantSettingsAsync(tenantContext.TenantId);
        if (settings == null)
        {
            _logger.LogWarning("Tenant not found: {TenantId}", tenantContext.TenantId);
            return NotFound();
        }

        // Populate form with tenant-specific values (not merged - only overrides)
        PopulateForm(settings);

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

        // Build settings object from input
        var settings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                AllowRefreshTokenIntrospection = Input.GetAllowRefreshTokenIntrospection(),
                RequireMfa = Input.GetRequireMfa(),
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
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = Input.AccessTokenLifetimeSeconds,
                RefreshTokenLifetimeSeconds = Input.RefreshTokenLifetimeSeconds,
                AuthorizationCodeLifetimeSeconds = Input.AuthorizationCodeLifetimeSeconds,
                IdTokenLifetimeSeconds = Input.IdTokenLifetimeSeconds
            }
        };

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
        Input.PasswordMinLength = settings.Auth?.PasswordPolicy?.MinLength;
        Input.SetPasswordRequireUppercase(settings.Auth?.PasswordPolicy?.RequireUppercase);
        Input.SetPasswordRequireLowercase(settings.Auth?.PasswordPolicy?.RequireLowercase);
        Input.SetPasswordRequireDigit(settings.Auth?.PasswordPolicy?.RequireDigit);
        Input.SetPasswordRequireSpecialChar(settings.Auth?.PasswordPolicy?.RequireSpecialChar);
        Input.SetQrLoginEnabled(settings.QrLogin?.Enabled);
        Input.QrSessionLifetimeSeconds = settings.QrLogin?.SessionLifetimeSeconds;
        Input.AccessTokenLifetimeSeconds = settings.Tokens?.AccessTokenLifetimeSeconds;
        Input.RefreshTokenLifetimeSeconds = settings.Tokens?.RefreshTokenLifetimeSeconds;
        Input.AuthorizationCodeLifetimeSeconds = settings.Tokens?.AuthorizationCodeLifetimeSeconds;
        Input.IdTokenLifetimeSeconds = settings.Tokens?.IdTokenLifetimeSeconds;
    }
}
