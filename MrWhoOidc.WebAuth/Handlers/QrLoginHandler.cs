using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IQrLoginHandler
{
    Task<IResult> InitiateAsync(HttpContext http);
    Task<IResult> InitiateAsync(HttpContext http, AuthorizeValidationResult validationResult, AuthorizeRequest request);
    Task<IResult> GetStatusAsync(HttpContext http, string sessionToken);
    Task<IResult> ConfirmAsync(HttpContext http);
    Task<IResult> CancelAsync(HttpContext http);
    Task<IResult> MobileLandingAsync(HttpContext http);
    Task<IResult> ConfirmPageAsync(HttpContext http);
    Task<IResult> CompleteAsync(HttpContext http, string sessionToken);
}

public sealed class QrLoginHandler : IQrLoginHandler
{
    private readonly IQrLoginService _qrService;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly IAuthorizationCodeService _authCodeService;
    private readonly AuthDbContext _db;
    private readonly ILogger<QrLoginHandler> _logger;
    private readonly IAuditSink _audit;
    private readonly IOptions<QrLoginOptions> _options;
    private readonly ILoginContinuationStore _continuationStore;

    public QrLoginHandler(
        IQrLoginService qrService,
        IQrCodeGenerator qrCodeGenerator,
        IAuthorizationCodeService authCodeService,
        AuthDbContext db,
        ILogger<QrLoginHandler> logger,
        IAuditSink audit,
        IOptions<QrLoginOptions> options,
        ILoginContinuationStore continuationStore)
    {
        _qrService = qrService;
        _qrCodeGenerator = qrCodeGenerator;
        _authCodeService = authCodeService;
        _db = db;
        _logger = logger;
        _audit = audit;
        _options = options;
        _continuationStore = continuationStore;
    }

    public async Task<IResult> InitiateAsync(HttpContext http)
    {
        // This method is for direct QR initiation without validation
        // Extract parameters from query string for backward compatibility
        _logger.LogWarning("⚠️ PARAMETERLESS InitiateAsync(HttpContext) called - this should NOT be called from AuthorizeHandler!");
        var opts = _options.Value;
        _logger.LogInformation("QR login initiate called from {IP}, Path: {Path}",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            http.Request.Path);

        if (!opts.Enabled)
        {
            _logger.LogWarning("QR login attempt rejected: feature is disabled");
            return Results.BadRequest("QR login is not enabled");
        }

        // Extract parameters
        var clientIdStr = http.Request.Query["client_id"].ToString();
        var returnUrlStr = http.Request.Query["returnUrl"].ToString();
        var stateStr = http.Request.Query["state"].ToString();
        var nonceStr = http.Request.Query["nonce"].ToString();
        var scopeStr = http.Request.Query["scope"].ToString();
        var codeChallengeStr = http.Request.Query["code_challenge"].ToString();
        var codeChallengeMethodStr = http.Request.Query["code_challenge_method"].ToString();

        _logger.LogDebug("QR login parameters present: hasClientId={HasClientId}, hasReturnUrl={HasReturnUrl}, hasState={HasState}, hasNonce={HasNonce}, scopeLen={ScopeLen}, hasCodeChallenge={HasCodeChallenge}, codeChallengeMethod={CodeChallengeMethod}",
            !string.IsNullOrEmpty(clientIdStr),
            !string.IsNullOrEmpty(returnUrlStr),
            !string.IsNullOrEmpty(stateStr),
            !string.IsNullOrEmpty(nonceStr),
            scopeStr?.Length ?? 0,
            !string.IsNullOrEmpty(codeChallengeStr),
            codeChallengeMethodStr ?? "(null)");

        if (string.IsNullOrEmpty(clientIdStr) || string.IsNullOrEmpty(returnUrlStr))
        {
            _logger.LogWarning("QR login rejected: missing required parameters. clientId={HasClientId}, returnUrl={HasReturnUrl}",
                !string.IsNullOrEmpty(clientIdStr),
                !string.IsNullOrEmpty(returnUrlStr));
            return Results.BadRequest("Missing required parameters");
        }

        // Default values
        if (string.IsNullOrEmpty(codeChallengeMethodStr))
        {
            _logger.LogDebug("Using default code challenge method: S256");
            codeChallengeMethodStr = OAuthConstants.CodeChallengeMethods.S256;
        }

        if (string.IsNullOrEmpty(scopeStr))
        {
            _logger.LogDebug("Using default scope: {Scope}", OidcConstants.Scopes.OpenId);
            scopeStr = OidcConstants.Scopes.OpenId;
        }

        // Generate PKCE if not present
        if (string.IsNullOrEmpty(codeChallengeStr))
        {
            _logger.LogDebug("Generating PKCE challenge (not provided by client)");
            var (verifier, challenge) = GeneratePkce();
            codeChallengeStr = challenge;
            // Store verifier in session for desktop to use later
            http.Session.SetString("pkce_verifier", verifier);
        }

        return await InitiateCoreAsync(http, clientIdStr, returnUrlStr, stateStr ?? string.Empty, nonceStr, scopeStr, codeChallengeStr, codeChallengeMethodStr);
    }

