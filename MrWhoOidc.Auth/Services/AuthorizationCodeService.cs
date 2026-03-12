using System.Security.Cryptography;
using System.Text.Json;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for issuing OIDC authorization codes.
/// </summary>
public interface IAuthorizationCodeService
{
    /// <summary>
    /// Issues an authorization code for a validated request and user.
    /// </summary>
    /// <param name="valid">The validated authorization request.</param>
    /// <param name="userId">The ID of the authenticated user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the issued code or an error.</returns>
    Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default, DateTimeOffset? authTime = null);
}

internal sealed class AuthorizationCodeService(AuthDbContext db, IAuthorizationCodeMetadataStore _meta, ITenantAccessor tenantAccessor, ITenantSettingsService settingsService) : IAuthorizationCodeService
{
    public async Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default, DateTimeOffset? authTime = null)
    {
        // Get tenant-specific authorization code lifetime
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        var settings = await settingsService.GetTenantSettingsAsync(tenantId);
        var lifetimeSeconds = settings?.Tokens?.AuthorizationCodeLifetimeSeconds ?? 300; // Default: 5 minutes

        // Create a random code
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Store a SHA-256 hash of the code in the DB so a DB breach does not expose active codes.
        var codeHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

        var entity = new AuthorizationCode
        {
            Code = codeHash,
            ClientId = valid.ClientId!,
            UserId = userId,
            RedirectUri = valid.RedirectUri!,
            ScopesJson = JsonSerializer.Serialize(valid.Scopes ?? Array.Empty<string>()),
            Nonce = valid.Nonce,
            Resource = valid.Resource,
            ClaimsJson = valid.ClaimsJson,
            AuthorizationDetailsJson = valid.AuthorizationDetailsJson,
            CodeChallenge = valid.CodeChallenge,
            CodeChallengeMethod = valid.CodeChallengeMethod,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds),
            Consumed = false,
            TenantId = tenantId
        };

        db.AuthorizationCodes.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Store transient metadata for this code
        if (valid.Resource is not null)
        {
            _meta.SetResource(code, valid.Resource);
        }
        var now = DateTimeOffset.UtcNow;
        entity.AuthTime = authTime ?? now;
        _meta.SetAuthTime(code, authTime ?? now);

        var uri = new UriBuilder(valid.RedirectUri!);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["code"] = code;
        if (!string.IsNullOrEmpty(valid.Error)) query["error"] = valid.Error; // should not happen here
        return (true, null, BuildRedirect(uri, query, valid), code);
    }

    static string BuildRedirect(UriBuilder uri, System.Collections.Specialized.NameValueCollection query, AuthorizeValidationResult valid)
    {
        if (!string.IsNullOrEmpty(valid.ErrorDescription)) query["error_description"] = valid.ErrorDescription;
        // OIDC Core §3.1.2.5: the nonce MUST NOT appear in the authorization response query.
        // It belongs exclusively in the ID Token to prevent leakage via Referer headers and logs.
        if (!string.IsNullOrEmpty(valid.State)) query["state"] = valid.State;
        uri.Query = query.ToString();
        return uri.ToString();
    }
}
