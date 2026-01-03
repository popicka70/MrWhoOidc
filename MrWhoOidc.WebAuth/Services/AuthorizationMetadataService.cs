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

public sealed class AuthorizationMetadataService(IAuthorizationCodeMetadataStore meta, AuthDbContext db) : IAuthorizationMetadataService
{
    public async Task PopulateMetadataAsync(HttpContext http, string code, CancellationToken ct = default)
    {
        // Capture auth_time
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

        meta.SetAuthTime(code, authTimeValue);

        // New: stash upstream identity context (idp/acr/amr) for propagation into tokens
        var idp = http.User.FindFirst(OidcConstants.Claims.Idp)?.Value;
        var acr = http.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
        var amrValues = http.User.Claims.Where(c => c.Type == OidcConstants.Claims.Amr).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
        var amr = amrValues.Length > 0 ? string.Join(' ', amrValues) : null; // store space-delimited
        meta.SetUpstream(code, idp, acr, amr);

        // Also capture mapped claims with ext_map_* prefix
        var mapped = http.User.Claims
            .Where(c => c.Type.StartsWith("ext_map_", StringComparison.Ordinal))
            .ToDictionary(c => c.Type.Substring("ext_map_".Length), c => c.Value, StringComparer.Ordinal);
        if (mapped.Count > 0)
        {
            meta.SetMappedClaims(code, mapped);
        }

        // Front-channel logout: generate sid and store with the code for ID token issuance
        var sid = http.User.FindFirst(OidcConstants.Claims.Sid)?.Value ?? Guid.NewGuid().ToString("N");
        meta.SetSid(code, sid);

        // Persist key pieces of metadata onto the auth code row so token exchange remains correct
        // even if the server restarts between /authorize and /token.
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == code, ct).ConfigureAwait(false);
        if (entity is not null)
        {
            entity.AuthTime = authTimeValue;
            // NOTE: upstream context + sid remain in the in-memory store for now.
            // We can extend persistence further later without changing behavior here.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return;
    }
}