    public async Task<IResult> InitiateAsync(HttpContext http, AuthorizeValidationResult validationResult, AuthorizeRequest request)
    {
        // This method is for QR initiation from authorize flow (with validated request)
        _logger.LogInformation("✅ 3-PARAMETER InitiateAsync(HttpContext, AuthorizeValidationResult, AuthorizeRequest) called - CORRECT!");
        var opts = _options.Value;
        _logger.LogInformation("QR login initiate from authorize flow from {IP}, client={ClientId}",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            validationResult.ClientId);

        if (!opts.Enabled)
        {
            _logger.LogWarning("QR login attempt rejected: feature is disabled");
            return Results.BadRequest("QR login is not enabled");
        }

        _logger.LogDebug("QR login from validated request: clientId={ClientId}, hasRedirectUri={HasRedirectUri}, scopeCount={ScopeCount}, hasNonce={HasNonce}",
            validationResult.ClientId,
            !string.IsNullOrEmpty(validationResult.RedirectUri),
            validationResult.Scopes?.Length ?? 0,
            !string.IsNullOrEmpty(validationResult.Nonce));

        var scope = string.Join(" ", validationResult.Scopes ?? new[] { OidcConstants.Scopes.OpenId });
        var state = request.state ?? string.Empty;

        return await InitiateCoreAsync(
            http,
            validationResult.ClientId!,
            validationResult.RedirectUri!,
            state,
            validationResult.Nonce,
            scope,
            validationResult.CodeChallenge!,
            validationResult.CodeChallengeMethod ?? "S256");
    }

    private async Task<IResult> InitiateCoreAsync(
        HttpContext http,
        string clientId,
        string returnUrl,
        string state,
        string? nonce,
        string scope,
        string codeChallenge,
        string codeChallengeMethod)
    {
        var opts = _options.Value;

        _logger.LogDebug("QR session creation: clientId={ClientId}, scope={Scope}, challenge={HasChallenge}",
            clientId,
            scope,
            !string.IsNullOrEmpty(codeChallenge));

        try
        {
            _logger.LogDebug("Creating QR session for client {ClientId} with scope {Scope}", clientId, scope);

            var (sessionToken, authUrl) = await _qrService.CreateSessionAsync(
                clientId, returnUrl, codeChallenge, codeChallengeMethod,
                state ?? string.Empty, nonce, scope);

            var qrCodeDataUri = _qrCodeGenerator.GenerateQrCodeDataUri(authUrl);

            _logger.LogInformation("QR login session created: {SessionToken} for client {ClientId}",
                sessionToken, clientId);

            _audit.Emit("qr.session.created", new
            {
                client_id = clientId,
                session_token_hash = ComputeHash(sessionToken),
                ip = http.Connection.RemoteIpAddress?.ToString(),
                expiry = opts.SessionLifetimeSeconds
            });

            // Pass data via query parameters to the Razor page
            var qrPageUrl = $"/auth/qr?token={Uri.EscapeDataString(sessionToken)}&qr={Uri.EscapeDataString(qrCodeDataUri)}&interval={opts.PollIntervalSeconds}";

            _logger.LogDebug("Redirecting to /auth/qr Razor page with QR data in query");
            return Results.Redirect(qrPageUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating QR login session for client {ClientId}: {Message}",
                clientId, ex.Message);
            return Results.Problem("Failed to create QR login session");
        }
    }

