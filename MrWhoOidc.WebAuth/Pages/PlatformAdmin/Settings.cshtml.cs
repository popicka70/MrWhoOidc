using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class SettingsModel : PageModel
{
    private readonly IPlatformSettingsService _settingsService;
    private readonly IOptions<AuthOptions> _authOptions;

    private readonly IPlatformInitialAccessTokenService _initialAccessTokens;
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMultiTenancyStateProvider _multiTenancy;

    public SettingsModel(
        IPlatformSettingsService settingsService,
        IOptions<AuthOptions> authOptions,
        IPlatformInitialAccessTokenService initialAccessTokens,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IMultiTenancyStateProvider multiTenancy)
    {
        _settingsService = settingsService;
        _authOptions = authOptions;
        _initialAccessTokens = initialAccessTokens;
        _db = db;
        _tenantAccessor = tenantAccessor;
        _multiTenancy = multiTenancy;
    }

    [BindProperty]
    public bool QrLoginAtDiscoveryEnabled { get; set; }

    [BindProperty]
    public RootLoginMode RootLoginMode { get; set; }

    [BindProperty]
    public bool DynamicClientRegistrationEnabled { get; set; }

    [BindProperty]
    public bool EnableTokenExchange { get; set; }

    [BindProperty]
    public Guid? SingleTenantDynamicClientRegistrationRealmId { get; set; }

    [BindProperty]
    public string? NewInitialAccessTokenDescription { get; set; }

    public bool IsMultiTenancyEnabled => _multiTenancy.IsEnabled;

    public List<SelectListItem> SingleTenantDynamicClientRegistrationRealmOptions { get; private set; } = new();

    public IReadOnlyList<PlatformInitialAccessToken> ActiveInitialAccessTokens { get; private set; } = Array.Empty<PlatformInitialAccessToken>();

    public async Task OnGetAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        QrLoginAtDiscoveryEnabled = settings.QrLoginAtDiscoveryEnabled;
        RootLoginMode = settings.RootLoginMode;
        DynamicClientRegistrationEnabled = settings.DynamicClientRegistrationEnabled;
        EnableTokenExchange = settings.EnableTokenExchange ?? _authOptions.Value.EnableTokenExchange;

        ActiveInitialAccessTokens = await _initialAccessTokens.GetActiveAsync();

        if (!IsMultiTenancyEnabled)
        {
            await LoadSingleTenantRealmOptionsAsync();
            SingleTenantDynamicClientRegistrationRealmId = await GetSingleTenantDynamicRegistrationRealmIdAsync();
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid)
        {
            ActiveInitialAccessTokens = await _initialAccessTokens.GetActiveAsync();
            if (!IsMultiTenancyEnabled)
            {
                await LoadSingleTenantRealmOptionsAsync();
            }

            return Page();
        }

        var settings = await _settingsService.GetSettingsAsync();
        settings.QrLoginAtDiscoveryEnabled = QrLoginAtDiscoveryEnabled;
        settings.RootLoginMode = RootLoginMode;
        settings.DynamicClientRegistrationEnabled = DynamicClientRegistrationEnabled;
        settings.EnableTokenExchange = EnableTokenExchange;
        await _settingsService.UpdateSettingsAsync(settings, User.Identity?.Name);

        if (!IsMultiTenancyEnabled)
        {
            var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                ModelState.AddModelError(string.Empty, "Default tenant context is not available.");
                ActiveInitialAccessTokens = await _initialAccessTokens.GetActiveAsync();
                await LoadSingleTenantRealmOptionsAsync();
                return Page();
            }

            if (SingleTenantDynamicClientRegistrationRealmId != null)
            {
                var exists = await _db.Realms.AnyAsync(r => r.TenantId == tenantId && r.Id == SingleTenantDynamicClientRegistrationRealmId.Value);
                if (!exists)
                {
                    ModelState.AddModelError(nameof(SingleTenantDynamicClientRegistrationRealmId), "Selected realm does not exist for the default tenant.");
                    ActiveInitialAccessTokens = await _initialAccessTokens.GetActiveAsync();
                    await LoadSingleTenantRealmOptionsAsync();
                    return Page();
                }
            }

            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null)
            {
                ModelState.AddModelError(string.Empty, "Default tenant not found.");
                ActiveInitialAccessTokens = await _initialAccessTokens.GetActiveAsync();
                await LoadSingleTenantRealmOptionsAsync();
                return Page();
            }

            tenant.SettingsJson = UpsertDynamicRegistrationRealm(tenant.SettingsJson, SingleTenantDynamicClientRegistrationRealmId);
            await _db.SaveChangesAsync();
        }

        TempData["SuccessMessage"] = "Platform settings saved successfully.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateInitialAccessTokenAsync()
    {
        var (entity, plaintext) = await _initialAccessTokens.CreateAsync(NewInitialAccessTokenDescription, User.Identity?.Name);

        TempData["SuccessMessage"] = "Initial access token created. Copy it now; it will not be shown again.";
        TempData["NewInitialAccessToken"] = plaintext;
        TempData["NewInitialAccessTokenId"] = entity.Id.ToString();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeInitialAccessTokenAsync(Guid id)
    {
        var revoked = await _initialAccessTokens.RevokeAsync(id, User.Identity?.Name);
        TempData["SuccessMessage"] = revoked ? "Initial access token revoked." : "Initial access token not found (or already revoked).";
        return RedirectToPage();
    }

    private async Task LoadSingleTenantRealmOptionsAsync()
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId == null)
        {
            SingleTenantDynamicClientRegistrationRealmOptions = new List<SelectListItem>
            {
                new() { Value = "", Text = "Disabled (no realm)" }
            };
            return;
        }

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

        SingleTenantDynamicClientRegistrationRealmOptions = options;
    }

    private async Task<Guid?> GetSingleTenantDynamicRegistrationRealmIdAsync()
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId == null)
        {
            return null;
        }

        var json = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.SettingsJson)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TenantSettings>(json);
            return settings?.Auth?.DynamicClientRegistrationRealmId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? UpsertDynamicRegistrationRealm(string? settingsJson, Guid? realmId)
    {
        TenantSettings settings;

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            settings = new TenantSettings();
        }
        else
        {
            try
            {
                settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson) ?? new TenantSettings();
            }
            catch (JsonException)
            {
                settings = new TenantSettings();
            }
        }

        settings.Auth ??= new AuthTenantSettings();
        settings.Auth.DynamicClientRegistrationRealmId = realmId;

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}
