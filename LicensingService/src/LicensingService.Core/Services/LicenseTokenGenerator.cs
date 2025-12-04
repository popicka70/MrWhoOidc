using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using LicensingService.Core.Crypto;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace LicensingService.Core.Services;

/// <summary>
/// Default implementation of ILicenseTokenGenerator using ES256.
/// </summary>
public class LicenseTokenGenerator : ILicenseTokenGenerator
{
    private readonly ISigningKeyService _signingKeyService;
    private readonly IConfiguration _configuration;

    public LicenseTokenGenerator(
        ISigningKeyService signingKeyService,
        IConfiguration configuration)
    {
        _signingKeyService = signingKeyService;
        _configuration = configuration;
    }

    public async Task<GenerateLicenseTokenResult> GenerateAsync(GenerateLicenseTokenRequest request, CancellationToken cancellationToken = default)
    {
        // Get active signing key
        var (key, kid) = await _signingKeyService.GetActiveSigningKeyAsync(cancellationToken);

        // Generate unique token ID using UUIDv7
        var tokenId = GuidHelper.NewId().ToString();
        var issuer = _configuration["Licensing:Issuer"] ?? "LicensingService";
        var now = DateTimeOffset.UtcNow;

        // Build claims
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Jti, tokenId),
            new Claim(JwtRegisteredClaimNames.Iss, issuer),
            new Claim(JwtRegisteredClaimNames.Sub, request.CustomerIdentifier),
            new Claim(JwtRegisteredClaimNames.Aud, request.ProductIdentifier),
            new Claim(JwtRegisteredClaimNames.Nbf, request.ValidFrom.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Exp, request.ValidUntil.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim("tier", request.Tier),
            new Claim("scope", request.Scope)
        };

        // Add options as JSON claim if present
        if (request.Options != null && request.Options.Count > 0)
        {
            var optionsJson = JsonSerializer.Serialize(request.Options);
            claims.Add(new Claim("options", optionsJson, JsonClaimValueTypes.Json));
        }

        // Create signing credentials
        var signingCredentials = new SigningCredentials(
            new ECDsaSecurityKey(key) { KeyId = kid },
            SecurityAlgorithms.EcdsaSha256);

        // Create token descriptor
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = signingCredentials,
            NotBefore = request.ValidFrom.UtcDateTime,
            Expires = request.ValidUntil.UtcDateTime
        };

        // Generate JWT
        var tokenHandler = new JwtSecurityTokenHandler();
        var securityToken = tokenHandler.CreateToken(tokenDescriptor);
        var jwtToken = tokenHandler.WriteToken(securityToken);

        return new GenerateLicenseTokenResult
        {
            TokenId = tokenId,
            Token = jwtToken,
            Kid = kid
        };
    }
}
