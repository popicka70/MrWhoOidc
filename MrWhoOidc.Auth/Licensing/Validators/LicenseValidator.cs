using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;

namespace MrWhoOidc.Auth.Licensing.Validators;

internal sealed class LicenseValidator : ILicenseValidator
{
    private const string LegacyIssuer = "MrWhoOidc-License-Authority";
    private const string KeyGenIssuer = "MrWhoOidc-KeyGen";
    private const string ScopeClaim = "license_scope";
    private const string TenantIdClaim = "tenant_id";
    private const string TenantSlugClaim = "tenant_slug";
    private const string IssuedToClaim = "issued_to";
    private const string DefaultTenantFeaturesClaim = "default_tenant_features";
    private const string AllowedIssuersClaim = "allowed_issuers";
    
    private static readonly string[] AllowedIssuers = new[] { KeyGenIssuer, LegacyIssuer };

    internal static IReadOnlyCollection<string> SupportedIssuers => AllowedIssuers;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseValidator> _logger;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TimeProvider _timeProvider;

    public LicenseValidator(
        IOptions<LicensingOptions> options,
        ILogger<LicenseValidator> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokenHandler = new JwtSecurityTokenHandler
        {
            // Avoid mapping inbound claim types to WS-Fed defaults so we can use raw names from the license payload.
            MapInboundClaims = false
        };
    }