    public async Task<IResult> GetStatusAsync(HttpContext http, string sessionToken)
    {
        _logger.LogDebug("QR status check for session {SessionTokenHash}", ComputeHash(sessionToken));

        var session = await _qrService.GetSessionAsync(sessionToken);

        if (session is null)
        {
            _logger.LogWarning("QR status check: session not found for token hash {Hash}", ComputeHash(sessionToken));
            return Results.Json(new { status = "not_found" }, statusCode: 404);
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogDebug("QR session expired: {SessionTokenHash}", ComputeHash(sessionToken));
            await _qrService.ExpireSessionAsync(sessionToken);
            return Results.Json(new { status = "expired" });
        }

        _logger.LogDebug("QR session status: {Status} for client {ClientId}", session.Status, session.ClientId);

        // Determine redirect URL based on login type
        string? redirectUrl = null;
        if (session.Status == QrSessionStatus.Authenticated)
        {
            var isPlatformQrLogin = !session.ReturnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (isPlatformQrLogin)
            {
                // Platform QR login - redirect to return URL with user ID for session creation
                redirectUrl = $"/auth/qr-complete?session={Uri.EscapeDataString(sessionToken)}";
            }
            else if (!string.IsNullOrEmpty(session.AuthorizationCode))
            {
                // OAuth QR login - redirect to callback with auth code
                redirectUrl = BuildCallbackUrl(session);
            }
        }

        var response = new
        {
            status = session.Status.ToString().ToLowerInvariant(),
            redirectUrl,
            message = session.Status switch
            {
                QrSessionStatus.Pending => "Waiting for mobile device to scan",
                QrSessionStatus.Scanned => "QR code scanned. Complete authentication on your mobile device.",
                QrSessionStatus.Authenticated => "Authentication successful! Redirecting...",
                QrSessionStatus.Expired => "QR code expired. Please refresh the page.",
                QrSessionStatus.Cancelled => "Login cancelled.",
                _ => null
            }
        };

        return Results.Json(response);
    }

