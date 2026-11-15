using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

public sealed class AuthorizationMetadataService(AuthDbContext db, IOpSessionService opSessions) : IAuthorizationMetadataService
{
    public AuthorizeLoginContext GetLoginContext(HttpContext http)
    {
        var authTimeStr = http.User.FindFirst("auth_time")?.Value;
        DateTimeOffset authTimeValue;
        if (long.TryParse(authTimeStr, out var authTime))
        {
            authTimeValue = DateTimeOffset.FromUnixTimeSeconds(authTime);
        }
        else
        {
            // Fallback to current time if not present (e.g. just logged in)
            authTimeValue = DateTimeOffset.UtcNow;
        }

        // Stash upstream identity context (idp/acr/amr) for propagation into tokens
        var idp = http.User.FindFirst(OidcConstants.Claims.Idp)?.Value;
        var acr = http.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
        var amrValues = http.User.Claims.Where(c => c.Type == OidcConstants.Claims.Amr).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();

        if (string.IsNullOrWhiteSpace(acr) && amrValues.Length > 0)
        {
            // Best-effort mapping for local sign-ins.
            // If an upstream IdP provided an explicit acr claim, we keep it.
            if (amrValues.Contains("mfa", StringComparer.Ordinal)) acr = OidcConstants.AcrValues.Mfa;
            else if (amrValues.Contains("webauthn", StringComparer.Ordinal) && amrValues.Contains("user", StringComparer.Ordinal)) acr = OidcConstants.AcrValues.Passkey;
            else if (amrValues.Contains("pwd", StringComparer.Ordinal)) acr = OidcConstants.AcrValues.Password;
        }

        // Also capture mapped claims with ext_map_* prefix
        var mapped = http.User.Claims
            .Where(c => c.Type.StartsWith("ext_map_", StringComparison.Ordinal))
            .ToDictionary(c => c.Type.Substring("ext_map_".Length), c => c.Value, StringComparer.Ordinal);

        // Front-channel logout: the `sid` is the id of the OP session (F1). It is written into the sign-in
        // cookie and travels into the ID token and into the logout token. A cookie that carries no session
        // (the store is off, or a legacy cookie that has not been upgraded yet) has no `sid`: the ID token
        // then carries none either, because a random value is not a session and must not be treated as one.
        var sid = http.User.FindFirst(OidcConstants.Claims.Sid)?.Value;

        return new AuthorizeLoginContext(authTimeValue, idp, acr, amrValues, sid, mapped);
    }

    public async Task PopulateMetadataAsync(HttpContext http, string code, CancellationToken ct = default)
    {
        var loginContext = GetLoginContext(http);

        // Persist the login context onto the auth code row (stored by hash) so token exchange is correct
        // on any replica and across restarts; process memory is not shared between pods.
        var codeHash = AuthorizationCodeHasher.Hash(code);
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == codeHash, ct).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException("Authorization code row not found while persisting login context");
        }

        entity.AuthTime = loginContext.AuthTime;
        entity.UpstreamIdp = loginContext.UpstreamIdp;
        entity.UpstreamAcr = loginContext.UpstreamAcr;
        entity.UpstreamAmr = loginContext.UpstreamAmr is { Length: > 0 }
            ? string.Join(' ', loginContext.UpstreamAmr)
            : null;
        entity.MappedClaimsJson = loginContext.MappedClaims.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(loginContext.MappedClaims)
            : null;
        entity.Sid = loginContext.SessionId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RegisterSessionClientInternalAsync(loginContext.SessionId, entity.ClientId, ct).ConfigureAwait(false);

        return;
    }

    public Task RegisterSessionClientAsync(HttpContext http, string clientId, CancellationToken ct = default)
        => RegisterSessionClientInternalAsync(GetLoginContext(http).SessionId, clientId, ct);

    private async Task RegisterSessionClientInternalAsync(string? sid, string clientId, CancellationToken ct)
    {
        // F1: record the client in the session, so that a logout can target the applications that took part in
        // this session instead of every application the user ever signed in to.
        if (Guid.TryParse(sid, out var sessionId))
        {
            await opSessions.RegisterClientAsync(sessionId, clientId, ct).ConfigureAwait(false);
        }
    }
}
