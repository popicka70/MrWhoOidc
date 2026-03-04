using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class SessionsModel(AuthDbContext db, IUserAgentParser uaParser) : PageModel
{
    public List<SessionViewModel> Sessions { get; private set; } = new();
    public string? Message { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return;

        var currentSessionJti = GetCurrentSessionJti();
        var currentUserAgent = Request.Headers.UserAgent.ToString();

        // Get active tokens (sessions)
        var tokens = await db.Tokens
            .AsNoTracking()
            .Where(t => t.UserId == user.Id
                        && t.RevokedAt == null
                        && t.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        Sessions = tokens.Select(t =>
        {
            var uaInfo = uaParser.Parse(t.UserAgent);
            var isCurrentDevice = !string.IsNullOrEmpty(currentUserAgent) &&
                                  !string.IsNullOrEmpty(t.UserAgent) &&
                                  currentUserAgent.Equals(t.UserAgent, StringComparison.OrdinalIgnoreCase);

            return new SessionViewModel
            {
                Id = t.Id,
                TokenType = t.Type,
                ClientId = t.ClientId ?? "Unknown",
                CreatedAt = t.CreatedAt,
                ExpiresAt = t.ExpiresAt,
                Jti = t.Jti ?? string.Empty,
                IsCurrent = !string.IsNullOrEmpty(currentSessionJti) && t.Jti == currentSessionJti,
                IpAddress = t.IpAddress,
                Browser = uaInfo.Browser,
                Os = uaInfo.Os,
                DeviceType = uaInfo.DeviceType,
                DeviceIcon = uaInfo.Icon,
                IsCurrentDevice = isCurrentDevice
            };
        }).ToList();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid sessionId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/sessions") });

        var token = await db.Tokens
            .FirstOrDefaultAsync(t => t.Id == sessionId && t.UserId == user.Id);

        if (token is null)
        {
            Message = "Session not found or already revoked.";
            return RedirectToPage();
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            Message = "Session revoked successfully.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAllAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/sessions") });

        var currentSessionJti = GetCurrentSessionJti();

        // Revoke all tokens except current session
        var tokensToRevoke = await db.Tokens
            .Where(t => t.UserId == user.Id
                        && t.RevokedAt == null
                        && (string.IsNullOrEmpty(currentSessionJti) || t.Jti != currentSessionJti))
            .ToListAsync();

        foreach (var token in tokensToRevoke)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        Message = $"Revoked {tokensToRevoke.Count} session(s). Current session remains active.";

        return RedirectToPage();
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    private string? GetCurrentSessionJti()
    {
        // Try to get session JTI from claims
        return User?.FindFirst("jti")?.Value;
    }

    public class SessionViewModel
    {
        public Guid Id { get; set; }
        public string TokenType { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Jti { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
        // Phase 5B Feature 3: Session Metadata
        public string? IpAddress { get; set; }
        public string Browser { get; set; } = "Unknown";
        public string Os { get; set; } = "Unknown";
        public string DeviceType { get; set; } = "desktop";
        public string DeviceIcon { get; set; } = "ph ph-desktop";
        public bool IsCurrentDevice { get; set; }
    }
}
