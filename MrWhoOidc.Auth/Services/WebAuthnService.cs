using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Webauthn;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// WebAuthn/passkey service implemented with .NET built-in APIs only.
/// No external FIDO2 library required — crypto is handled by
/// System.Security.Cryptography and System.Formats.Cbor.
/// </summary>
internal sealed class WebAuthnService : IWebAuthnService
{
    private readonly AuthDbContext _db;
    private readonly HybridCache _cache;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IOptions<WebAuthnOptions> _options;
    private readonly ILogger<WebAuthnService> _logger;

    public WebAuthnService(
        AuthDbContext db,
        HybridCache cache,
        ITenantAccessor tenantAccessor,
        IOptions<WebAuthnOptions> options,
        ILogger<WebAuthnService> logger)
    {
        _db = db;
        _cache = cache;
        _tenantAccessor = tenantAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task<(WebAuthnRegistrationOptions options, string sessionId)> CreateRegistrationChallengeAsync(
        User user,
        bool excludeCredentials = true,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = GetEffectiveOptions();
        if (!effectiveOptions.Enabled)
            throw new InvalidOperationException("WebAuthn is disabled for this tenant");

        var sessionId = GuidHelper.NewId().ToString();

        var activeCredentialCount = await _db.WebAuthnCredentials
            .CountAsync(c => c.UserId == user.Id && c.TenantId == user.TenantId && c.IsActive, cancellationToken);
        if (activeCredentialCount >= effectiveOptions.MaxCredentialsPerUser)
            throw new InvalidOperationException($"Maximum of {effectiveOptions.MaxCredentialsPerUser} WebAuthn credentials per user reached");

        // Build excludeCredentials list
        WebAuthnCredentialDescriptor[]? excludeList = null;
        var shouldExcludeExisting = excludeCredentials && effectiveOptions.ExcludeExistingCredentials;
        if (shouldExcludeExisting)
        {
            var existing = await _db.WebAuthnCredentials
                .Where(c => c.UserId == user.Id && c.TenantId == user.TenantId && c.IsActive)
                .Select(c => c.CredentialId)
                .ToListAsync(cancellationToken);

            if (existing.Count > 0)
                excludeList = existing
                    .Select(id => new WebAuthnCredentialDescriptor { Id = Convert.FromBase64String(id) })
                    .ToArray();
        }

        // Determine supported algorithms
        var algIds = effectiveOptions.AllowedAlgorithms.Length > 0
            ? effectiveOptions.AllowedAlgorithms
            : new[] { -7, -257 }; // ES256 (P-256), RS256 (RSA)

        var challenge = RandomNumberGenerator.GetBytes(32);

        var options = new WebAuthnRegistrationOptions
        {
            Rp = new WebAuthnRp { Id = effectiveOptions.RelyingPartyId, Name = effectiveOptions.RelyingPartyName },
            User = new WebAuthnUser
            {
                Id = Encoding.UTF8.GetBytes(user.Id.ToString()),
                Name = user.Username,
                DisplayName = user.Name ?? user.Username
            },
            Challenge = challenge,
            PubKeyCredParams = algIds.Select(a => new WebAuthnPubKeyParam { Alg = a }).ToArray(),
            Timeout = (int)(effectiveOptions.RegistrationTimeoutSeconds * 1000L),
            Attestation = effectiveOptions.AttestationConveyance,
            AuthenticatorSelection = new WebAuthnAuthenticatorSelection
            {
                ResidentKey = effectiveOptions.ResidentKey,
                RequireResidentKey = string.Equals(effectiveOptions.ResidentKey, "required", StringComparison.OrdinalIgnoreCase),
                UserVerification = effectiveOptions.UserVerification,
                AuthenticatorAttachment = effectiveOptions.AuthenticatorAttachment
            },
            ExcludeCredentials = excludeList
        };

        // Cache the challenge session
        var cacheKey = $"webauthn_registration_{sessionId}";
        var cacheOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds)
        };

