using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Pages;

public class LoginModel(
    IUserService users,
    IGlobalAuthenticationService globalAuthService,
    ILogger<LoginModel> logger,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantSettingsService settingsService,
    ITenantBrandingService brandingService,
    ITenantCredentialTicketStore ticketStore,
    MrWhoOidc.WebAuth.Services.ILoginContinuationStore continuationStore) : PageModel
{
    // Local IP-based rate limiting (defense in depth - complements global account lockout)
    private static readonly Dictionary<string, (int Attempts, DateTimeOffset First)> _attempts = new();
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [BindProperty]
    // Accepts either traditional username or an email address.
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Ctx { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Email { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TicketId { get; set; }

    public bool ShowNotYouLink => !string.IsNullOrEmpty(Email);

    public TenantBranding? TenantBranding { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrEmpty(ReturnUrl) && !string.IsNullOrEmpty(Ctx))
        {
            ReturnUrl = await continuationStore.TryGetAsync(Ctx, HttpContext.RequestAborted);
            if (string.IsNullOrEmpty(ReturnUrl))
            {
                ModelState.AddModelError(string.Empty, "Your sign-in session expired. Please start again.");
            }
        }

        logger.LogInformation("🔍 [Login Page GET] ReturnUrlLength={ReturnUrlLength}, HasCtx={HasCtx}, Email={Email}",
            ReturnUrl?.Length ?? 0,
            !string.IsNullOrEmpty(Ctx),
            Email ?? "(null)");

        // Pre-fill username with email if provided
        if (!string.IsNullOrEmpty(Email))
        {
            Username = Email;
        }

        // Load tenant branding for display
        try
        {
            TenantBranding = await brandingService.GetCurrentTenantBrandingAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load tenant branding, using default");
            TenantBranding = null;
        }

        if (!string.IsNullOrEmpty(TicketId))
        {
            var result = await TryCompleteLoginWithTicketAsync();
            if (result is not null)
            {
                return result;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(ReturnUrl) && !string.IsNullOrEmpty(Ctx))
        {
            ReturnUrl = await continuationStore.TryGetAsync(Ctx, HttpContext.RequestAborted);
            if (string.IsNullOrEmpty(ReturnUrl))
            {
                ModelState.AddModelError(string.Empty, "Your sign-in session expired. Please start again.");
            }
        }

        logger.LogInformation("🔐 [Login POST] Username={Username}, ReturnUrlLength={ReturnUrlLength}, HasCtx={HasCtx}, ModelStateValid={Valid}, CurrentTenant={TenantSlug}, TenantId={TenantId}",
            Username ?? "(null)",
            ReturnUrl?.Length ?? 0,
            !string.IsNullOrEmpty(Ctx),
            ModelState.IsValid,
            tenantAccessor.CurrentTenant?.Slug ?? "(null)",
            tenantAccessor.CurrentTenant?.TenantId.ToString() ?? "(null)");

        if (!ModelState.IsValid)
            return Page();

        // Validate username is not null
        if (string.IsNullOrWhiteSpace(Username))
        {
            ModelState.AddModelError(string.Empty, "Username is required");
            return Page();
        }

        // Use global authentication service for credential verification
        var authResult = await globalAuthService.AuthenticateAsync(Username, Password);
        
        logger.LogInformation("🔍 [Login POST] Global auth result: Succeeded={Succeeded}, FailureReason={FailureReason}",
            authResult.Succeeded,
            authResult.FailureReason?.ToString() ?? "(none)");

        if (!authResult.Succeeded)
        {
            // Handle different failure reasons with appropriate messages
            var errorMessage = authResult.FailureReason switch
            {
                AuthenticationFailureReason.AccountLocked => 
                    $"Your account is temporarily locked. Please try again {FormatLockoutTime(authResult.LockedUntil)}.",
                AuthenticationFailureReason.NoActiveMemberships => 
                    "Your account does not have access to any organizations. Please contact your administrator.",
                AuthenticationFailureReason.MfaRequired => 
                    null, // MFA required is not an error - we'll handle it below
                _ => "Invalid username or password"
            };

            if (authResult.FailureReason == AuthenticationFailureReason.MfaRequired)
            {
                // Store preauth and redirect to MFA challenge
                return await HandleMfaRequiredAsync(authResult.Account!);
            }

            logger.LogWarning("⚠️ [Login POST] Authentication failed: Reason={Reason}",
                authResult.FailureReason);
            
            ModelState.AddModelError(string.Empty, errorMessage!);
            return Page();
        }

        // Get the user for the current tenant from the memberships
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        var membership = currentTenantId.HasValue 
            ? authResult.Memberships.FirstOrDefault(m => m.TenantId == currentTenantId.Value)
            : authResult.Memberships.FirstOrDefault();

        if (membership == null)
        {
            logger.LogWarning("⚠️ [Login POST] User {AccountId} has no membership for tenant {TenantId}",
                authResult.Account!.Id, currentTenantId);
            ModelState.AddModelError(string.Empty, "You do not have access to this organization.");
            return Page();
        }

        // Look up the per-tenant User record for session/claims
        var user = await users.FindByUsernameAsync(authResult.Account!.Username) 
                   ?? await users.FindByUsernameOrEmailAsync(authResult.Account.Email ?? authResult.Account.Username);
        
        if (user is null)
        {
            logger.LogWarning("⚠️ [Login POST] No per-tenant User record for UserAccount {AccountId} in tenant {TenantId}",
                authResult.Account.Id, currentTenantId);
            ModelState.AddModelError(string.Empty, "Account configuration error. Please contact support.");
            return Page();
        }

        var result = await CompleteSignInAsync(user);
        if (!string.IsNullOrEmpty(Ctx))
        {
            await continuationStore.RemoveAsync(Ctx, HttpContext.RequestAborted);
        }
        return result;
    }

    private async Task<IActionResult> HandleMfaRequiredAsync(UserAccount account)
    {
        // Look up the per-tenant user to get the ID for preauth
        var user = await users.FindByUsernameAsync(account.Username) 
                   ?? await users.FindByUsernameOrEmailAsync(account.Email ?? account.Username);
        
        if (user is null)
        {
            logger.LogWarning("⚠️ [Login MFA] No per-tenant User record for UserAccount {AccountId}", account.Id);
            ModelState.AddModelError(string.Empty, "Account configuration error. Please contact support.");
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.Amr, "pwd")
        };
        var identity = new ClaimsIdentity(claims, "preauth");
        await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(identity));
        
        logger.LogInformation("🔐 [Login MFA] User {User} requires MFA, redirecting to TOTP page", user.Username);
        var url = Url.Page("/LoginTotp", null, new { ReturnUrl }, protocol: Request.Scheme);
        return Redirect(url ?? "/LoginTotp");
    }

    private static string FormatLockoutTime(DateTimeOffset? lockedUntil)
    {
        if (!lockedUntil.HasValue)
            return "later";
        
        var remaining = lockedUntil.Value - DateTimeOffset.UtcNow;
        if (remaining.TotalMinutes > 1)
            return $"in {(int)remaining.TotalMinutes} minutes";
        if (remaining.TotalSeconds > 30)
            return "in about a minute";
        return "shortly";
    }

    static string Key(HttpContext ctx, string username) => $"{ctx.Connection.RemoteIpAddress}-{username}";

    static bool IsLockedOut(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        if (_attempts.TryGetValue(key, out var info))
        {
            if (DateTimeOffset.UtcNow - info.First > Window)
            {
                _attempts.Remove(key);
                return false;
            }
            return info.Attempts >= MaxAttempts;
        }
        return false;
    }

    static void RegisterFailedAttempt(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        if (_attempts.TryGetValue(key, out var info))
        {
            if (DateTimeOffset.UtcNow - info.First > Window)
                _attempts[key] = (1, DateTimeOffset.UtcNow);
            else
                _attempts[key] = (info.Attempts + 1, info.First);
        }
        else
        {
            _attempts[key] = (1, DateTimeOffset.UtcNow);
        }
    }

    static void ClearAttempts(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        _attempts.Remove(key);
    }

    private async Task<IActionResult?> TryCompleteLoginWithTicketAsync()
    {
        if (string.IsNullOrEmpty(TicketId))
        {
            return null;
        }

        var ticket = ticketStore.GetTicket(TicketId);
        if (ticket is null)
        {
            logger.LogInformation("Tenant credential ticket {TicketId} missing or expired", TicketId);
            ModelState.AddModelError(string.Empty, "Your verification expired. Please enter your password again.");
            TicketId = null;
            return null;
        }

        if (string.IsNullOrEmpty(Email) || !string.Equals(ticket.EmailHash, HashEmail(Email), StringComparison.Ordinal))
        {
            logger.LogWarning("Ticket {TicketId} email hash mismatch", TicketId);
            ModelState.AddModelError(string.Empty, "We could not confirm your email for this session. Please sign in again.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            logger.LogWarning("Ticket {TicketId} used without active tenant context", TicketId);
            ModelState.AddModelError(string.Empty, "We could not determine your organization. Please start over.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        var verifiedUser = ticket.VerifiedUsers.FirstOrDefault(v => v.TenantId == tenant.TenantId);
        if (verifiedUser == null)
        {
            logger.LogWarning("Ticket {TicketId} does not include tenant {TenantId}", TicketId, tenant.TenantId);
            ModelState.AddModelError(string.Empty, "Please confirm your password again to access this organization.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        // Look up user by email in current tenant (ticket verified access to this tenant)
        var user = await users.FindByUsernameOrEmailAsync(Email!);
        if (user is null)
        {
            logger.LogWarning("Ticket {TicketId} verified but no user found for email in tenant {TenantId}", TicketId, tenant.TenantId);
            ModelState.AddModelError(string.Empty, "Your account could not be found. Please sign in again.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        Username = user.Username;
        var result = await CompleteSignInAsync(user);
        ticketStore.RemoveTicket(ticket.TicketId);
        TempData.Remove("TenantTicketId");
        return result;
    }

    private async Task<IActionResult> CompleteSignInAsync(User user)
    {
        // Check tenant MFA requirement
        var settings = await settingsService.GetCurrentTenantSettingsAsync();
        var mfaRequired = settings.Auth?.RequireMfa ?? false;

        // If MFA is required but user doesn't have it enabled, redirect to enrollment
        if (mfaRequired && !user.TotpEnabled)
        {
            var preauthClaims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(OidcConstants.Claims.Amr, "pwd"),
                new("mfa_enrollment_required", "true")
            };
            var preauthIdentity = new ClaimsIdentity(preauthClaims, "preauth");
            await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(preauthIdentity));
            ClearAttempts(HttpContext, user.Username);

            logger.LogInformation("⚠️ [Login] User {User} requires MFA enrollment (tenant policy). Redirecting to /Mfa", user.Username);
            var enrollUrl = Url.Page("/Mfa/Index", null, new { required = true, returnUrl = ReturnUrl }, protocol: Request.Scheme);
            return Redirect(enrollUrl ?? "/Mfa/Index?required=true");
        }

        // If TOTP enabled, issue short-lived preauth and redirect to TOTP page
        if (user.TotpEnabled)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(OidcConstants.Claims.Amr, "pwd")
            };
            var identity = new ClaimsIdentity(claims, "preauth");
            await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(identity));
            ClearAttempts(HttpContext, user.Username);
            var url = Url.Page("/LoginTotp", null, new { ReturnUrl }, protocol: Request.Scheme);
            return Redirect(url ?? "/LoginTotp");
        }

        var finalClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Amr, "pwd"),
            new(OidcConstants.Claims.Idp, "local")
        };

        var finalIdentity = new ClaimsIdentity(finalClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(finalIdentity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // Clear both local (IP-based) and global (account-based) lockouts on successful login
        ClearAttempts(HttpContext, user.Username);
        
        // Clear global lockout if user has an email (for global authentication)
        if (!string.IsNullOrEmpty(user.Email))
        {
            var userAccount = await globalAuthService.FindAccountByEmailAsync(user.Email);
            if (userAccount != null)
            {
                await globalAuthService.ClearFailedAttemptsAsync(userAccount.Id);
            }
        }
        logger.LogInformation("✅ [Login] User {User} signed in successfully", user.Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            logger.LogInformation("➡️ [Login] Redirecting to ReturnUrl: {ReturnUrl}", ReturnUrl);
            return Redirect(ReturnUrl);
        }

        // Build tenant-aware default redirect URL based on mode
        var currentTenant = tenantAccessor.CurrentTenant;
        string defaultUrl;

        if (multiTenancyOptions.Enabled && currentTenant != null)
        {
            defaultUrl = $"/t/{currentTenant.Slug}/";
            logger.LogInformation("➡️ [Login] Multi-tenant mode: redirecting to {DefaultUrl} (Tenant: {TenantSlug})",
                defaultUrl, currentTenant.Slug);
        }
        else
        {
            defaultUrl = "/";
            logger.LogInformation("➡️ [Login] Single-tenant mode: redirecting to {DefaultUrl}", defaultUrl);
        }

        return LocalRedirect(defaultUrl);
    }

    private static string HashEmail(string email) => string.IsNullOrEmpty(email) ? "empty" : MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(email.ToLowerInvariant())[..8];
}