    public async Task<IResult> ConfirmAsync(HttpContext http)
    {
        _logger.LogInformation("QR confirm called from {IP}", http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        var form = await http.Request.ReadFormAsync();
        var sessionToken = form["sessionToken"].ToString();

        if (string.IsNullOrEmpty(sessionToken))
        {
            _logger.LogWarning("QR confirm rejected: missing session token");
            return Results.BadRequest(new { success = false, message = "Missing session token" });
        }

        _logger.LogDebug("QR confirm for session hash {Hash}", ComputeHash(sessionToken));
        var session = await _qrService.GetSessionAsync(sessionToken);

        if (session is null)
        {
            _logger.LogWarning("QR confirm rejected: session not found for hash {Hash}", ComputeHash(sessionToken));
            return Results.NotFound(new { success = false, message = "Session not found" });
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("QR confirm rejected: session expired for client {ClientId}", session.ClientId);
            return Results.BadRequest(new { success = false, message = "QR code has expired" });
        }

        if (session.Status != QrSessionStatus.Scanned && session.Status != QrSessionStatus.Pending)
        {
            _logger.LogWarning("QR confirm rejected: invalid status {Status} for client {ClientId}", session.Status, session.ClientId);
            return Results.BadRequest(new { success = false, message = "Invalid session status" });
        }

        // Get authenticated user from mobile session
        var user = http.User;
        _logger.LogInformation("QR confirm: checking authentication, IsAuthenticated={IsAuth}, AuthType={AuthType}, Name={Name}",
            user.Identity?.IsAuthenticated,
            user.Identity?.AuthenticationType,
            user.Identity?.Name);

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            _logger.LogWarning("QR confirm rejected: user not authenticated");
            return Results.Json(new { success = false, message = "You must be logged in to confirm this login request" }, statusCode: 401);
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _logger.LogInformation("QR confirm: NameIdentifier claim={Claim}", userIdClaim ?? "(null)");
        
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            _logger.LogWarning("QR confirm rejected: invalid user ID claim, got {Claim}", userIdClaim);
            return Results.Json(new { success = false, message = "Invalid user session" }, statusCode: 401);
        }

        try
        {
            // Check if this is a platform QR login (relative URL) or OAuth QR login (absolute URL)
            var isPlatformQrLogin = !session.ReturnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("QR confirm: isPlatformQrLogin={IsPlatform}, ReturnUrl={ReturnUrl}", isPlatformQrLogin, session.ReturnUrl);

            if (isPlatformQrLogin)
            {
                // Platform QR login - just mark as authenticated, no auth code needed
                // The desktop browser will sign in the user directly via cookie
                _logger.LogInformation("QR confirm: platform QR login for user {UserId}", userId);
                
                await _qrService.UpdateStatusAsync(sessionToken, QrSessionStatus.Authenticated, userId, authCode: null);

                _logger.LogInformation("QR platform login confirmed: user {UserId}", userId);

                _audit.Emit("qr.confirm", new
                {
                    user_id = userId,
                    client_id = session.ClientId,
                    session_token_hash = ComputeHash(sessionToken),
                    platform_login = true,
                    success = true
                });

                return Results.Json(new
                {
                    success = true,
                    message = "Authentication confirmed. You may close this page."
                });
            }

            // OAuth QR login - generate authorization code
            _logger.LogInformation("QR confirm: generating auth code for client {ClientId}, user {UserId}", session.ClientId, userId);

            // Build validation result for code generation
            var validationResult = new AuthorizeValidationResult
            {
                IsValid = true,
                ClientId = session.ClientId,
                RedirectUri = session.ReturnUrl,
                Scopes = session.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Nonce = session.Nonce,
                CodeChallenge = session.CodeChallenge,
                CodeChallengeMethod = session.CodeChallengeMethod
            };

            _logger.LogInformation("QR confirm: validation result - RedirectUri={RedirectUri}, Scopes={Scopes}, HasChallenge={HasChallenge}",
                validationResult.RedirectUri,
                string.Join(",", validationResult.Scopes),
                !string.IsNullOrEmpty(validationResult.CodeChallenge));

            // Generate authorization code
            _logger.LogInformation("QR confirm: calling IssueAsync...");
            var result = await _authCodeService.IssueAsync(validationResult, userId);
            _logger.LogInformation("QR confirm: IssueAsync returned ok={Ok}, hasCode={HasCode}, error={Error}",
                result.ok, !string.IsNullOrEmpty(result.code), result.error ?? "(none)");

            if (!result.ok || string.IsNullOrEmpty(result.code))
            {
                _logger.LogError("Failed to generate authorization code for QR session: ok={Ok}, error={Error}", result.ok, result.error);
                return Results.Json(new { success = false, message = result.error ?? "Failed to generate code" }, statusCode: 500);
            }

            _logger.LogInformation("QR confirm: auth code generated, updating session status...");

            // Update session
            await _qrService.UpdateStatusAsync(sessionToken, QrSessionStatus.Authenticated, userId, result.code);

            _logger.LogInformation("QR login confirmed: user {UserId} for client {ClientId}",
                userId, session.ClientId);

            _audit.Emit("qr.confirm", new
            {
                user_id = userId,
                client_id = session.ClientId,
                session_token_hash = ComputeHash(sessionToken),
                success = true
            });

            return Results.Json(new
            {
                success = true,
                message = "Authentication confirmed. You may close this page."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming QR login for client {ClientId}: {Message}", session?.ClientId ?? "unknown", ex.Message);
            return Results.Json(new { success = false, message = $"Server error: {ex.Message}" }, statusCode: 500);
        }
    }

    public async Task<IResult> CancelAsync(HttpContext http)
    {
        _logger.LogInformation("QR cancel called from {IP}", http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        var form = await http.Request.ReadFormAsync();
        var sessionToken = form["sessionToken"].ToString();

        if (string.IsNullOrEmpty(sessionToken))
        {
            _logger.LogWarning("QR cancel rejected: missing session token");
            return Results.BadRequest(new { success = false, message = "Missing session token" });
        }

        _logger.LogDebug("Looking up QR session to cancel, hash {Hash}", ComputeHash(sessionToken));
        var session = await _qrService.GetSessionAsync(sessionToken);
        if (session is not null)
        {
            _logger.LogInformation("Cancelling QR session for client {ClientId}", session.ClientId);
            await _qrService.UpdateStatusAsync(sessionToken, QrSessionStatus.Cancelled);

            _audit.Emit("qr.cancel", new
            {
                session_token_hash = ComputeHash(sessionToken),
                source = "desktop"
            });
        }

        return Results.Json(new { success = true });
    }

    public async Task<IResult> MobileLandingAsync(HttpContext http)
    {
        var sessionToken = http.Request.Query["session"].ToString();

        _logger.LogInformation("🔍 [QR Mobile Landing] Request from {IP}, Path: {Path}",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            http.Request.Path);
        _logger.LogInformation("🔍 [QR Mobile Landing] Session token present: {HasSession}, Length: {Length}",
            !string.IsNullOrEmpty(sessionToken),
            sessionToken?.Length ?? 0);

        if (string.IsNullOrEmpty(sessionToken))
        {
            _logger.LogWarning("❌ [QR Mobile Landing] REJECTED: missing session parameter");
            return Results.BadRequest("Missing session parameter");
        }

        _logger.LogDebug("🔍 [QR Mobile Landing] Looking up QR session with hash {Hash}", ComputeHash(sessionToken));
        var session = await _qrService.GetSessionAsync(sessionToken);

        if (session is null)
        {
            _logger.LogWarning("❌ [QR Mobile Landing] Session not found for hash {Hash}", ComputeHash(sessionToken));
            return Results.NotFound("QR session not found");
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("❌ [QR Mobile Landing] Session expired for client {ClientId}", session.ClientId);
            return Results.BadRequest("This QR code has expired. Please scan a new code.");
        }

        if (session.Status == QrSessionStatus.Consumed)
        {
            _logger.LogWarning("❌ [QR Mobile Landing] Session already consumed for client {ClientId}", session.ClientId);
            return Results.BadRequest("This QR code has already been used.");
        }

        if (session.Status == QrSessionStatus.Cancelled)
        {
            _logger.LogWarning("❌ [QR Mobile Landing] Session cancelled for client {ClientId}", session.ClientId);
            return Results.BadRequest("Login cancelled. You may close this page.");
        }

        // Mark as scanned
        var mobileIp = http.Connection.RemoteIpAddress?.ToString();
        var mobileUserAgent = http.Request.Headers.UserAgent.ToString();
        _logger.LogInformation("✅ [QR Mobile Landing] Marking session as scanned for client {ClientId}", session.ClientId);
        await _qrService.MarkScannedAsync(sessionToken, mobileIp, mobileUserAgent);

        _audit.Emit("qr.session.scanned", new
        {
            session_token_hash = ComputeHash(sessionToken),
            mobile_ip = mobileIp,
            mobile_user_agent = mobileUserAgent
        });

        // Check if user is authenticated
        var isAuthenticated = http.User.Identity?.IsAuthenticated ?? false;
        _logger.LogInformation("🔍 [QR Mobile Landing] User authenticated: {IsAuthenticated}", isAuthenticated);

        if (!isAuthenticated)
        {
            // Redirect to login with return URL
            var returnUrl = $"/auth/qr-confirm?session={Uri.EscapeDataString(sessionToken)}";
            var ctx = await _continuationStore.StoreAsync(returnUrl, http.RequestAborted);
            var loginUrl = $"/login?ctx={Uri.EscapeDataString(ctx)}";
            _logger.LogInformation("➡️ [QR Mobile Landing] Redirecting to /login for QR flow");
            return Results.Redirect(loginUrl);
        }

        // User is authenticated, redirect to confirmation page
        var confirmUrl = $"/auth/qr-confirm?session={Uri.EscapeDataString(sessionToken)}";
        _logger.LogInformation("➡️ [QR Mobile Landing] User already authenticated, redirecting to /auth/qr-confirm");
        return Results.Redirect(confirmUrl);
    }

    /// <summary>
    /// Completes a platform QR login by signing in the user on the desktop browser.
    /// This is called when the mobile user has confirmed their identity.
    /// </summary>
    public async Task<IResult> CompleteAsync(HttpContext http, string sessionToken)
    {
        _logger.LogInformation("QR complete called for session {SessionHash}", ComputeHash(sessionToken));

        var session = await _qrService.GetSessionAsync(sessionToken);
        if (session is null)
        {
            _logger.LogWarning("QR complete: session not found");
            return Results.Redirect("/DiscoverTenant?error=session_not_found");
        }

        if (session.Status != QrSessionStatus.Authenticated)
        {
            _logger.LogWarning("QR complete: session not authenticated, status={Status}", session.Status);
            return Results.Redirect("/DiscoverTenant?error=not_authenticated");
        }

        if (session.UserId is null)
        {
            _logger.LogWarning("QR complete: session has no user ID");
            return Results.Redirect("/DiscoverTenant?error=no_user");
        }

        // Get the user
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId.Value);
        if (user is null)
        {
            _logger.LogWarning("QR complete: user {UserId} not found", session.UserId);
            return Results.Redirect("/DiscoverTenant?error=user_not_found");
        }

        // Mark session as consumed
        await _qrService.UpdateStatusAsync(sessionToken, QrSessionStatus.Consumed, session.UserId, session.AuthorizationCode);

        // Sign in the user on the desktop browser
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("tenant_id", user.TenantId.ToString())
        };

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new(ClaimTypes.Email, user.Email));
        }

