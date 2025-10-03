using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
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

    public QrLoginHandler(
        IQrLoginService qrService,
        IQrCodeGenerator qrCodeGenerator,
        IAuthorizationCodeService authCodeService,
        AuthDbContext db,
        ILogger<QrLoginHandler> logger,
        IAuditSink audit,
        IOptions<QrLoginOptions> options)
    {
        _qrService = qrService;
        _qrCodeGenerator = qrCodeGenerator;
        _authCodeService = authCodeService;
        _db = db;
        _logger = logger;
        _audit = audit;
        _options = options;
    }

    public async Task<IResult> InitiateAsync(HttpContext http)
    {
        // This method is for direct QR initiation without validation
        // Extract parameters from query string for backward compatibility
        _logger.LogWarning("⚠️ PARAMETERLESS InitiateAsync(HttpContext) called - this should NOT be called from AuthorizeHandler!");
        var opts = _options.Value;
        _logger.LogInformation("QR login initiate called from {IP}, Path: {Path}, QueryString: {QueryString}",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            http.Request.Path,
            http.Request.QueryString.Value ?? "(empty)");

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

        _logger.LogDebug("QR login parameters: clientId={ClientId}, returnUrl={ReturnUrl}, state={State}, nonce={Nonce}, scope={Scope}, codeChallenge={CodeChallenge}, codeChallengeMethod={CodeChallengeMethod}",
            clientIdStr ?? "(null)",
            returnUrlStr ?? "(null)",
            stateStr ?? "(null)",
            nonceStr ?? "(null)",
            scopeStr ?? "(null)",
            !string.IsNullOrEmpty(codeChallengeStr) ? "***" : "(null)",
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
            codeChallengeMethodStr = "S256";
        }

        if (string.IsNullOrEmpty(scopeStr))
        {
            _logger.LogDebug("Using default scope: openid");
            scopeStr = "openid";
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

        _logger.LogDebug("QR login from validated request: clientId={ClientId}, redirectUri={RedirectUri}, scope={Scope}, nonce={Nonce}",
            validationResult.ClientId,
            validationResult.RedirectUri,
            string.Join(" ", validationResult.Scopes ?? Array.Empty<string>()),
            validationResult.Nonce ?? "(null)");

        var scope = string.Join(" ", validationResult.Scopes ?? new[] { "openid" });
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

            _audit.Emit("qr.session.created", new { 
                client_id = clientId, 
                session_token_hash = ComputeHash(sessionToken),
                ip = http.Connection.RemoteIpAddress?.ToString(),
                expiry = opts.SessionLifetimeSeconds
            });

            // Pass data via query parameters to the Razor page
            var qrPageUrl = $"/Auth/Qr?token={Uri.EscapeDataString(sessionToken)}&qr={Uri.EscapeDataString(qrCodeDataUri)}&interval={opts.PollIntervalSeconds}";
            
            _logger.LogDebug("Redirecting to /Auth/Qr Razor page with QR data in query");
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

        var response = new
        {
            status = session.Status.ToString().ToLowerInvariant(),
            redirectUrl = session.Status == QrSessionStatus.Authenticated && !string.IsNullOrEmpty(session.AuthorizationCode)
                ? BuildCallbackUrl(session)
                : null,
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
        if (!user.Identity?.IsAuthenticated ?? true)
        {
            _logger.LogWarning("QR confirm rejected: user not authenticated");
            return Results.Unauthorized();
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            _logger.LogWarning("QR confirm rejected: invalid user ID claim");
            return Results.Unauthorized();
        }

        try
        {
            _logger.LogDebug("Generating authorization code for QR session, client {ClientId}, user {UserId}", session.ClientId, userId);
            
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

            // Generate authorization code
            var result = await _authCodeService.IssueAsync(validationResult, userId);
            
            if (!result.ok || string.IsNullOrEmpty(result.code))
            {
                _logger.LogError("Failed to generate authorization code for QR session: ok={Ok}, error={Error}", result.ok, result.error);
                return Results.Json(new { success = false, error = "Failed to generate code" }, statusCode: 500);
            }

            _logger.LogDebug("Authorization code generated successfully for QR session");
            
            // Update session
            await _qrService.UpdateStatusAsync(sessionToken, QrSessionStatus.Authenticated, userId, result.code);

            _logger.LogInformation("QR login confirmed: user {UserId} for client {ClientId}", 
                userId, session.ClientId);

            _audit.Emit("qr.confirm", new { 
                user_id = userId, 
                client_id = session.ClientId,
                session_token_hash = ComputeHash(sessionToken),
                success = true
            });

            return Results.Json(new { 
                success = true, 
                message = "Authentication confirmed. You may close this page." 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming QR login for client {ClientId}: {Message}", session?.ClientId ?? "unknown", ex.Message);
            return Results.Problem("Failed to confirm authentication");
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
            
            _audit.Emit("qr.cancel", new { 
                session_token_hash = ComputeHash(sessionToken),
                source = "desktop"
            });
        }

        return Results.Json(new { success = true });
    }

    public async Task<IResult> MobileLandingAsync(HttpContext http)
    {
        var sessionToken = http.Request.Query["session"].ToString();
        _logger.LogInformation("QR mobile landing from {IP}, session={HasSession}",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            !string.IsNullOrEmpty(sessionToken));

        if (string.IsNullOrEmpty(sessionToken))
        {
            _logger.LogWarning("QR mobile landing rejected: missing session parameter");
            return Results.BadRequest("Missing session parameter");
        }

        _logger.LogDebug("Looking up QR session with hash {Hash}", ComputeHash(sessionToken));
        var session = await _qrService.GetSessionAsync(sessionToken);
        
        if (session is null)
        {
            _logger.LogWarning("QR mobile landing: session not found for hash {Hash}", ComputeHash(sessionToken));
            return Results.NotFound("QR session not found");
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("QR mobile landing: session expired for client {ClientId}", session.ClientId);
            return Results.BadRequest("This QR code has expired. Please scan a new code.");
        }

        if (session.Status == QrSessionStatus.Consumed)
        {
            _logger.LogWarning("QR mobile landing: session already consumed for client {ClientId}", session.ClientId);
            return Results.BadRequest("This QR code has already been used.");
        }

        if (session.Status == QrSessionStatus.Cancelled)
        {
            _logger.LogWarning("QR mobile landing: session cancelled for client {ClientId}", session.ClientId);
            return Results.BadRequest("Login cancelled. You may close this page.");
        }

        // Mark as scanned
        var mobileIp = http.Connection.RemoteIpAddress?.ToString();
        var mobileUserAgent = http.Request.Headers.UserAgent.ToString();
        _logger.LogDebug("Marking QR session as scanned for client {ClientId}", session.ClientId);
        await _qrService.MarkScannedAsync(sessionToken, mobileIp, mobileUserAgent);

        _audit.Emit("qr.session.scanned", new { 
            session_token_hash = ComputeHash(sessionToken),
            mobile_ip = mobileIp,
            mobile_user_agent = mobileUserAgent
        });

        // Check if user is authenticated
        if (!http.User.Identity?.IsAuthenticated ?? true)
        {
            // Redirect to login with return URL
            var returnUrl = $"/Auth/QrConfirm?session={Uri.EscapeDataString(sessionToken)}";
            return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // User is authenticated, redirect to confirmation page
        return Results.Redirect($"/Auth/QrConfirm?session={Uri.EscapeDataString(sessionToken)}");
    }

    public async Task<IResult> ConfirmPageAsync(HttpContext http)
    {
        var sessionToken = http.Request.Query["session"].ToString();

        if (string.IsNullOrEmpty(sessionToken))
        {
            return Results.BadRequest("Missing session parameter");
        }

        var session = await _qrService.GetSessionAsync(sessionToken);
        
        if (session is null)
        {
            return Results.NotFound("QR session not found");
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Results.BadRequest("This QR code has expired.");
        }

        // Get client info
        var client = await _db.Clients
            .Where(c => c.ClientId == session.ClientId)
            .Select(c => new { c.ClientName, c.ClientId })
            .FirstOrDefaultAsync();

        if (client is null)
        {
            return Results.BadRequest("Invalid client");
        }

        http.Items["SessionToken"] = sessionToken;
        http.Items["ClientName"] = client.ClientName ?? client.ClientId;
        http.Items["Timestamp"] = session.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

        return Results.Redirect("/Auth/QrConfirm");
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
