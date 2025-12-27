using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Persistence.Extensions;
using MrWhoOidc.Auth.Services;
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
    /// Error message to display if QR login initialization fails.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Check if session token is already provided (redirect from handler)
        var tokenFromQuery = Request.Query["token"].ToString();
        var qrFromQuery = Request.Query["qr"].ToString();
        var intervalFromQuery = Request.Query["interval"].ToString();

        if (!string.IsNullOrEmpty(tokenFromQuery) && !string.IsNullOrEmpty(qrFromQuery))
        {
            // Session already created by handler, use provided values
            SessionToken = tokenFromQuery;
            QrCodeDataUri = qrFromQuery;
            PollIntervalSeconds = int.TryParse(intervalFromQuery, out var interval) ? interval : 2;
            return Page();
        }

        // Otherwise, initialize a new QR session (standalone QR login from DiscoverTenant)
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

            // Create QR session
            var (sessionToken, authUrl) = await _qrService.CreateSessionAsync(
                clientId,
                ReturnUrl,
                challenge,
                "S256",
                string.Empty, // state
                null, // nonce
                "openid profile email");

            SessionToken = sessionToken;
            QrCodeDataUri = _qrCodeGenerator.GenerateQrCodeDataUri(authUrl);
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