        var identity = new ClaimsIdentity(claims, "QrLogin");
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal);

        _logger.LogInformation("QR login complete: user {UserId} signed in on desktop", user.Id);

        _audit.Emit("qr.complete", new
        {
            user_id = user.Id,
            username = user.Username,
            session_token_hash = ComputeHash(sessionToken)
        });

        // Redirect to the original return URL
        var returnUrl = session.ReturnUrl;
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith("/"))
        {
            returnUrl = "/";
        }

        return Results.Redirect(returnUrl);
    }

    /// <summary>
    /// NOTE: This method is no longer used - the Razor Page at /auth/qr-confirm handles requests directly.
    /// It's kept here for interface compatibility but should not be called.
    /// </summary>
    public Task<IResult> ConfirmPageAsync(HttpContext http)
    {
        _logger.LogWarning("⚠️ ConfirmPageAsync called but is deprecated - Razor Page should handle /auth/qr-confirm directly");
        return Task.FromResult(Results.Redirect("/auth/qr-confirm") as IResult);
    }

    private string BuildCallbackUrl(QrLoginSession session)
    {
        var returnUrl = session.ReturnUrl;
        var separator = returnUrl.Contains('?') ? "&" : "?";

        var url = $"{returnUrl}{separator}code={Uri.EscapeDataString(session.AuthorizationCode!)}";

        if (!string.IsNullOrEmpty(session.State))
        {
            url += $"&state={Uri.EscapeDataString(session.State)}";
        }

        return url;
    }

    private static (string verifier, string challenge) GeneratePkce()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var verifier = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var challengeBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (verifier, challenge);
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
