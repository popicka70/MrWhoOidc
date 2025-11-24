using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.KeyGen.Configuration;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Domain.Services;

/// <summary>
/// Service implementation for generating license tokens.
/// </summary>
public class LicenseGenerationService : ILicenseGenerationService
{
    private readonly KeyGenDbContext _dbContext;
    private readonly KeyGenOptions _options;
    private readonly ILogger<LicenseGenerationService> _logger;

    public LicenseGenerationService(
        KeyGenDbContext dbContext,
        IOptions<KeyGenOptions> options,
        ILogger<LicenseGenerationService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(string TokenId, string JwtToken)> GenerateLicenseTokenAsync(
        string tier,
        string organization,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        string scope,
        string? issuedTo = null,
        Guid? tenantId = null,
        string? tenantSlug = null,
        string? features = null,
        string? limits = null,
        string? createdBy = null,
        string? defaultTenantFeatures = null,
        string? allowedIssuers = null)
    {
        // Validate inputs
        ValidateInputs(tier, organization, notBefore, expiresAt, scope, tenantId, defaultTenantFeatures);

        // Generate unique token ID using UUIDv7
        var tokenId = GuidHelper.NewId().ToString();

        // Load licensing private key
        var ecdsaKey = LoadLicensingPrivateKey();

        // Build JWT claims
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Iss, "MrWhoOidc-KeyGen"),
            new Claim(JwtRegisteredClaimNames.Nbf, notBefore.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Exp, expiresAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, tokenId),
            new Claim("tier", tier),
            new Claim("organization", organization),
            new Claim("license_scope", scope)
        };

        if (!string.IsNullOrWhiteSpace(issuedTo))
        {
            claims.Add(new Claim("issued_to", issuedTo));
        }

        if (tenantId.HasValue)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            claims.Add(new Claim("tenant_slug", tenantSlug));
        }

        if (!string.IsNullOrWhiteSpace(features))
        {
            claims.Add(new Claim("features", features));
        }

        if (!string.IsNullOrWhiteSpace(limits))
        {
            claims.Add(new Claim("limits", limits));
        }

        if (!string.IsNullOrWhiteSpace(defaultTenantFeatures))
        {
            claims.Add(new Claim("default_tenant_features", defaultTenantFeatures));
        }

        if (!string.IsNullOrWhiteSpace(allowedIssuers))
        {
            claims.Add(new Claim("allowed_issuers", allowedIssuers));
        }

        // Create signing credentials with ECDSA P-256
        var signingCredentials = new SigningCredentials(
            new ECDsaSecurityKey(ecdsaKey) { KeyId = "licensing-key" },
            SecurityAlgorithms.EcdsaSha256);

        // Create JWT token descriptor
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = signingCredentials,
            NotBefore = notBefore.UtcDateTime,
            Expires = expiresAt.UtcDateTime
        };

        // Generate JWT
        var tokenHandler = new JwtSecurityTokenHandler();
        var securityToken = tokenHandler.CreateToken(tokenDescriptor);
        var jwtToken = tokenHandler.WriteToken(securityToken);

        // Create metadata record
        var metadata = new LicenseTokenMetadata
        {
            Id = GuidHelper.NewId(),
            TokenId = tokenId,
            Tier = tier,
            Organization = organization,
            Scope = scope,
            IssuedTo = issuedTo,
            TenantId = tenantId,
            TenantSlug = tenantSlug,
            Features = features,
            Limits = limits,
            DefaultTenantFeatures = defaultTenantFeatures,
            ValidFrom = notBefore,
            ValidUntil = expiresAt,
            GeneratedAt = now,
            GeneratedBy = createdBy
        };

        _dbContext.LicenseTokenMetadata.Add(metadata);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Generated license token {TokenId} for organization {Organization} with tier {Tier}",
            tokenId, organization, tier);

        return (tokenId, jwtToken);
    }

    private static void ValidateInputs(
        string tier,
        string organization,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        string scope,
        Guid? tenantId,
        string? defaultTenantFeatures)
    {
        // Validate tier
        var validTiers = new[] { "community", "professional", "enterprise", "enterprise+" };
        if (!validTiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid tier '{tier}'. Must be one of: {string.Join(", ", validTiers)}",
                nameof(tier));
        }

        // Validate organization
        if (string.IsNullOrWhiteSpace(organization))
        {
            throw new ArgumentException("Organization name is required", nameof(organization));
        }

        // Validate date range
        if (notBefore >= expiresAt)
        {
            throw new ArgumentException("Not Before date must be earlier than Expiration date");
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Expiration date must be in the future");
        }

        if (!LicenseScopeOptions.IsValid(scope))
        {
            throw new ArgumentException("License scope must be either 'platform' or 'tenant'", nameof(scope));
        }

        var isTenantScope = scope.Equals(LicenseScopeOptions.Tenant, StringComparison.OrdinalIgnoreCase);
        if (isTenantScope && !tenantId.HasValue)
        {
            throw new ArgumentException("Tenant ID is required for tenant-scoped licenses", nameof(tenantId));
        }

        if (!isTenantScope && tenantId.HasValue)
        {
            throw new ArgumentException("Platform licenses cannot include a tenant ID", nameof(tenantId));
        }

        if (isTenantScope && !string.IsNullOrWhiteSpace(defaultTenantFeatures))
        {
            throw new ArgumentException("Default tenant feature overrides apply only to platform licenses", nameof(defaultTenantFeatures));
        }
    }

    private ECDsa LoadLicensingPrivateKey()
    {
        var keyPath = _options.LicensingPrivateKeyPath;

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new InvalidOperationException(
                "LicensingPrivateKeyPath not configured in appsettings.json");
        }

        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException(
                $"Licensing private key file not found at path: {keyPath}");
        }

        try
        {
            // Read PEM file
            var pemContent = File.ReadAllText(keyPath);

            // Import ECDSA private key from PEM
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pemContent);

            _logger.LogDebug("Loaded licensing private key from {KeyPath}", keyPath);

            return ecdsa;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load licensing private key from {KeyPath}", keyPath);
            throw new InvalidOperationException(
                $"Failed to load licensing private key from {keyPath}", ex);
        }
    }
}
