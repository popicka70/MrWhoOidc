using System.Security.Cryptography;
using System.Text.Json;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface IAuthorizationCodeService
{
    Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default);
}

internal sealed class AuthorizationCodeService(AuthDbContext db, IAuthorizationCodeMetadataStore _meta, ITenantAccessor tenantAccessor) : IAuthorizationCodeService
{
    public async Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default)
    {
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
            ScopesJson = JsonSerializer.Serialize(valid.Scopes),
            Nonce = valid.Nonce,
            CodeChallenge = valid.CodeChallenge,
            CodeChallengeMethod = valid.CodeChallengeMethod,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Consumed = false,
            TenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required")
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
