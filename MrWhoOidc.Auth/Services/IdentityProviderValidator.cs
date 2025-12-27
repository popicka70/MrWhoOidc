using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for validating identity provider configurations.
/// </summary>
public interface IIdentityProviderValidator
{
    /// <summary>
    /// Validates an identity provider configuration.
    /// </summary>
    /// <param name="provider">The identity provider to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure with an error message.</returns>
    Task<(bool ok, string? error)> ValidateAsync(IdentityProvider provider, CancellationToken ct = default);
}

public sealed class IdentityProviderValidator(
    AuthDbContext db, 
    IHttpClientFactory httpClientFactory,
    ILogger<IdentityProviderValidator> logger) : IIdentityProviderValidator
{
    public async Task<(bool ok, string? error)> ValidateAsync(IdentityProvider provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) return (false, "Name is required");
        if (provider.Name.Length > 150) return (false, "Name too long");

        // Unique name
        var exists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Name == provider.Name && p.Id != provider.Id, ct).ConfigureAwait(false);
        if (exists) return (false, "Name already exists");

        if (provider.Type == IdentityProviderType.Oidc && !string.IsNullOrWhiteSpace(provider.ConfigJson))
        {
            var (ok, error) = OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg);
            if (!ok) return (false, $"Config invalid: {error}");

            // DataAnnotation-based validation for stricter checks
            var context = new ValidationContext(cfg!);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(cfg!, context, results, validateAllProperties: true))
            {
                var msg = string.Join("; ", results.Select(r => r.ErrorMessage));
                return (false, msg);
            }

            // Try discovery if reachable - NON-BLOCKING: failures are logged but don't prevent saving
            var metadataUrl = string.IsNullOrWhiteSpace(cfg!.DiscoveryUrl) ? CombineWellKnown(cfg.Authority) : cfg.DiscoveryUrl!;
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                using var resp = await client.GetAsync(metadataUrl, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Provider '{ProviderName}' discovery check failed: HTTP {StatusCode} from {Url}. Provider saved anyway.",
                        provider.Name, 
                        (int)resp.StatusCode, 
                        metadataUrl);
                }
                else
                {
                    logger.LogInformation(
                        "Provider '{ProviderName}' discovery check successful: {Url}",
                        provider.Name,
                        metadataUrl);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Provider '{ProviderName}' discovery check failed: {ErrorMessage} from {Url}. Provider saved anyway.",
                    provider.Name,
                    ex.Message,
                    metadataUrl);
            }
        }

        return (true, null);
    }

    private static string CombineWellKnown(string authority)
    {
        authority = authority.TrimEnd('/');
        return authority + "/.well-known/openid-configuration";
    }
}
