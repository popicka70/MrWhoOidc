using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Persistence.Extensions;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Auth;

[AllowAnonymous]
public class QrModel : PageModel
{
    private readonly IQrLoginService _qrService;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly AuthDbContext _db;
    private readonly IOptions<QrLoginOptions> _options;
    private readonly ILogger<QrModel> _logger;

    public QrModel(
        IQrLoginService qrService,
        IQrCodeGenerator qrCodeGenerator,
        AuthDbContext db,
        IOptions<QrLoginOptions> options,
        ILogger<QrModel> logger)
    {
        _qrService = qrService;
        _qrCodeGenerator = qrCodeGenerator;
        _db = db;
        _options = options;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    /// <summary>
    /// Session token for the QR login session.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Data URI for the QR code image.
    /// </summary>
    public string? QrCodeDataUri { get; set; }

    /// <summary>
    /// Poll interval in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Number the user must type on the phone to confirm (number matching, H9). Shown only to the initiating browser.
    /// </summary>
    public string? MatchCode { get; set; }

    /// <summary>
    /// Error message to display if QR login initialization fails.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Session already created by the authorize-flow handler, which redirected here with its token. The QR image is
        // always rebuilt from the session on the server; an image (or anything else) in the query string is ignored,
        // so this page cannot be made to show an attacker-chosen QR code (H9).
        var tokenFromQuery = Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(tokenFromQuery))
        {
            return await ShowExistingSessionAsync(tokenFromQuery);
        }

        // Otherwise, initialize a new QR session (standalone QR login from DiscoverTenant).
        // Only local return URLs: an absolute one made ConfirmAsync issue a code to an arbitrary redirect_uri (V5).
        if (!Url.IsLocalUrl(ReturnUrl))
        {
            ReturnUrl = "/";
        }

        var opts = _options.Value;

        if (!opts.Enabled)
        {
            _logger.LogWarning("QR login attempted but feature is disabled");
            ErrorMessage = "QR login is not currently available.";
            return Page();
        }

        try
        {
            // Use default client for platform-level QR login
            var clientId = await _db.ResolveDefaultClientIdAsync();
            if (string.IsNullOrEmpty(clientId))
            {
                _logger.LogError("No default client found for platform QR login");
                ErrorMessage = "QR login is not properly configured.";
                return Page();
            }

            // Generate PKCE
            var (verifier, challenge) = GeneratePkce();
            HttpContext.Session.SetString("pkce_verifier", verifier);

            // Create QR session, bound to this browser (H9)
            var created = await _qrService.CreateSessionAsync(
                clientId,
                ReturnUrl,
                challenge,
                "S256",
                string.Empty, // state
                null, // nonce
                "openid profile email",
                QrInitiatorBinding.DescribeInitiator(HttpContext));
            var sessionToken = created.SessionToken;
            QrInitiatorBinding.Issue(HttpContext, sessionToken, created.InitiatorSecret, created.ExpiresAt);

            SessionToken = sessionToken;
            QrCodeDataUri = _qrCodeGenerator.GenerateQrCodeDataUri(created.MobileUrl);
            MatchCode = created.MatchCode;
            PollIntervalSeconds = opts.PollIntervalSeconds;

            _logger.LogInformation("QR session created from Qr page for standalone login, session={SessionHash}",
                ComputeHash(sessionToken));

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create QR session from Qr page");
            ErrorMessage = "Failed to initialize QR login. Please try again.";
            return Page();
        }
    }

    private async Task<IActionResult> ShowExistingSessionAsync(string sessionToken)
    {
        if (!_options.Value.Enabled)
        {
            ErrorMessage = "QR login is not currently available.";
            return Page();
        }

        var session = await _qrService.GetSessionAsync(sessionToken);

        // Only the browser that started the login sees its QR code and match number; someone who was merely sent the
        // session token (it is in the QR code) gets nothing useful here.
        if (session is null || !QrInitiatorBinding.IsBound(HttpContext, session))
        {
            _logger.LogWarning("QR page refused: session not found or not started in this browser");
            ErrorMessage = "This QR login was not started in this browser. Please start the login again.";
            return Page();
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow ||
            session.Status is not (QrSessionStatus.Pending or QrSessionStatus.Scanned))
        {
            ErrorMessage = "This QR code has expired or was already used. Please start the login again.";
            return Page();
        }

        SessionToken = session.SessionToken;
        QrCodeDataUri = _qrCodeGenerator.GenerateQrCodeDataUri(_qrService.BuildMobileUrl(session.SessionToken));
        MatchCode = session.MatchCode;
        PollIntervalSeconds = _options.Value.PollIntervalSeconds;
        return Page();
    }

    private static (string Verifier, string Challenge) GeneratePkce() { var verifier = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); return (verifier, MrWhoOidc.Auth.Utils.CryptoHelper.ComputePkceS256(verifier)); }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ComputeHash(string input) => MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input)[..8];
}

