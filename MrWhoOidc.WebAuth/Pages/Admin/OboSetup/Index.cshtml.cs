using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Admin.OboSetup;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IOboSetupOrchestrator oboOrchestrator,
    ILogger<IndexModel> logger) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private readonly AuthDbContext _db = db;
    private readonly IOboSetupOrchestrator _oboOrchestrator = oboOrchestrator;
    private readonly ILogger<IndexModel> _logger = logger;

    [FromQuery(Name = "step")]
    public string CurrentStep { get; set; } = "shape";

    [BindProperty]
    public OboWizardModel Wizard { get; set; } = new();

    public List<UserViewModel> AvailableUsers { get; private set; } = new();
    public OboProvisioningResult? ProvisioningResult { get; private set; }

    public List<ClientViewModelForSelection> AvailableClients { get; private set; } = new();
    public List<Realm> Realms { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            RedirectToPage("/Error", new { message = "Invalid tenant context" });
            return;
        }

        // Restore wizard model from TempData if available
        if (TempData.ContainsKey("OboWizardModel"))
        {
            var json = TempData["OboWizardModel"] as string;
            if (!string.IsNullOrEmpty(json))
            {
                Wizard = System.Text.Json.JsonSerializer.Deserialize<OboWizardModel>(json) ?? new OboWizardModel();
            }
            TempData.Peek("OboWizardModel"); // Keep for next request
        }

        // Load available users for assignment
        AvailableUsers = await _db.Users
            .Where(u => u.TenantId == tenantId)
            .Select(u => new UserViewModel { Id = u.Id, Email = u.Email ?? string.Empty, Name = u.Email ?? string.Empty })
            .OrderBy(u => u.Email)
            .ToListAsync();

        // Load available clients for existing-client mode
        AvailableClients = await _oboOrchestrator.ListAvailableUiClientsAsync(tenantId);

        // Load realms for realm selection dropdown
        Realms = await _db.Realms
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(string action = "")
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            ModelState.AddModelError("", "Invalid tenant context");
            return Page();
        }

        // Handle navigation actions (next/back)
        if (action == "next" || action == "back")
        {
            string nextStep = GetNextStep(CurrentStep, Wizard.Mode, action == "back");

            // Save wizard model to TempData for next request
            TempData["OboWizardModel"] = System.Text.Json.JsonSerializer.Serialize(Wizard);

            return RedirectToPage(new { step = nextStep });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Reload users and clients for re-display
        AvailableUsers = await _db.Users
            .Where(u => u.TenantId == tenantId)
            .Select(u => new UserViewModel { Id = u.Id, Email = u.Email ?? string.Empty, Name = u.Email ?? string.Empty })
            .OrderBy(u => u.Email)
            .ToListAsync();

        AvailableClients = await _oboOrchestrator.ListAvailableUiClientsAsync(tenantId);

        // Load realm
        var realm = await _db.Realms.FirstOrDefaultAsync(r => r.Id == Wizard.RealmId);
        if (realm == null)
        {
            ModelState.AddModelError(nameof(Wizard.RealmId), "Selected realm not found");
            return Page();
        }

        // Check mode and route to appropriate provisioning handler
        bool isExistingMode = !string.IsNullOrEmpty(Wizard.Mode) && Wizard.Mode == "existing";

        if (isExistingMode)
        {
            return await HandleExistingClientModeAsync(tenantId, realm);
        }
        else
        {
            return await HandleNewClientModeAsync(tenantId, realm);
        }
    }

    private async Task<IActionResult> HandleNewClientModeAsync(Guid tenantId, Realm realm)
    {
        var request = new OboSetupRequest
        {
            TenantId = tenantId,
            RealmId = Wizard.RealmId,
            SolutionName = Wizard.SolutionName,
            UiClientName = Wizard.UiClientName,
            UiClientId = Wizard.UiClientId,
            UiRedirectUris = Wizard.UiRedirectUris?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? new(),
            UiPostLogoutRedirectUris = Wizard.UiPostLogoutRedirectUris?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? new(),
            UiClientIsPublic = Wizard.UiClientIsPublic,
            UiClientRequirePkce = Wizard.UiClientRequirePkce,
            UiClientRequireConsent = Wizard.UiClientRequireConsent,
            ApiClientName = Wizard.ApiClientName,
            ApiClientId = Wizard.ApiClientId,
            ApiAudience = Wizard.ApiAudience,
            ApiDelegatedScopes = Wizard.ApiDelegatedScopes?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(),
            OboMaxDelegationDepth = Wizard.OboMaxDelegationDepth,
            OboMaxLifetimeMinutes = Wizard.OboMaxLifetimeMinutes,
            OboDpopMode = Wizard.OboDpopMode,
            UserIdsToAssign = ParseUserIds(Wizard.SelectedUserIds),
            EnableAutoAssignNewUsers = Wizard.EnableAutoAssignNewUsers,
            ProvisionedBy = User?.Identity?.Name ?? "system"
        };

        try
        {
            ProvisioningResult = await _oboOrchestrator.ProvisionOboSetupAsync(request);

            if (ProvisioningResult.Success)
            {
                _logger.LogInformation("✅ OBO setup provisioned successfully: UI={UiClientId}, API={ApiClientId}",
                    ProvisioningResult.UiClientId, ProvisioningResult.ApiClientId);
                TempData["SuccessMessage"] = $"OBO setup provisioned successfully!";
                TempData.Remove("OboWizardModel"); // Clear wizard data
                CurrentStep = "complete";
            }
            else
            {
                ModelState.AddModelError("", ProvisioningResult.ErrorMessage ?? "Provisioning failed");
                CurrentStep = "review";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ OBO setup provisioning error");
            ModelState.AddModelError("", $"Provisioning error: {ex.Message}");
            CurrentStep = "review";
        }

        return Page();
    }

    private async Task<IActionResult> HandleExistingClientModeAsync(Guid tenantId, Realm realm)
    {
        if (!Guid.TryParse(Wizard.ExistingUiClientId, out var uiClientId) || uiClientId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(Wizard.ExistingUiClientId), "Valid UI client must be selected");
            return Page();
        }

        if (!Guid.TryParse(Wizard.ExistingApiClientId, out var apiClientId) || apiClientId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(Wizard.ExistingApiClientId), "Valid API client must be selected");
            return Page();
        }

        var request = new OboExistingClientRequest
        {
            TenantId = tenantId,
            RealmId = Wizard.RealmId,
            SolutionName = Wizard.SolutionName,
            UiClientId = uiClientId,
            ApiClientId = apiClientId,
            ApiAudience = Wizard.ApiAudience,
            ApiDelegatedScopes = Wizard.ApiDelegatedScopes?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(),
            OboMaxDelegationDepth = Wizard.OboMaxDelegationDepth,
            OboMaxLifetimeMinutes = Wizard.OboMaxLifetimeMinutes,
            OboDpopMode = Wizard.OboDpopMode,
            UserIdsToAssign = ParseUserIds(Wizard.SelectedUserIds),
            EnableAutoAssignNewUsers = Wizard.EnableAutoAssignNewUsers,
            ProvisionedBy = User?.Identity?.Name ?? "system"
        };

        try
        {
            ProvisioningResult = await _oboOrchestrator.ConfigureExistingClientsForOboAsync(request);

            if (ProvisioningResult.Success)
            {
                _logger.LogInformation("✅ OBO configured on existing clients: UI={UiClientId}, API={ApiClientId}",
                    ProvisioningResult.UiClientId, ProvisioningResult.ApiClientId);
                TempData["SuccessMessage"] = $"OBO configuration updated successfully!";
                TempData.Remove("OboWizardModel"); // Clear wizard data
                CurrentStep = "complete";
            }
            else
            {
                ModelState.AddModelError("", ProvisioningResult.ErrorMessage ?? "Configuration failed");
                CurrentStep = "review";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ OBO configuration error");
            ModelState.AddModelError("", $"Configuration error: {ex.Message}");
            CurrentStep = "review";
        }

        return Page();
    }

    private string GetNextStep(string currentStep, string? mode, bool goBack = false)
    {
        bool isExistingMode = mode == "existing";

        var steps = isExistingMode
            ? new[] { "shape", "client-selection", "api-policy", "user-access", "review", "complete" }
            : new[] { "shape", "ui-client", "api-policy", "user-access", "review", "complete" };

        int currentIndex = Array.IndexOf(steps, currentStep);
        if (currentIndex == -1) return "shape"; // Invalid step, return to start

        if (goBack)
        {
            return currentIndex > 0 ? steps[currentIndex - 1] : steps[0];
        }
        else
        {
            return currentIndex < steps.Length - 1 ? steps[currentIndex + 1] : steps[currentIndex];
        }
    }

    private List<Guid> ParseUserIds(string? userIdString)
    {
        return userIdString?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList() ?? new();
    }

    public class OboWizardModel
    {
        [Required]
        public Guid RealmId { get; set; }

        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string SolutionName { get; set; } = string.Empty;

        /// <summary>
        /// Mode: "new" or "existing". Controls the flow of the wizard.
        /// </summary>
        public string? Mode { get; set; } = "new";

        // Existing client mode
        public string? ExistingUiClientId { get; set; }
        public string? ExistingApiClientId { get; set; }

        // UI Client (new mode only)
        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string UiClientName { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "Client ID must contain only alphanumeric characters, hyphens, and underscores")]
        public string UiClientId { get; set; } = string.Empty;

        public List<string> UiRedirectUris { get; set; } = new();
        public List<string> UiPostLogoutRedirectUris { get; set; } = new();
        public bool UiClientIsPublic { get; set; }
        public bool UiClientRequirePkce { get; set; } = true;
        public bool UiClientRequireConsent { get; set; } = false;

        // API Client
        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string ApiClientName { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        [RegularExpression(@"^[a-zA-Z0-9_:\-\.]+$", ErrorMessage = "API Client ID can be a URI or identifier")]
        public string ApiClientId { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [StringLength(200)]
        public string ApiAudience { get; set; } = string.Empty;

        public List<string> ApiDelegatedScopes { get; set; } = new();

        // OBO Policy
        [Range(1, 10)]
        public int OboMaxDelegationDepth { get; set; } = 1;

        [Range(5, 1440)]
        public int OboMaxLifetimeMinutes { get; set; } = 15;

        public string OboDpopMode { get; set; } = "Deny";

        // User Assignment
        public string? SelectedUserIds { get; set; }
        public bool EnableAutoAssignNewUsers { get; set; }
    }

    public class UserViewModel
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
