using System.Security.Cryptography;
using System.Text.Json;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.Auth.Services;

public interface IAuthorizationCodeService
{
    Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default);
}

internal sealed class AuthorizationCodeService(AuthDbContext db, IAuthorizationCodeMetadataStore _meta, ITenantAccessor tenantAccessor, ITenantSettingsService settingsService) : IAuthorizationCodeService
{
    public async Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default)
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

        var entity = new AuthorizationCode
        {
            Code = code,
            ClientId = valid.ClientId!,
            UserId = userId,
            RedirectUri = valid.RedirectUri!,
            ScopesJson = JsonSerializer.Serialize(valid.Scopes ?? Array.Empty<string>()),
            Nonce = valid.Nonce,
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
        _meta.SetAuthTime(code, DateTimeOffset.UtcNow);

        var uri = new UriBuilder(valid.RedirectUri!);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["code"] = code;
        if (!string.IsNullOrEmpty(valid.Error)) query["error"] = valid.Error; // should not happen here
        return (true, null, BuildRedirect(uri, query, valid), code);
    }

    static string BuildRedirect(UriBuilder uri, System.Collections.Specialized.NameValueCollection query, AuthorizeValidationResult valid)
    {
        if (!string.IsNullOrEmpty(valid.ErrorDescription)) query["error_description"] = valid.ErrorDescription;
        if (!string.IsNullOrEmpty(valid.Nonce)) query["nonce"] = valid.Nonce;
        uri.Query = query.ToString();
        return uri.ToString();
    }
}
