using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account.Assistance;

/// <summary>
/// Requester and helper landing page for consent-based assistance. The page is a control surface: while a
/// helper overlay is active it deliberately runs as the helper, so the helper can always end the session.
/// </summary>
[Authorize]
public class IndexModel(
    IUserAssistanceService assistanceService,
    IOptions<AuthOptions> authOptions,
    ITenantAccessor tenantAccessor,
    ILogger<IndexModel> logger) : PageModel
{
    public bool FeatureEnabled { get; private set; }
    public bool TenantEnabled { get; private set; }
    public ResolvedAssistanceSettings? Settings { get; private set; }
    public List<UserAssistanceHelperCandidate> Helpers { get; private set; } = new();
    public List<UserAssistanceInfo> Outgoing { get; private set; } = new();
    public List<UserAssistanceInfo> Incoming { get; private set; } = new();
    public string? Message { get; private set; }

    [BindProperty]
    public RequestInput Input { get; set; } = new();

    [BindProperty]
    public Guid CancelRequestId { get; set; }

    [BindProperty]
    public Guid EndSessionId { get; set; }

    public class RequestInput
    {
        [Required(ErrorMessage = "Select a helper.")]
        public Guid HelperUserId { get; set; }

        public UserAssistanceMode Mode { get; set; } = UserAssistanceMode.ReadOnly;

        [Required(ErrorMessage = "Explain what you need help with.")]
        [StringLength(500, ErrorMessage = "The reason must be at most 500 characters.")]
        public string Reason { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "The ticket reference must be at most 100 characters.")]
        public string? TicketReference { get; set; }

        [Range(1, 1440, ErrorMessage = "The duration must be between 1 and 1440 minutes.")]
        public int? DurationMinutes { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostRequestAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            ModelState.AddModelError(string.Empty, "User assistance is disabled.");
            await LoadAsync().ConfigureAwait(false);
            return Page();
        }

        if (!TryGetUserId(out var userId))
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync().ConfigureAwait(false);
            return Page();
        }

        var result = await assistanceService.CreateRequestAsync(
            HttpContext,
            User,
            Input.HelperUserId,
            Input.Mode,
            Input.Reason,
            Input.DurationMinutes,
            Input.TicketReference,
            HttpContext.RequestAborted).ConfigureAwait(false);

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Could not create the assistance request.");
            await LoadAsync().ConfigureAwait(false);
            return Page();
        }

        TempData["Success"] = "Assistance request sent. The helper must accept it before the session starts.";
        logger.LogInformation(
            "User {RequesterUserId} created assistance request {RequestId}",
            userId,
            result.RequestId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        if (!TryGetUserId(out _))
        {
            return Challenge();
        }

        var result = await assistanceService.CancelAsync(
            HttpContext,
            User,
            CancelRequestId,
            HttpContext.RequestAborted).ConfigureAwait(false);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Assistance request cancelled."
            : result.Error ?? "The request could not be cancelled.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEndAsync()
    {
        if (!TryGetUserId(out _))
        {
            return Challenge();
        }

        var result = await assistanceService.EndAsync(
            HttpContext,
            User,
            EndSessionId,
            "Ended by requester",
            HttpContext.RequestAborted).ConfigureAwait(false);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Assistance session ended. The helper's access and tokens were revoked."
            : result.Error ?? "The session could not be ended.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            Message = "User assistance is disabled.";
            return;
        }

        if (!TryGetUserId(out var userId))
        {
            Message = "The signed-in identity could not be resolved.";
            return;
        }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            Message = "Select a tenant before using user assistance.";
            return;
        }

        Settings = await assistanceService.GetSettingsAsync(tenant.TenantId).ConfigureAwait(false);
        TenantEnabled = Settings?.Enabled == true;
        if (!TenantEnabled)
        {
            Message = "User assistance is disabled for this tenant.";
            return;
        }

        Helpers = (await assistanceService.ListEligibleHelpersAsync(
                tenant.TenantId,
                userId,
                HttpContext.RequestAborted).ConfigureAwait(false))
            .ToList();

        Outgoing = (await assistanceService.ListForRequesterAsync(userId, HttpContext.RequestAborted)
                .ConfigureAwait(false))
            .ToList();
        Incoming = (await assistanceService.ListForHelperAsync(userId, HttpContext.RequestAborted)
                .ConfigureAwait(false))
            .ToList();

        if (Helpers.Count == 0)
        {
            Message = "No eligible helper currently holds an assistance role in this tenant.";
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(value, out userId))
        {
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    public static string StatusLabel(UserAssistanceStatus status) => status switch
    {
        UserAssistanceStatus.Pending => "Pending",
        UserAssistanceStatus.Active => "Active",
        UserAssistanceStatus.Ended => "Ended",
        UserAssistanceStatus.Expired => "Expired",
        UserAssistanceStatus.Revoked => "Revoked",
        UserAssistanceStatus.Declined => "Declined",
        UserAssistanceStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string StatusBadgeClass(UserAssistanceStatus status) => status switch
    {
        UserAssistanceStatus.Pending => "bg-warning text-dark",
        UserAssistanceStatus.Active => "bg-success",
        UserAssistanceStatus.Declined or UserAssistanceStatus.Cancelled => "bg-secondary",
        UserAssistanceStatus.Revoked => "bg-danger",
        _ => "bg-light text-dark"
    };
}