    public Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return Task.FromResult(LicenseValidationResult.InvalidFormat());
        }

        try
        {
            var parameters = CreateValidationParameters();
            var principal = _tokenHandler.ValidateToken(licenseKey, parameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt)
            {
                _logger.LogWarning("License token was not recognized as JWT.");
                return Task.FromResult(LicenseValidationResult.InvalidFormat());
            }

            var licenseInfo = ParseLicense(jwt, principal.Claims);
            return Task.FromResult(LicenseValidationResult.Success(licenseInfo));
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning(ex, "License token expired.");
            return Task.FromResult(LicenseValidationResult.Expired());
        }
        catch (SecurityTokenNotYetValidException ex)
        {
            _logger.LogWarning(ex, "License token is not yet valid.");
            return Task.FromResult(LicenseValidationResult.NotYetValid());
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            _logger.LogWarning(ex, "License token signature invalid.");
            return Task.FromResult(LicenseValidationResult.InvalidSignature());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "License token failed validation due to invalid argument.");
            return Task.FromResult(LicenseValidationResult.InvalidFormat());
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "License token failed validation due to invalid format.");
            return Task.FromResult(LicenseValidationResult.InvalidFormat());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating license token signature.");
            return Task.FromResult(LicenseValidationResult.Failure("validation_error", "Unexpected error validating license."));
        }
    }

    public Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return Task.FromResult<LicenseInfo?>(null);
        }

        try
        {
            var token = _tokenHandler.ReadJwtToken(licenseKey);
            var licenseInfo = ParseLicense(token, token.Claims);
            return Task.FromResult<LicenseInfo?>(licenseInfo);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to parse license token.");
            return Task.FromResult<LicenseInfo?>(null);
        }
    }

    public Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseInfo);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _ = LicenseTierExtensions.FromTierString(licenseInfo.Tier);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            _logger.LogWarning(ex, "License tier '{Tier}' was not recognized.", licenseInfo.Tier);
            return Task.FromResult(LicenseValidationResult.InvalidFormat());
        }

        foreach (var (key, value) in licenseInfo.Limits)
        {
            if (value < -1)
            {
                _logger.LogWarning("License limit '{LimitKey}' has invalid value {LimitValue}.", key, value);
                return Task.FromResult(LicenseValidationResult.Failure("invalid_limit", $"Limit '{key}' has invalid value '{value}'."));
            }
        }

        var now = _timeProvider.GetUtcNow();
        var validFrom = licenseInfo.ValidFrom;
        var validUntil = licenseInfo.ValidUntil;

        if (validUntil <= validFrom)
        {
            _logger.LogWarning("License valid period is inconsistent: from {ValidFrom} to {ValidUntil}.", validFrom, validUntil);
            return Task.FromResult(LicenseValidationResult.InvalidFormat());
        }

        if (validFrom > now + ClockSkew)
        {
            _logger.LogWarning("License not yet valid. Starts at {ValidFrom} (UTC).", validFrom);
            return Task.FromResult(LicenseValidationResult.NotYetValid());
        }

        var expired = validUntil < now;
        var graceWindowEnd = validUntil.AddDays(_options.GracePeriodDays);
        var inGraceWindow = ! _options.StrictValidation && expired && now <= graceWindowEnd;

        if (expired && !inGraceWindow)
        {
            _logger.LogWarning("License expired at {ValidUntil} (UTC).", validUntil);
            return Task.FromResult(LicenseValidationResult.Expired());
        }

        if (licenseInfo.Scope == LicenseScope.Tenant)
        {
            foreach (var feature in licenseInfo.EnabledFeatures)
            {
                if (FeatureFlags.IsPlatformOnlyFeature(feature))
                {
                    _logger.LogWarning("License contains platform-only feature {Feature} but is scoped to a tenant.", feature);
                    return Task.FromResult(LicenseValidationResult.PlatformOnlyFeatureNotAllowed(feature));
                }
            }
        }

        if (licenseInfo.TierEnum != LicenseTier.Community && !string.IsNullOrWhiteSpace(_options.PlatformIssuer))
        {
            if (licenseInfo.AllowedIssuers.Count > 0 && !licenseInfo.AllowedIssuers.Any(allowed => IsIssuerMatch(allowed, _options.PlatformIssuer)))
            {
                _logger.LogWarning("License does not allow issuer {Issuer}.", _options.PlatformIssuer);
                return Task.FromResult(LicenseValidationResult.Failure("invalid_issuer", $"License does not allow issuer '{_options.PlatformIssuer}'."));
            }
        }

        var updated = licenseInfo with
        {
            IsExpired = expired,
            IsValid = !expired || inGraceWindow
        };

        return Task.FromResult(LicenseValidationResult.Success(updated));
    }

    private TokenValidationParameters CreateValidationParameters()
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKeyPem))
        {
            throw new InvalidOperationException("Licensing public key is not configured.");
        }

        var ecdsa = CreateEcdsaFromPem(_options.PublicKeyPem);
        var key = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = "licensing-public-key"
        };

        return new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateAudience = false,
            RequireAudience = false,
            ValidateIssuer = _options.StrictValidation,
            ValidIssuer = KeyGenIssuer,
            ValidIssuers = AllowedIssuers,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
            RequireExpirationTime = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 }
        };
    }

    private LicenseInfo ParseLicense(JwtSecurityToken token, IEnumerable<Claim> claims)
    {
        var tier = token.Payload.TryGetValue("tier", out var tierValue) ? tierValue?.ToString() : claims.FirstOrDefault(c => c.Type == "tier")?.Value;
        if (string.IsNullOrWhiteSpace(tier))
        {
            throw new FormatException("License token missing 'tier' claim.");
        }

        var organization = token.Payload.TryGetValue("organization", out var orgValue) ? orgValue?.ToString() : claims.FirstOrDefault(c => c.Type == "organization")?.Value;

        var validFrom = ResolveValidFrom(token);
        var validUntil = ResolveValidUntil(token);

        var features = ParseFeatures(token.Payload.TryGetValue("features", out var featuresValue) ? featuresValue : null, claims, "features");
        var defaultTenantFeatures = ParseFeatures(token.Payload.TryGetValue(DefaultTenantFeaturesClaim, out var defaultFeaturesValue) ? defaultFeaturesValue : null, claims, DefaultTenantFeaturesClaim);
        var allowedIssuers = ParseFeatures(token.Payload.TryGetValue(AllowedIssuersClaim, out var allowedIssuersValue) ? allowedIssuersValue : null, claims, AllowedIssuersClaim);
        var limits = ParseLimits(token.Payload.TryGetValue("limits", out var limitsValue) ? limitsValue : null, claims);

        var scopeInfo = ResolveScope(token, claims);
        var issuedTo = ResolveIssuedTo(token, claims);
        var tenantId = ParseGuidClaim(token.Payload.TryGetValue(TenantIdClaim, out var tenantIdValue) ? tenantIdValue?.ToString() : claims.FirstOrDefault(c => c.Type == TenantIdClaim)?.Value);
        var tenantSlug = ResolveStringClaim(token, TenantSlugClaim, claims);

        var now = _timeProvider.GetUtcNow();
        var isExpired = validUntil <= now;

        return new LicenseInfo(
            tier,
            organization,
            validFrom,
            validUntil,
            features,
            limits,
            isExpired,
            !isExpired,
            scopeInfo.Scope,
            issuedTo,
            tenantId,
            tenantSlug,
            defaultTenantFeatures,
            scopeInfo.HasExplicitScopeClaim,
            allowedIssuers);
    }

    private static DateTimeOffset ResolveValidFrom(JwtSecurityToken token)
    {
        if (token.ValidFrom != DateTime.MinValue)
        {
            var utc = DateTime.SpecifyKind(token.ValidFrom, DateTimeKind.Utc);
            return new DateTimeOffset(utc);
        }

        if (token.Payload.ValidFrom != DateTime.MinValue)
        {
            var nbfUtc = DateTime.SpecifyKind(token.Payload.ValidFrom, DateTimeKind.Utc);
            return new DateTimeOffset(nbfUtc);
        }

        if (token.Payload.IssuedAt != DateTime.MinValue)
        {
            var iatUtc = DateTime.SpecifyKind(token.Payload.IssuedAt, DateTimeKind.Utc);
            return new DateTimeOffset(iatUtc);
        }

        throw new FormatException("License token missing 'nbf' or 'iat' claim.");
    }

    private static DateTimeOffset ResolveValidUntil(JwtSecurityToken token)
    {
        if (token.ValidTo == DateTime.MinValue)
        {
            throw new FormatException("License token missing 'exp' claim.");
        }

        var utc = DateTime.SpecifyKind(token.ValidTo, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static IReadOnlySet<string> ParseFeatures(object? value, IEnumerable<Claim> claims, string claimType)
    {
        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (value is not null)
        {
            PopulateFeaturesFromValue(features, value);
        }
        else
        {
            foreach (var claim in claims.Where(c => string.Equals(c.Type, claimType, StringComparison.Ordinal)))
            {
                PopulateFeaturesFromValue(features, claim.Value);
            }
        }

        return features;
    }

    private static void PopulateFeaturesFromValue(ISet<string> features, object value)
    {
        switch (value)
        {
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var featureValue = item.GetString();
                            if (!string.IsNullOrWhiteSpace(featureValue))
                            {
                                features.Add(featureValue);
                            }
                        }
                    }
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    var featureValue = element.GetString();
                    if (!string.IsNullOrWhiteSpace(featureValue))
                    {
                        features.Add(featureValue);
                    }
                }
                break;
            case IEnumerable<object> enumerable:
                foreach (var item in enumerable)
                {
                    PopulateFeaturesFromValue(features, item);
                }
                break;
            case string str:
                if (IsJsonLike(str))
                {
                    using var doc = JsonDocument.Parse(str);
                    PopulateFeaturesFromValue(features, doc.RootElement);
                }
                else if (!string.IsNullOrWhiteSpace(str))
                {
                    features.Add(str);
                }
                break;
            case Claim claim:
                PopulateFeaturesFromValue(features, claim.Value);
                break;
        }
    }

    private static IReadOnlyDictionary<string, long> ParseLimits(object? value, IEnumerable<Claim> claims)
    {
        if (value is null)
        {
            foreach (var claim in claims.Where(c => string.Equals(c.Type, "limits", StringComparison.Ordinal)))
            {
                var fromClaim = ParseLimitsValue(claim.Value);
                if (fromClaim.Count > 0)
                {
                    return fromClaim;
                }
            }

            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        return ParseLimitsValue(value);
    }

    private static (LicenseScope Scope, bool HasExplicitScopeClaim) ResolveScope(JwtSecurityToken token, IEnumerable<Claim> claims)
    {
        var scopeClaim = ResolveStringClaim(token, ScopeClaim, claims);
        if (string.IsNullOrWhiteSpace(scopeClaim))
        {
            return (LicenseScope.Platform, false);
        }

        return scopeClaim.ToLowerInvariant() switch
        {
            "platform" => (LicenseScope.Platform, true),
            "tenant" => (LicenseScope.Tenant, true),
            _ => throw new FormatException($"Unsupported license scope '{scopeClaim}'.")
        };
    }

    private static string? ResolveIssuedTo(JwtSecurityToken token, IEnumerable<Claim> claims)
    {
        return ResolveStringClaim(token, IssuedToClaim, claims)
            ?? ResolveStringClaim(token, TenantSlugClaim, claims)
            ?? token.Subject;
    }

    private static string? ResolveStringClaim(JwtSecurityToken token, string claimName, IEnumerable<Claim> claims)
    {
        if (token.Payload.TryGetValue(claimName, out var claimValue) && claimValue is not null)
        {
            return claimValue.ToString();
        }

        return claims.FirstOrDefault(c => string.Equals(c.Type, claimName, StringComparison.Ordinal))?.Value;
    }

    private static Guid? ParseGuidClaim(string? value)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, long> ParseLimitsValue(object value)
    {
        return value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Object => ParseLimitsFromJson(element),
            JsonElement element when element.ValueKind == JsonValueKind.String => ParseLimitsValue(element.GetString() ?? string.Empty),
            IDictionary<string, object> dictionary => ParseLimitsFromDictionary(dictionary),
            IDictionary<string, string> stringDictionary => ParseLimitsFromStringDictionary(stringDictionary),
            string str => ParseLimitsFromString(str),
            _ => throw new FormatException("Unsupported limits claim format.")
        };
    }

    private static IReadOnlyDictionary<string, long> ParseLimitsFromJson(JsonElement element)
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            limits[property.Name] = ParseLimitNumber(property.Value);
        }

        return limits;
    }

    private static IReadOnlyDictionary<string, long> ParseLimitsFromDictionary(IDictionary<string, object> dictionary)
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dictionary)
        {
            limits[kvp.Key] = ConvertToInt64(kvp.Value);
        }

        return limits;
    }

    private static IReadOnlyDictionary<string, long> ParseLimitsFromStringDictionary(IDictionary<string, string> dictionary)
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dictionary)
        {
            limits[kvp.Key] = Convert.ToInt64(kvp.Value, CultureInfo.InvariantCulture);
        }

        return limits;
    }

    private static IReadOnlyDictionary<string, long> ParseLimitsFromString(string str)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        if (!IsJsonLike(str))
        {
            throw new FormatException("Limits claim must be JSON object.");
        }

        using var doc = JsonDocument.Parse(str);
        return ParseLimitsValue(doc.RootElement);
    }

    private static long ParseLimitNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new FormatException("Limit value must be a number.");
    }

    private static long ConvertToInt64(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            decimal d => Convert.ToInt64(d, CultureInfo.InvariantCulture),
            double dbl => Convert.ToInt64(dbl, CultureInfo.InvariantCulture),
            string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element => ParseLimitNumber(element),
            _ => throw new FormatException("Unable to parse limit value to integer.")
        };
    }

    private static bool IsJsonLike(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.StartsWith("[") && trimmed.EndsWith("]")) || (trimmed.StartsWith("{") && trimmed.EndsWith("}"));
    }

    private static ECDsa CreateEcdsaFromPem(string pem)
    {
        var normalized = pem.Trim();
        if (!normalized.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Licensing public key must be provided in PEM format.");
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(normalized);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static bool IsIssuerMatch(string allowedPattern, string actualIssuer)
    {
        if (string.Equals(allowedPattern, actualIssuer, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(allowedPattern, UriKind.Absolute, out var allowedUri) &&
            Uri.TryCreate(actualIssuer, UriKind.Absolute, out var actualUri))
        {
            return string.Equals(allowedUri.Scheme, actualUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(allowedUri.Host, actualUri.Host, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
