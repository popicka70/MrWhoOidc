using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>The end-user being logged out, plus the session details from a verified id_token_hint.</summary>
public sealed record LogoutSubject(Guid UserId, string? HintClientId, string? HintSid);

/// <summary>An RP to notify, with the subject identifier that RP knows the user by.</summary>
public sealed record LogoutTarget(Client Client, string Sub, string? Sid);

/// <summary>
/// Determines who is logging out and which RPs must be told.
/// Only authoritative inputs are used: the OP's own authenticated principal, or an id_token_hint whose
/// signature and issuer verify against this tenant's keys. Unverified hints and the non-standard
/// <c>sid</c> query parameter are ignored, so a third party cannot trigger logout notifications.
/// </summary>
public sealed class LogoutTargetResolver(
    AuthDbContext db,
    ICachedKeyProvider keyProvider,
    IPairwiseSubjectService pairwiseSubjects,
    ILogger<LogoutTargetResolver> logger)
{
    // Window in which an issued authorization code still indicates the RP took part in the OP session.
    // Matches the 8h absolute cookie lifetime with headroom; replaced by real session tracking later.
    internal static readonly TimeSpan SessionParticipationWindow = TimeSpan.FromHours(12);

    private static readonly string[] AllowedAlgorithms =
    {
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    };

    public async Task<LogoutSubject?> ResolveSubjectAsync(ClaimsPrincipal currentUser, string? idTokenHint, string issuer, CancellationToken ct)
    {
        Guid? sessionUserId = null;
        if (currentUser.Identity?.IsAuthenticated == true &&
            Guid.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var cookieUserId))
        {
            sessionUserId = cookieUserId;
        }

        var hint = await ValidateHintAsync(idTokenHint, issuer, ct).ConfigureAwait(false);
        Guid? hintUserId = hint is null ? null : await ResolveHintUserAsync(hint.Value.Sub, ct).ConfigureAwait(false);

        if (sessionUserId is { } uid)
        {
            // A hint naming a different user must not widen the logout to that user's RPs.
            return hintUserId == uid
                ? new LogoutSubject(uid, hint!.Value.ClientId, hint.Value.Sid)
                : new LogoutSubject(uid, null, null);
        }

        // No OP session (e.g. it already expired): a verified hint still identifies whose RP sessions to end.
        return hintUserId is { } hid ? new LogoutSubject(hid, hint!.Value.ClientId, hint.Value.Sid) : null;
    }

    /// <summary>RPs that received codes or still hold live tokens for the user, each with its own <c>sub</c>.</summary>
    public async Task<IReadOnlyList<LogoutTarget>> GetTargetsAsync(LogoutSubject subject, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - SessionParticipationWindow;

        var codeClients = db.AuthorizationCodes.AsNoTracking()
            .Where(c => c.UserId == subject.UserId && c.ExpiresAt > since)
            .Select(c => c.ClientId);
        var tokenClients = db.Tokens.AsNoTracking()
            .Where(t => t.UserId == subject.UserId && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => t.ClientId);
        var participating = await codeClients.Union(tokenClients).Distinct().ToListAsync(ct).ConfigureAwait(false);

        if (participating.Count == 0)
        {
            return Array.Empty<LogoutTarget>();
        }

        var clients = await db.Clients.AsNoTracking()
            .Where(c => participating.Contains(c.ClientId) &&
                        (c.BackChannelLogoutUri != null && c.BackChannelLogoutUri != "" ||
                         c.FrontChannelLogoutUri != null && c.FrontChannelLogoutUri != ""))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targets = new List<LogoutTarget>(clients.Count);
        foreach (var client in clients)
        {
            string sub;
            try
            {
                sub = await pairwiseSubjects.GetSubjectAsync(client, subject.UserId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Skipping logout notification for client {ClientIdHash}: subject could not be derived", Bucketization.Bucket(client.ClientId));
                continue;
            }

            // The hint's sid belongs to the RP the hint was issued to; other RPs get sub only.
            var sid = string.Equals(client.ClientId, subject.HintClientId, StringComparison.Ordinal) ? subject.HintSid : null;
            targets.Add(new LogoutTarget(client, sub, sid));
        }

        return targets;
    }

    private async Task<(string Sub, string? Sid, string? ClientId)?> ValidateHintAsync(string? idTokenHint, string issuer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idTokenHint) || !JwtLightParser.IsProbablyJwt(idTokenHint))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = await keyProvider.GetPublicJwksAsync(ct).ConfigureAwait(false),
            ValidateAudience = false,
            // OIDC RP-Initiated Logout §2: the OP should accept an expired id_token_hint.
            ValidateLifetime = false,
            ValidAlgorithms = AllowedAlgorithms,
        };

        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(idTokenHint, parameters, out var validated);
            if (!MrWhoOidc.Auth.Services.IdTokenHintPolicy.IsIdToken((validated as JwtSecurityToken)?.Header.Typ, principal))
            {
                logger.LogInformation("Ignoring id_token_hint at end_session: not an ID token");
                return null;
            }

            var sub = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub))
            {
                return null;
            }

            var clientId = principal.FindFirst("azp")?.Value ?? principal.FindAll("aud").Select(c => c.Value).FirstOrDefault();
            return (sub, principal.FindFirst("sid")?.Value, clientId);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger.LogInformation("Ignoring id_token_hint at end_session: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    private async Task<Guid?> ResolveHintUserAsync(string sub, CancellationToken ct)
    {
        if (Guid.TryParse(sub, out var userId))
        {
            return userId;
        }

        // Pairwise subject: map back to the internal user id.
        return await db.PairwiseSubjectIdentifiers.AsNoTracking()
            .Where(p => p.Subject == sub)
            .Select(p => (Guid?)p.UserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
