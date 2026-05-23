using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Invitations;

[AllowAnonymous]
public class AcceptModel(
    ITenantEnrollmentService tenantEnrollment,
    ICurrentUserAccountResolver currentUserAccountResolver,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    public TenantInvitationDetails? Invitation { get; private set; }

    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    public string ReturnPath => $"/invitations/{Uri.EscapeDataString(Token)}";

    public string RegisterPath => $"/Registrations?invite={Uri.EscapeDataString(Token)}";

    public string SignInPath
    {
        get
        {
            var returnUrl = Uri.EscapeDataString(ReturnPath);
            if (multiTenancyOptions.Enabled && !string.IsNullOrWhiteSpace(Invitation?.TenantSlug))
            {
                return $"/t/{Uri.EscapeDataString(Invitation.TenantSlug)}/login?returnUrl={returnUrl}";
            }

            return $"/login?returnUrl={returnUrl}";
        }
    }

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    public async Task OnGetAsync()
    {
        await LoadInvitationAsync();
    }

    public async Task<IActionResult> OnPostAcceptAsync()
    {
        await LoadInvitationAsync();
        if (Invitation is null || !Invitation.IsAcceptable)
        {
            return Page();
        }

        var resolution = await currentUserAccountResolver.ResolveAsync(User, HttpContext.RequestAborted);
        if (resolution is null || resolution.Value.UserAccountId is not Guid userAccountId)
        {
            return Redirect(SignInPath);
        }

        var result = await tenantEnrollment.AcceptInvitationAsync(Token, userAccountId, HttpContext.RequestAborted);
        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage ?? "Invitation could not be accepted.";
            return Page();
        }

        SuccessMessage = "Invitation accepted.";
        var slug = result.TenantSlug ?? Invitation.TenantSlug;
        var target = multiTenancyOptions.Enabled ? $"/t/{slug}/account" : "/account";
        return Redirect(target);
    }

    private async Task LoadInvitationAsync()
    {
        Invitation = await tenantEnrollment.GetInvitationAsync(Token, HttpContext.RequestAborted);
        if (Invitation is null)
        {
            ErrorMessage = "Invitation link is invalid.";
            return;
        }

        if (!Invitation.IsAcceptable)
        {
            ErrorMessage = Invitation.Status == TenantInvitationStatus.Expired
                ? "Invitation link has expired. Ask your tenant admin for a new invitation."
                : "Invitation link is no longer available.";
        }
    }
}