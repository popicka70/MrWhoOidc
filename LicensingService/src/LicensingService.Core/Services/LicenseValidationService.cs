using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using LicensingService.Core.Crypto;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace LicensingService.Core.Services;

/// <summary>
/// Default implementation of ILicenseValidationService.
/// </summary>
public class LicenseValidationService : ILicenseValidationService
{
    private readonly ISigningKeyService _signingKeyService;
    private readonly ILicenseStore? _licenseStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LicenseValidationService> _logger;

    public LicenseValidationService(
        ISigningKeyService signingKeyService,
        IConfiguration configuration,
        ILogger<LicenseValidationService> logger,
        ILicenseStore? licenseStore = null)
    {
        _signingKeyService = signingKeyService;
        _configuration = configuration;
        _logger = logger;
        _licenseStore = licenseStore;
    }

    public async Task<LicenseValidationResult> ValidateAsync(string token, bool checkDatabase = false, CancellationToken cancellationToken = default)
    {
        return await ValidateInternalAsync(token, expectedProductIdentifier: null, checkDatabase, cancellationToken);
    }

    public async Task<LicenseValidationResult> ValidateForProductAsync(string token, string expectedProductIdentifier, bool checkDatabase = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedProductIdentifier))
        {
            return LicenseValidationResult.Failure("Product identifier is required", "invalid_product");
        }

        return await ValidateInternalAsync(token, expectedProductIdentifier, checkDatabase, cancellationToken);
    }

    private async Task<LicenseValidationResult> ValidateInternalAsync(
        string token,
        string? expectedProductIdentifier,
        bool checkDatabase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return LicenseValidationResult.Failure("Token is required", "missing_token");
        }

        try
        {
            // Get all public keys for validation
            var publicKeys = await _signingKeyService.GetPublicKeysAsync(cancellationToken);
            if (publicKeys.Count == 0)
            {
                _logger.LogError("No signing keys available for validation");
                return LicenseValidationResult.Failure("No signing keys available", "no_keys");
            }

            // Build signing keys for validation
            var securityKeys = publicKeys
                .Select(pk => new ECDsaSecurityKey(pk.Key) { KeyId = pk.Kid })
                .ToList();

            var issuer = _configuration["Licensing:Issuer"] ?? "LicensingService";

            // Configure validation parameters
            var validationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = !string.IsNullOrEmpty(expectedProductIdentifier),
                ValidAudience = expectedProductIdentifier,
                IssuerSigningKeys = securityKeys,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                RequireExpirationTime = true,
                RequireSignedTokens = true
            };

            // Validate the token
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt)
            {
                return LicenseValidationResult.Failure("Invalid token format", "invalid_format");
            }

            // Extract claims
            var tokenId = jwt.Id;
            var customerIdentifier = jwt.Subject;
            var productIdentifier = jwt.Audiences.FirstOrDefault();
            var tier = jwt.Claims.FirstOrDefault(c => c.Type == "tier")?.Value;
            var scope = jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value;

            if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(customerIdentifier) ||
                string.IsNullOrEmpty(productIdentifier) || string.IsNullOrEmpty(tier))
            {
                return LicenseValidationResult.Failure("Token missing required claims", "missing_claims");
            }

            // Parse options if present
            Dictionary<string, object>? options = null;
            var optionsClaim = jwt.Claims.FirstOrDefault(c => c.Type == "options");
            if (optionsClaim != null)
            {
                try
                {
                    options = JsonSerializer.Deserialize<Dictionary<string, object>>(optionsClaim.Value);
                }
                catch
                {
                    // Options parsing failed, continue without them
                    _logger.LogWarning("Failed to parse options claim for token {TokenId}", tokenId);
                }
            }

            var validFrom = new DateTimeOffset(jwt.ValidFrom, TimeSpan.Zero);
            var validUntil = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);

            // Check database for revocation if requested
            string? databaseStatus = null;
            bool isRevoked = false;

            if (checkDatabase && _licenseStore != null)
            {
                var license = await _licenseStore.GetByTokenIdAsync(tokenId, cancellationToken);
                if (license != null)
                {
                    databaseStatus = license.Status.ToString();
                    isRevoked = license.Status == LicenseStatus.Revoked;

                    if (isRevoked)
                    {
                        _logger.LogInformation("License {TokenId} is revoked", tokenId);
                    }
                }
                else
                {
                    _logger.LogWarning("License {TokenId} not found in database", tokenId);
                }
            }

            _logger.LogDebug("License {TokenId} validated successfully for {Customer}/{Product}",
                tokenId, customerIdentifier, productIdentifier);

            return LicenseValidationResult.Success(
                tokenId,
                customerIdentifier,
                productIdentifier,
                tier,
                scope ?? "default",
                validFrom,
                validUntil,
                options,
                databaseStatus,
                isRevoked);
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogInformation("Token validation failed: expired at {Expiry}", ex.Expires);
            return LicenseValidationResult.Failure($"License expired at {ex.Expires:u}", "token_expired");
        }
        catch (SecurityTokenNotYetValidException ex)
        {
            _logger.LogInformation("Token validation failed: not valid until {ValidFrom}", ex.NotBefore);
            return LicenseValidationResult.Failure($"License not valid until {ex.NotBefore:u}", "token_not_yet_valid");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            _logger.LogWarning("Token validation failed: invalid signature");
            return LicenseValidationResult.Failure("Invalid token signature", "invalid_signature");
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            _logger.LogWarning("Token validation failed: invalid issuer");
            return LicenseValidationResult.Failure("Invalid token issuer", "invalid_issuer");
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            _logger.LogWarning("Token validation failed: invalid audience");
            return LicenseValidationResult.Failure("License not valid for this product", "invalid_audience");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return LicenseValidationResult.Failure("Token validation failed", "validation_failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during token validation");
            return LicenseValidationResult.Failure("An error occurred during validation", "internal_error");
        }
    }
}
