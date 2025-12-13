using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Auth;

[AllowAnonymous]
public class QrConfirmModel : PageModel
{
    private readonly IQrLoginService _qrService;
    private readonly AuthDbContext _db;
    private readonly ILogger<QrConfirmModel> _logger;

    public QrConfirmModel(IQrLoginService qrService, AuthDbContext db, ILogger<QrConfirmModel> logger)
    {
        _qrService = qrService;
        _db = db;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string? Session { get; set; }

    public string? ClientName { get; set; }
    public string? Timestamp { get; set; }
    public string? SessionToken { get; set; }

    public async Task<IActionResult> OnGet()
    {
        _logger.LogInformation("🔍 [QR Confirm Page] Request from {IP}, Path: {Path}",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Request.Path);
        _logger.LogInformation("🔍 [QR Confirm Page] Session parameter: {HasSession}, Length: {Length}, IsAuthenticated: {IsAuth}",
            !string.IsNullOrEmpty(Session),
            Session?.Length ?? 0,
            User.Identity?.IsAuthenticated ?? false);

        if (string.IsNullOrEmpty(Session))
        {
            _logger.LogWarning("❌ [QR Confirm Page] REJECTED: missing session parameter");
            return BadRequest("Missing session parameter");
        }

        _logger.LogDebug("🔍 [QR Confirm Page] Looking up session");
        var session = await _qrService.GetSessionAsync(Session);

        if (session is null)
        {
            _logger.LogWarning("❌ [QR Confirm Page] Session not found");
            return NotFound("QR session not found");
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("❌ [QR Confirm Page] Session expired for client {ClientId}", session.ClientId);
            return BadRequest("This QR code has expired.");
        }

        _logger.LogInformation("✅ [QR Confirm Page] Session valid for client {ClientId}, status: {Status}",
            session.ClientId, session.Status);

        // Get client info
        var client = await _db.Clients
            .Where(c => c.ClientId == session.ClientId)
            .Select(c => new { c.ClientName, c.ClientId })
            .FirstOrDefaultAsync();

        if (client is null)
        {
            _logger.LogWarning("❌ [QR Confirm Page] Client not found: {ClientId}", session.ClientId);
            return BadRequest("Invalid client");
        }

        _logger.LogInformation("✅ [QR Confirm Page] Rendering confirm page for client {ClientName} ({ClientId})",
            client.ClientName ?? client.ClientId, client.ClientId);

        // Set model properties for the view
        SessionToken = Session;
        ClientName = client.ClientName ?? client.ClientId;
        Timestamp = session.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

        return Page();
    }
}
