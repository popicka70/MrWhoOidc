using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Users;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

/// <summary>
/// Represents an external identity provider option available for registration.
/// </summary>
public sealed record RegistrationIdpOption
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? LogoUrl { get; init; }
    public bool HasLogoData { get; init; }
    public string? ButtonBackgroundColor { get; init; }
    public string? ButtonTextColor { get; init; }
}

[AllowAnonymous]
public class IndexModel(
    IPasswordHasher hasher,
    IRegistrationWorkflowService registrationService,
    ITenantEnrollmentService tenantEnrollmentService,
    ITenantDomainClaimService tenantDomainClaimService,
    IReturnUrlClientContextResolver clientContextResolver,
    AuthDbContext dbContext,
    ITenantAccessor tenantAccessor,
    ITenantSettingsService tenantSettingsService,
    ITenantBrandingService tenantBrandingService,
    IMultiTenancyOptions multiTenancyOptions,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public RegistrationInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// When returning from IdP callback, indicates flow mode.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Mode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Invite { get; set; }

    public string? SuccessMessage { get; private set; }
    public string? InfoMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// External identity providers available for registration.
    /// </summary>
    public List<RegistrationIdpOption> RegistrationIdps { get; private set; } = [];

    public TenantInvitationDetails? Invitation { get; private set; }

    public bool IsTenantRegistrationPath { get; private set; }

    public bool IsRegistrationAvailable { get; private set; } = true;

    public TenantUserRegistrationMode RegistrationMode { get; private set; } = TenantUserRegistrationMode.PlatformOnly;

    public TenantBranding TenantBranding { get; private set; } = new() { TenantName = "MrWhoOidc" };

    public string? CurrentTenantSlug { get; private set; }

    public string RegistrationPath { get; private set; } = "/Registrations";

    public string PageHeading { get; private set; } = "User Registration";

    public string? PageIntro { get; private set; }

    public string? HeroImageUrl { get; private set; }

    public string? TenantRegistrationUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadRegistrationContextAsync();

        if (TryRedirectTenantRegistrationToPlatform() is { } platformRedirect)
        {
            return platformRedirect;
        }

        // Handle IdP callback mode
        if (Mode == "idp_callback")
        {
            SuccessMessage = "Registration successful! Please check your email for confirmation instructions.";
        }
        else if (Mode == "idp_duplicate")
        {
            ErrorMessage = "An account with this email already exists. Please sign in instead.";
        }

        await LoadInvitationAsync();
        if (Invitation is { IsAcceptable: true })
        {
            if (await ShouldRedirectInvitationToTenantRegistrationAsync(Invitation))
            {
                return Redirect(BuildTenantRegistrationPath(Invitation.TenantSlug, Invite));
            }

            if (IsTenantRegistrationPath && !string.Equals(CurrentTenantSlug, Invitation.TenantSlug, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "This invitation belongs to a different tenant.";
                IsRegistrationAvailable = false;
            }

            Input.Email = Invitation.Email;
        }

        if (IsRegistrationAvailable)
        {
            await LoadRegistrationIdpsAsync();
        }

        return Page();
    }

    private async Task LoadRegistrationContextAsync()
    {
        var requestPath = HttpContext.Request.Path.Value ?? string.Empty;
        var currentTenant = tenantAccessor.CurrentTenant;

        IsTenantRegistrationPath = multiTenancyOptions.Enabled
            && requestPath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase);
        CurrentTenantSlug = currentTenant?.Slug;
        RegistrationPath = IsTenantRegistrationPath && !string.IsNullOrWhiteSpace(CurrentTenantSlug)
            ? $"/t/{Uri.EscapeDataString(CurrentTenantSlug)}/Registrations"
            : "/Registrations";

        TenantBranding = await tenantBrandingService.GetCurrentTenantBrandingAsync();
        var settings = await tenantSettingsService.GetCurrentTenantSettingsAsync();
        RegistrationMode = settings.Registration?.Mode ?? TenantUserRegistrationMode.PlatformOnly;

        if (IsTenantRegistrationPath)
        {
            IsRegistrationAvailable = IsTenantRegistrationAllowed(RegistrationMode);
            PageHeading = !string.IsNullOrWhiteSpace(settings.Registration?.Headline)
                ? settings.Registration.Headline!
                : $"Register with {TenantBranding.TenantName}";
            PageIntro = settings.Registration?.IntroText;
            HeroImageUrl = settings.Registration?.HeroImageUrl;

            if (!IsRegistrationAvailable && string.IsNullOrWhiteSpace(ErrorMessage))
            {
                ErrorMessage = "Tenant-specific registration is not enabled for this tenant.";
            }
        }
        else
        {
            IsRegistrationAvailable = true;
            PageHeading = "Platform Account Registration";
            PageIntro = "Submit a platform account request for administrator approval.";
            HeroImageUrl = null;
        }
    }

    private async Task LoadInvitationAsync()
    {
        Invitation = null;
        if (string.IsNullOrWhiteSpace(Invite))
        {
            return;
        }

        Invitation = await tenantEnrollmentService.GetInvitationAsync(Invite, HttpContext.RequestAborted);
        if (Invitation is null)
        {
            ErrorMessage = "Invitation link is invalid.";
        }
        else if (!Invitation.IsAcceptable)
        {
            ErrorMessage = Invitation.Status == TenantInvitationStatus.Expired
                ? "Invitation link has expired. Ask your tenant admin for a new invitation."
                : "Invitation link is no longer available.";
        }
    }

    private async Task LoadRegistrationIdpsAsync()
    {
        try
        {
            var tenantId = IsTenantRegistrationPath
                ? tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty
                : await GetDefaultTenantIdAsync();

            if (tenantId == Guid.Empty)
            {
                logger.LogWarning("No tenant found for registration IdP loading. TenantPath={TenantPath}, Slug={Slug}", IsTenantRegistrationPath, CurrentTenantSlug);
                return;
            }

            // Load IdPs that are enabled and allow registration
            RegistrationIdps = await dbContext.IdentityProviders
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId
                         && p.Enabled
                         && p.AllowRegistration)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.DisplayName)
                .Select(p => new RegistrationIdpOption
                {
                    Id = p.Id,
                    Name = p.Name,
                    DisplayName = p.DisplayName ?? p.Name,
                    UpdatedAt = p.UpdatedAt,
                    LogoUrl = p.LogoUrl,
                    HasLogoData = p.LogoData != null,
                    ButtonBackgroundColor = p.ButtonBackgroundColor,
                    ButtonTextColor = p.ButtonTextColor
                })
                .ToListAsync();

            logger.LogDebug("Loaded {Count} registration-enabled IdPs for registration page", RegistrationIdps.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load registration IdPs");
            // Don't throw - page should still render with manual registration option
        }
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadRegistrationContextAsync();

        if (TryRedirectTenantRegistrationToPlatform() is { } platformRedirect)
        {
            return platformRedirect;
        }

        if (!ModelState.IsValid)
        {
            await LoadInvitationAsync();
            return await RenderPageAsync();
        }

        if (!IsRegistrationAvailable)
        {
            ModelState.AddModelError(string.Empty, ErrorMessage ?? "Registration is not available for this tenant.");
            return await RenderPageAsync();
        }

        try
        {
            await LoadInvitationAsync();
            if (!string.IsNullOrWhiteSpace(Invite))
            {
                if (Invitation is null || !Invitation.IsAcceptable)
                {
                    ModelState.AddModelError(string.Empty, ErrorMessage ?? "Invitation link is no longer available.");
                    return await RenderPageAsync();
                }

                if (await ShouldRedirectInvitationToTenantRegistrationAsync(Invitation))
                {
                    return Redirect(BuildTenantRegistrationPath(Invitation.TenantSlug, Invite));
                }

                if (IsTenantRegistrationPath && !string.Equals(CurrentTenantSlug, Invitation.TenantSlug, StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(string.Empty, "This invitation belongs to a different tenant.");
                    return await RenderPageAsync();
                }

                var normalizedInputEmail = EmailNormalizer.NormalizeForLookup(Input.Email);
                if (!string.Equals(normalizedInputEmail, Invitation.NormalizedEmail, StringComparison.Ordinal))
                {
                    ModelState.AddModelError(nameof(Input.Email), "Use the email address this invitation was sent to.");
                    return await RenderPageAsync();
                }
            }

            string? passwordHash = null;
            if (!string.IsNullOrWhiteSpace(Input.Password))
            {
                passwordHash = hasher.Hash(Input.Password);
            }

            // Determine tenant creation parameters
            string? tenantSlug = null;
            string? tenantName = null;
            string? tenantDescription = null;

            if (Input.CreateTenant && Invitation is not null)
            {
                ModelState.AddModelError(nameof(Input.CreateTenant), "Create a new tenant from a separate registration, not from an invitation link.");
                return await RenderPageAsync();
            }

            if (Input.CreateTenant && IsTenantRegistrationPath)
            {
                ModelState.AddModelError(nameof(Input.CreateTenant), "Create a new tenant from the platform registration page.");
                return await RenderPageAsync();
            }

            if (Input.CreateTenant)
            {
                tenantSlug = Input.TenantSlug?.Trim().ToLowerInvariant();
                tenantName = Input.TenantName?.Trim();
                tenantDescription = Input.TenantDescription?.Trim();

                // Validate tenant fields when creating tenant
                if (string.IsNullOrWhiteSpace(tenantSlug))
                {
                    ModelState.AddModelError(nameof(Input.TenantSlug), "Tenant slug is required.");
                    return await RenderPageAsync();
                }
                if (string.IsNullOrWhiteSpace(tenantName))
                {
                    ModelState.AddModelError(nameof(Input.TenantName), "Tenant name is required.");
                    return await RenderPageAsync();
                }
            }

            // Use the registration service instead of direct DB operations
            // Only associate a client when we can derive it from a validated authorize context and the client opts in.
            // Auto-approve only when creating a new tenant (user becomes tenant admin)
            Guid? clientId = null;
            var autoApprove = Input.CreateTenant || Invitation is not null;
            TenantDomainEnrollmentMatch? domainEnrollment = null;
            Guid? targetTenantId = Invitation?.TenantId ?? (IsTenantRegistrationPath ? tenantAccessor.CurrentTenant?.TenantId : null);
            var isPlatformRegistration = !Input.CreateTenant && Invitation is null && !IsTenantRegistrationPath;
            if (!Input.CreateTenant)
            {
                Client? client = await clientContextResolver.TryResolveClientAsync(HttpContext, ReturnUrl, HttpContext.RequestAborted);
                if (client is not null)
                {
                    targetTenantId = client.TenantId;
                    isPlatformRegistration = false;
                }

                if (client is not null && client.AutoAssignNewUsersToClient)
                {
                    clientId = client.Id;
                }

                if (client is not null && client.AutoApprovalMode == AutoApprovalMode.All)
                {
                    autoApprove = true;
                }

                if (Invitation is null && !IsTenantRegistrationPath)
                {
                    domainEnrollment = await tenantDomainClaimService.ResolveAutoJoinClaimAsync(Input.Email, HttpContext.RequestAborted);
                    if (domainEnrollment is not null)
                    {
                        if (!await IsPlatformRegistrationAllowedForTenantAsync(domainEnrollment.TenantId))
                        {
                            TenantRegistrationUrl = BuildTenantRegistrationPath(domainEnrollment.TenantSlug, inviteToken: null);
                            ModelState.AddModelError(nameof(Input.Email), $"Use {domainEnrollment.TenantName}'s tenant registration page for this email domain.");
                            return await RenderPageAsync();
                        }

                        targetTenantId = domainEnrollment.TenantId;
                        autoApprove = true;
                        isPlatformRegistration = false;
                    }
                }

                if (isPlatformRegistration)
                {
                    targetTenantId = await GetDefaultTenantIdAsync();
                    if (!targetTenantId.HasValue || targetTenantId.Value == Guid.Empty)
                    {
                        logger.LogError("Platform registration could not resolve the default platform tenant. DefaultSlug={DefaultSlug}", multiTenancyOptions.DefaultTenantSlug);
                        ModelState.AddModelError(string.Empty, "Platform registration is not available right now.");
                        return await RenderPageAsync();
                    }
                }
            }

            var result = await registrationService.CreateAndMaybeApproveRegistrationAsync(
                email: Input.Email.Trim(),
                firstName: string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim(),
                lastName: string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim(),
                clientId: clientId,
                passwordHash: passwordHash,
                isExternalIdp: false, // Local registration
                autoApprove: autoApprove,
                tenantSlug: tenantSlug,
                tenantName: tenantName,
                tenantDescription: tenantDescription,
                targetTenantId: targetTenantId,
                isPlatformRegistration: isPlatformRegistration,
                autoConfirmEmail: domainEnrollment is not null); // Domain-claim enrollment: email domain is already verified by tenant admin

            if (Invitation is { IsAcceptable: true } && result.Outcome == RegistrationOutcome.Approved && result.CreatedUserId.HasValue)
            {
                var acceptResult = await tenantEnrollmentService.AcceptInvitationForUserAsync(Invite!, result.CreatedUserId.Value, HttpContext.RequestAborted);
                if (!acceptResult.Success)
                {
                    ModelState.AddModelError(string.Empty, acceptResult.ErrorMessage ?? "Invitation could not be accepted.");
                    return await RenderPageAsync();
                }
            }

            switch (result.Outcome)
            {
                case RegistrationOutcome.Approved:
                    if (Input.CreateTenant)
                    {
                        return RedirectToAcceptedPage("tenant-created", tenantName);
                    }

                    if (domainEnrollment is not null)
                    {
                        return RedirectToAcceptedPage("domain-approved", domainEnrollment.TenantName);
                    }

                    return RedirectToAcceptedPage("approved");
                case RegistrationOutcome.PendingCreated:
                    return RedirectToAcceptedPage("pending");
                case RegistrationOutcome.PendingExisting:
                    return RedirectToAcceptedPage("pending-existing");
                case RegistrationOutcome.ExistingUser:
                    // Do not disclose that an account already exists for this email — that lets an
                    // unauthenticated attacker enumerate registered addresses. Return the same
                    // generic "check your email" response as a brand-new registration.
                    return RedirectToAcceptedPage("pending");
                default:
                    InfoMessage = "Registration submitted. Please check your email for next steps.";
                    break;
            }

            ModelState.Clear();
            Input = new();
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception)
        {
            ModelState.AddModelError(string.Empty, "An error occurred during registration. Please try again.");
        }

        return await RenderPageAsync();
    }

    private IActionResult RedirectToAcceptedPage(string status, string? tenantName = null)
    {
        var path = IsTenantRegistrationPath && !string.IsNullOrWhiteSpace(CurrentTenantSlug)
            ? $"/t/{Uri.EscapeDataString(CurrentTenantSlug)}/Registrations/Accepted"
            : "/Registrations/Accepted";

        var query = new List<string>
        {
            $"status={Uri.EscapeDataString(status)}"
        };

        if (!string.IsNullOrWhiteSpace(ReturnUrl))
        {
            query.Add($"returnUrl={Uri.EscapeDataString(ReturnUrl)}");
        }

        if (!string.IsNullOrWhiteSpace(tenantName))
        {
            query.Add($"tenantName={Uri.EscapeDataString(tenantName)}");
        }

        return Redirect($"{path}?{string.Join('&', query)}");
    }

    private async Task<PageResult> RenderPageAsync()
    {
        await LoadRegistrationContextAsync();
        await LoadInvitationAsync();
        if (IsRegistrationAvailable)
        {
            await LoadRegistrationIdpsAsync();
        }
        return Page();
    }

    private IActionResult? TryRedirectTenantRegistrationToPlatform()
    {
        if (!IsTenantRegistrationPath || IsRegistrationAvailable || !IsPlatformRegistrationAllowed(RegistrationMode))
        {
            return null;
        }

        var query = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value ?? string.Empty
            : string.Empty;
        return Redirect($"/Registrations{query}");
    }

    private async Task<Guid> GetDefaultTenantIdAsync()
    {
        var defaultTenantSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
        return await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == defaultTenantSlug && t.Status == TenantStatus.Active)
            .Select(t => t.Id)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> ShouldRedirectInvitationToTenantRegistrationAsync(TenantInvitationDetails invitation)
    {
        if (IsTenantRegistrationPath || !multiTenancyOptions.Enabled)
        {
            return false;
        }

        var mode = await GetRegistrationModeForTenantAsync(invitation.TenantId);
        return !IsPlatformRegistrationAllowed(mode) && IsTenantRegistrationAllowed(mode);
    }

    private async Task<bool> IsPlatformRegistrationAllowedForTenantAsync(Guid tenantId)
    {
        var mode = await GetRegistrationModeForTenantAsync(tenantId);
        return IsPlatformRegistrationAllowed(mode);
    }

    private async Task<TenantUserRegistrationMode> GetRegistrationModeForTenantAsync(Guid tenantId)
    {
        var settings = await tenantSettingsService.GetTenantSettingsAsync(tenantId);
        return settings?.Registration?.Mode ?? TenantUserRegistrationMode.PlatformOnly;
    }

    private static bool IsPlatformRegistrationAllowed(TenantUserRegistrationMode mode)
        => mode is TenantUserRegistrationMode.PlatformOnly or TenantUserRegistrationMode.PlatformAndTenant;

    private static bool IsTenantRegistrationAllowed(TenantUserRegistrationMode mode)
        => mode is TenantUserRegistrationMode.TenantOnly or TenantUserRegistrationMode.PlatformAndTenant;

    private static string BuildTenantRegistrationPath(string tenantSlug, string? inviteToken)
    {
        var path = $"/t/{Uri.EscapeDataString(tenantSlug)}/Registrations";
        return string.IsNullOrWhiteSpace(inviteToken)
            ? path
            : $"{path}?invite={Uri.EscapeDataString(inviteToken)}";
    }

    public sealed class RegistrationInput
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;
        [StringLength(100)]
        public string? FirstName { get; set; }
        [StringLength(100)]
        public string? LastName { get; set; }
        [StringLength(200)]
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        // New: Tenant creation options
        public bool CreateTenant { get; set; }
        [StringLength(100)]
        public string? TenantSlug { get; set; }
        [StringLength(200)]
        public string? TenantName { get; set; }
        [StringLength(500)]
        public string? TenantDescription { get; set; }
    }
}