        await _cache.SetAsync(cacheKey, new WebAuthnChallengeSession
        {
            Challenge = challenge,
            UserId = user.Id,
            TenantId = user.TenantId,
            Type = WebAuthnChallengeType.Registration,
            CreatedAt = DateTimeOffset.UtcNow
        }, cacheOptions, cancellationToken: cancellationToken);

        _logger.LogDebug("Created WebAuthn registration challenge for user {UserId} with session {SessionId}",
            user.Id, sessionId);

        return (options, sessionId);
    }

    public async Task<(bool success, string? credentialId, string? errorMessage)> CompleteRegistrationAsync(
        User user,
        WebAuthnAttestationResponse attestationResponse,
        string sessionId,
        string? friendlyName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveOptions = GetEffectiveOptions();
            if (!effectiveOptions.Enabled)
                return (false, null, "WebAuthn is disabled for this tenant");

            // Retrieve the challenge session
            var cacheKey = $"webauthn_registration_{sessionId}";
            var session = await _cache.GetOrCreateAsync<object?, WebAuthnChallengeSession?>(
                cacheKey,
                null,
                async (_, ct) => (WebAuthnChallengeSession?)null,
                cancellationToken: cancellationToken);

            if (session == null)
                return (false, null, "Registration session not found or expired");

            if (session.UserId != user.Id || session.TenantId != user.TenantId)
                return (false, null, "Invalid session for user");

            var activeCredentialCount = await _db.WebAuthnCredentials
                .CountAsync(c => c.UserId == user.Id && c.TenantId == user.TenantId && c.IsActive, cancellationToken);
            if (activeCredentialCount >= effectiveOptions.MaxCredentialsPerUser)
                return (false, null, $"Maximum of {effectiveOptions.MaxCredentialsPerUser} WebAuthn credentials per user reached");

            if (attestationResponse.Response?.ClientDataJSON is null)
                return (false, null, "Missing clientDataJSON in attestation response");
            if (attestationResponse.Response.AttestationObject is null)
                return (false, null, "Missing attestationObject in attestation response");

            var origins = effectiveOptions.AllowedOrigins.Length > 0
                ? effectiveOptions.AllowedOrigins
                : new[] { $"https://{effectiveOptions.RelyingPartyId}" };

            var transports = attestationResponse.Transports ?? attestationResponse.Response.Transports;

            // Cryptographic verification
            var result = WebAuthnCrypto.VerifyRegistration(
                clientDataJson: attestationResponse.Response.ClientDataJSON,
                attestationObject: attestationResponse.Response.AttestationObject,
                transports: transports,
                expectedChallenge: session.Challenge,
                rpId: effectiveOptions.RelyingPartyId,
                expectedOrigins: origins);

            var aaguidBase64 = result.AaGuid.Length == 16 && result.AaGuid.Any(b => b != 0)
                ? Convert.ToBase64String(result.AaGuid)
                : null;

            var aaguidPolicyError = ValidateAaguidPolicy(aaguidBase64, effectiveOptions.ValidateAaguid, effectiveOptions.AllowedAaguids);
            if (aaguidPolicyError != null)
                return (false, null, aaguidPolicyError);

            // Store the credential in the database
            var webAuthnCredential = new WebAuthnCredential
            {
                Id = GuidHelper.NewId(),
                TenantId = user.TenantId,
                UserId = user.Id,
                CredentialId = Convert.ToBase64String(result.CredentialId),
                PublicKey = Convert.ToBase64String(result.CosePublicKey),
                Type = "public-key",
                AttestationType = result.AttestationFormat,
                AaguidBase64 = aaguidBase64,
                SignatureCounter = result.SignCount,
                Transport = result.Transports,
                FriendlyName = string.IsNullOrWhiteSpace(friendlyName)
                    ? GetDefaultCredentialName(effectiveOptions.DefaultCredentialNamePattern)
                    : friendlyName,
                DeviceType = GetDeviceTypeFromTransports(result.Transports),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.WebAuthnCredentials.Add(webAuthnCredential);
            await _db.SaveChangesAsync(cancellationToken);

            // Clear the session
            await _cache.RemoveAsync(cacheKey, cancellationToken);

            _logger.LogInformation("Successfully registered WebAuthn credential {CredentialId} for user {UserId}",
                webAuthnCredential.CredentialId, user.Id);

            return (true, webAuthnCredential.CredentialId, null);
        }
        catch (WebAuthnVerificationException ex)
        {
            _logger.LogWarning("WebAuthn registration verification failed for user {UserId}: {Error}",
                user.Id, ex.Message);
            return (false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during WebAuthn registration for user {UserId}", user.Id);
            return (false, null, "Registration failed due to an internal error");
        }
    }

    public async Task<(WebAuthnAssertionOptions options, string sessionId)> CreateAuthenticationChallengeAsync(
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = GetEffectiveOptions();
        if (!effectiveOptions.Enabled)
            throw new InvalidOperationException("WebAuthn is disabled for this tenant");
        if (username == null && !effectiveOptions.AllowUsernamelessAuthentication)
            throw new InvalidOperationException("Usernameless WebAuthn authentication is disabled for this tenant");

        var sessionId = GuidHelper.NewId().ToString();
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("No tenant context");

        // Build allowCredentials list for the user (if username provided)
        WebAuthnCredentialDescriptor[]? allowList = null;
        if (username != null)
        {
            var creds = await _db.WebAuthnCredentials
                .Include(c => c.User)
                .Where(c => c.User.Username == username && c.TenantId == tenantId && c.IsActive)
                .ToListAsync(cancellationToken);

            if (creds.Count > 0)
                allowList = creds
                    .Select(c => new WebAuthnCredentialDescriptor
                    {
                        Id = Convert.FromBase64String(c.CredentialId),
                        Transports = string.IsNullOrEmpty(c.Transport)
                            ? null
                            : c.Transport.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    })
                    .ToArray();
        }

        var challenge = RandomNumberGenerator.GetBytes(32);

        var options = new WebAuthnAssertionOptions
        {
            RpId = effectiveOptions.RelyingPartyId,
            Challenge = challenge,
            Timeout = (int)(effectiveOptions.AuthenticationTimeoutSeconds * 1000L),
            UserVerification = effectiveOptions.UserVerification,
            AllowCredentials = allowList
        };

        // Cache the challenge session
        var cacheKey = $"webauthn_authentication_{sessionId}";
        var cacheOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds)
        };

        await _cache.SetAsync(cacheKey, new WebAuthnChallengeSession
        {
            Challenge = challenge,
            Username = username,
            TenantId = tenantId,
            Type = WebAuthnChallengeType.Authentication,
            CreatedAt = DateTimeOffset.UtcNow
        }, cacheOptions, cancellationToken: cancellationToken);

        _logger.LogDebug("Created WebAuthn authentication challenge for user {Username} with session {SessionId}",
            username ?? "[usernameless]", sessionId);

        return (options, sessionId);
    }

    public async Task<(bool success, User? user, string? errorMessage)> CompleteAuthenticationAsync(
        WebAuthnAssertionResponse assertionResponse,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveOptions = GetEffectiveOptions();
            if (!effectiveOptions.Enabled)
                return (false, null, "WebAuthn is disabled for this tenant");

            // Retrieve the challenge session
            var cacheKey = $"webauthn_authentication_{sessionId}";
            var session = await _cache.GetOrCreateAsync<object?, WebAuthnChallengeSession?>(
                cacheKey,
                null,
                async (_, ct) => (WebAuthnChallengeSession?)null,
                cancellationToken: cancellationToken);

            if (session == null)
                return (false, null, "Authentication session not found or expired");

            if (assertionResponse.Response?.ClientDataJSON is null)
                return (false, null, "Missing clientDataJSON in assertion response");
            if (assertionResponse.Response.AuthenticatorData is null)
                return (false, null, "Missing authenticatorData in assertion response");
            if (assertionResponse.Response.Signature is null)
                return (false, null, "Missing signature in assertion response");
            if (assertionResponse.RawId is null)
                return (false, null, "Missing rawId in assertion response");

            // Find the credential used for authentication
            var credentialIdBase64 = Convert.ToBase64String(assertionResponse.RawId);
            var credential = await _db.WebAuthnCredentials
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.CredentialId == credentialIdBase64 &&
                                         c.TenantId == session.TenantId &&
                                         c.IsActive, cancellationToken);

            if (credential == null)
                return (false, null, "Credential not found");

            var origins = effectiveOptions.AllowedOrigins.Length > 0
                ? effectiveOptions.AllowedOrigins
                : new[] { $"https://{effectiveOptions.RelyingPartyId}" };

            var storedCoseKey = Convert.FromBase64String(credential.PublicKey);

            // Cryptographic verification
            var result = WebAuthnCrypto.VerifyAuthentication(
                clientDataJson: assertionResponse.Response.ClientDataJSON,
                authenticatorData: assertionResponse.Response.AuthenticatorData,
                signature: assertionResponse.Response.Signature,
                userHandle: assertionResponse.Response.UserHandle,
                storedCosePublicKey: storedCoseKey,
                storedSignCount: credential.SignatureCounter,
                enforceSignatureCounter: effectiveOptions.EnforceSignatureCounter,
                expectedChallenge: session.Challenge,
                rpId: effectiveOptions.RelyingPartyId,
                expectedOrigins: origins);

            // Verify userHandle ownership when present
            if (result.UserHandle != null)
            {
                var userIdFromHandle = Encoding.UTF8.GetString(result.UserHandle);
                if (userIdFromHandle != credential.UserId.ToString())
                    return (false, null, "userHandle does not match credential owner");
            }

            // Update signature counter and last used timestamp
            credential.SignatureCounter = result.NewSignCount;
            credential.LastUsedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            // Clear the session
            await _cache.RemoveAsync(cacheKey, cancellationToken);

            _logger.LogInformation("Successful WebAuthn authentication for user {UserId} using credential {CredentialId}",
                credential.UserId, credential.CredentialId);

            return (true, credential.User, null);
        }
        catch (WebAuthnVerificationException ex)
        {
            _logger.LogWarning("WebAuthn authentication verification failed: {Error}", ex.Message);
            return (false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during WebAuthn authentication");
            return (false, null, "Authentication failed due to an internal error");
        }
    }

    public async Task<IReadOnlyList<WebAuthnCredential>> GetUserCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("No tenant context");

        return await _db.WebAuthnCredentials
            .Where(c => c.UserId == userId && c.TenantId == tenantId && c.IsActive)
            .OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("No tenant context");

        var credential = await _db.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId &&
                                     c.UserId == userId &&
                                     c.TenantId == tenantId, cancellationToken);

        if (credential == null)
            return false;

        credential.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Removed WebAuthn credential {CredentialId} for user {UserId}",
            credentialId, userId);

        return true;
    }

    public async Task<bool> UpdateCredentialNameAsync(
        Guid userId,
        Guid credentialId,
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("No tenant context");

        var credential = await _db.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId &&
                                     c.UserId == userId &&
                                     c.TenantId == tenantId &&
                                     c.IsActive, cancellationToken);

        if (credential == null)
            return false;

        credential.FriendlyName = friendlyName;
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> HasWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("No tenant context");

        return await _db.WebAuthnCredentials
            .AnyAsync(c => c.UserId == userId && c.TenantId == tenantId && c.IsActive, cancellationToken);
    }

    // Private helper methods

    private string GetDefaultCredentialName(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return $"Security Key - {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}";

        return pattern.Replace("{datetime:yyyy-MM-dd HH:mm}", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal)
            .Replace("{datetime}", DateTimeOffset.UtcNow.ToString("u"), StringComparison.Ordinal)
            .Replace("{deviceType}", "security-key", StringComparison.Ordinal)
            .Replace("{transport}", "unknown", StringComparison.Ordinal);
    }

    private static string? GetDeviceTypeFromTransports(string? transports)
    {
        if (string.IsNullOrEmpty(transports)) return null;
        var parts = transports.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("internal", StringComparer.OrdinalIgnoreCase)) return "platform";
        if (parts.Any(t => string.Equals(t, "usb", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t, "nfc", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t, "ble", StringComparison.OrdinalIgnoreCase)))
            return "cross-platform";
        return null;
    }

    private EffectiveWebAuthnOptions GetEffectiveOptions()
    {
        var root = _options.Value;
        var tenantSlug = _tenantAccessor.CurrentTenant?.Slug;
        WebAuthnTenantOverrides? tenantOverride = null;
        var hasOverride = !string.IsNullOrWhiteSpace(tenantSlug) &&
                          root.TenantOverrides.TryGetValue(tenantSlug!, out tenantOverride);

        T Get<T>(T rootVal, T? ovVal) where T : struct
            => (hasOverride && ovVal.HasValue) ? ovVal.Value : rootVal;

        string GetStr(string rootVal, string? ovVal)
            => (hasOverride && !string.IsNullOrWhiteSpace(ovVal)) ? ovVal! : rootVal;

        string? GetStrNull(string? rootVal, string? ovVal)
            => (hasOverride && ovVal is not null) ? ovVal : rootVal;

        IReadOnlyList<string> GetList(string[] rootList, string[]? ovList)
            => (hasOverride && ovList is not null) ? ovList : rootList;

        var rpId = GetStr(root.RelyingPartyId ?? "localhost", tenantOverride?.RelyingPartyId)!;
        var allowedOrigins = (hasOverride && tenantOverride?.AllowedOrigins is not null)
            ? tenantOverride.AllowedOrigins
            : root.AllowedOrigins;

        if (allowedOrigins.Length == 0)
        {
            var issuerUri = _tenantAccessor.CurrentTenant?.IssuerUri;
            if (Uri.TryCreate(issuerUri, UriKind.Absolute, out var issuer))
            {
                allowedOrigins = new[] { issuer.GetLeftPart(UriPartial.Authority) };
            }
            else
            {
                allowedOrigins = new[] { $"https://{rpId}" };
            }
        }

        return new EffectiveWebAuthnOptions(
            Enabled: Get(root.Enabled, tenantOverride?.Enabled),
            ExcludeExistingCredentials: Get(root.ExcludeExistingCredentials, tenantOverride?.ExcludeExistingCredentials),
            AllowUsernamelessAuthentication: Get(root.AllowUsernamelessAuthentication, tenantOverride?.AllowUsernamelessAuthentication),
            ChallengeSessionLifetimeSeconds: root.ChallengeSessionLifetimeSeconds,
            MaxCredentialsPerUser: Get(root.MaxCredentialsPerUser, tenantOverride?.MaxCredentialsPerUser),
            EnforceSignatureCounter: Get(root.EnforceSignatureCounter, tenantOverride?.EnforceSignatureCounter),
            ValidateAaguid: Get(root.ValidateAaguid, tenantOverride?.ValidateAaguid),
            AllowedAaguids: GetList(root.AllowedAaguids, tenantOverride?.AllowedAaguids),
            RelyingPartyId: rpId!,
            RelyingPartyName: GetStr(root.RelyingPartyName ?? "MrWhoOidc", tenantOverride?.RelyingPartyName),
            UserVerification: GetStr(root.UserVerification, tenantOverride?.UserVerification),
            ResidentKey: GetStr(root.ResidentKey, tenantOverride?.ResidentKey),
            AttestationConveyance: GetStr(root.AttestationConveyance, tenantOverride?.AttestationConveyance),
            AuthenticatorAttachment: GetStrNull(root.AuthenticatorAttachment, tenantOverride?.AuthenticatorAttachment),
            DefaultCredentialNamePattern: GetStr(root.DefaultCredentialNamePattern, tenantOverride?.DefaultCredentialNamePattern),
            AllowedAlgorithms: (hasOverride && tenantOverride?.AllowedCredentialAlgorithms is not null)
                ? tenantOverride!.AllowedCredentialAlgorithms!
                : root.AllowedCredentialAlgorithms,
            AllowedOrigins: allowedOrigins,
            RegistrationTimeoutSeconds: hasOverride && tenantOverride?.RegistrationTimeoutSeconds.HasValue == true
                ? tenantOverride!.RegistrationTimeoutSeconds!.Value
                : root.RegistrationTimeoutSeconds,
            AuthenticationTimeoutSeconds: hasOverride && tenantOverride?.AuthenticationTimeoutSeconds.HasValue == true
                ? tenantOverride!.AuthenticationTimeoutSeconds!.Value
                : root.AuthenticationTimeoutSeconds);
    }

    private static string? ParseUserVerification(string value) => value; // kept minimal; plain string passed through
    private static string? ParseResidentKey(string value) => value;
    private static string? ParseAttestation(string value) => value;
    private static string? ParseAttachment(string? value) => value;

    internal static string? ValidateAaguidPolicy(string? credentialAaguidBase64, bool validateAaguid, IReadOnlyList<string>? allowedAaguids)
    {
        var hasAllowlist = allowedAaguids is { Count: > 0 };
        if (!validateAaguid && !hasAllowlist)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(credentialAaguidBase64))
        {
            return "Authenticator AAGUID is required by WebAuthn policy";
        }

        if (!hasAllowlist)
        {
            return null;
        }

        var normalizedCredential = NormalizeAaguidValue(credentialAaguidBase64);
        foreach (var candidate in allowedAaguids!)
        {
            var normalizedCandidate = NormalizeAaguidValue(candidate);
            if (normalizedCandidate != null && normalizedCandidate == normalizedCredential)
            {
                return null;
            }
        }

        return "Authenticator is not permitted by AAGUID policy";
    }

    private static string? NormalizeAaguidValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Guid.TryParse(value.Trim(), out var guidValue))
        {
            return guidValue.ToString("D");
        }

        try
        {
            var bytes = Convert.FromBase64String(value.Trim());
            if (bytes.Length == 16)
            {
                return new Guid(bytes).ToString("D");
            }
        }
        catch
        {
            // Ignore parse errors and fall back to null.
        }

        return null;
    }

    private sealed record EffectiveWebAuthnOptions(
        bool Enabled,
        bool ExcludeExistingCredentials,
        bool AllowUsernamelessAuthentication,
        int ChallengeSessionLifetimeSeconds,
        int MaxCredentialsPerUser,
        bool EnforceSignatureCounter,
        bool ValidateAaguid,
        IReadOnlyList<string> AllowedAaguids,
        string RelyingPartyId,
        string RelyingPartyName,
        string UserVerification,
        string ResidentKey,
        string AttestationConveyance,
        string? AuthenticatorAttachment,
        string DefaultCredentialNamePattern,
        int[] AllowedAlgorithms,
        string[] AllowedOrigins,
        int RegistrationTimeoutSeconds,
        int AuthenticationTimeoutSeconds);
}

/// <summary>
/// Represents a cached WebAuthn challenge session.
/// </summary>
internal class WebAuthnChallengeSession
{
    public required byte[] Challenge { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public required Guid TenantId { get; set; }
    public required WebAuthnChallengeType Type { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Type of WebAuthn challenge.
/// </summary>
internal enum WebAuthnChallengeType
{
    Registration,
    Authentication
}
