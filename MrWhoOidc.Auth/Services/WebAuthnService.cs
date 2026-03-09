using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for handling WebAuthn/FIDO2 operations including credential registration and authentication.
/// </summary>
internal sealed class WebAuthnService : IWebAuthnService
{
    private readonly AuthDbContext _db;
    private readonly IFido2 _fido2;
    private readonly HybridCache _cache;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IOptions<WebAuthnOptions> _options;
    private readonly ILogger<WebAuthnService> _logger;

    public WebAuthnService(
        AuthDbContext db,
        IFido2 fido2,
        HybridCache cache,
        ITenantAccessor tenantAccessor,
        IOptions<WebAuthnOptions> options,
        ILogger<WebAuthnService> logger)
    {
        _db = db;
        _fido2 = fido2;
        _cache = cache;
        _tenantAccessor = tenantAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task<(CredentialCreateOptions options, string sessionId)> CreateRegistrationChallengeAsync(
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
        
        // Get existing credentials to exclude them from new registration
        var shouldExcludeExisting = excludeCredentials && effectiveOptions.ExcludeExistingCredentials;
        var existingCredentials = shouldExcludeExisting
            ? await GetUserCredentialDescriptorsAsync(user.Id, cancellationToken)
            : Array.Empty<PublicKeyCredentialDescriptor>();

        var fidoUser = new Fido2User
        {
            Name = user.Username,
            Id = Encoding.UTF8.GetBytes(user.Id.ToString()),
            DisplayName = user.Name ?? user.Username
        };

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = effectiveOptions.ResidentKey,
                UserVerification = effectiveOptions.UserVerification,
                AuthenticatorAttachment = effectiveOptions.AuthenticatorAttachment
            },
            AttestationPreference = effectiveOptions.AttestationConveyance,
            ExcludeCredentials = existingCredentials,
            Extensions = new AuthenticationExtensionsClientInputs()
        });

        // Cache the challenge session
        var cacheKey = $"webauthn_registration_{sessionId}";
        var cacheOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds)
        };
        
        await _cache.SetAsync(cacheKey, new WebAuthnChallengeSession
        {
            Challenge = options.Challenge,
            Options = options,
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
        AuthenticatorAttestationRawResponse attestationResponse,
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
                async (_, ct) => (WebAuthnChallengeSession?)null, // Factory returns null if not cached
                cancellationToken: cancellationToken);
            
            if (session == null)
            {
                return (false, null, "Registration session not found or expired");
            }

            if (session.UserId != user.Id || session.TenantId != user.TenantId)
            {
                return (false, null, "Invalid session for user");
            }

            if (session.Options == null)
            {
                return (false, null, "Invalid session options");
            }

            var activeCredentialCount = await _db.WebAuthnCredentials
                .CountAsync(c => c.UserId == user.Id && c.TenantId == user.TenantId && c.IsActive, cancellationToken);
            if (activeCredentialCount >= effectiveOptions.MaxCredentialsPerUser)
            {
                return (false, null, $"Maximum of {effectiveOptions.MaxCredentialsPerUser} WebAuthn credentials per user reached");
            }

            // Make the new credential using the Fido2 service
            var credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = session.Options,
                IsCredentialIdUniqueToUserCallback = IsCredentialIdUniqueToUserAsync
            }, cancellationToken);

            var (attestationType, aaguidBase64) = ExtractAttestationMetadata(credential);

            // Store the credential in the database
            var webAuthnCredential = new WebAuthnCredential
            {
                Id = GuidHelper.NewId(),
                TenantId = user.TenantId,
                UserId = user.Id,
                CredentialId = Convert.ToBase64String(credential.Id),
                PublicKey = Convert.ToBase64String(credential.PublicKey),
                Type = credential.Type.ToString(),
                AttestationType = attestationType,
                AaguidBase64 = aaguidBase64,
                SignatureCounter = credential.SignCount,
                Transport = credential.Transports != null ? string.Join(",", credential.Transports) : null,
                FriendlyName = string.IsNullOrWhiteSpace(friendlyName)
                    ? GetDefaultCredentialName(effectiveOptions.DefaultCredentialNamePattern)
                    : friendlyName,
                DeviceType = GetDeviceTypeFromTransports(credential.Transports),
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
        catch (Fido2VerificationException ex)
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

    public async Task<(AssertionOptions options, string sessionId)> CreateAuthenticationChallengeAsync(
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

        // Get allowed credentials for the user (if username provided) or all credentials for usernameless flow
        var allowedCredentials = username != null
            ? await GetCredentialsForUserAsync(username, tenantId, cancellationToken)
            : await GetAllTenantCredentialsAsync(tenantId, cancellationToken);

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = effectiveOptions.UserVerification,
            Extensions = new AuthenticationExtensionsClientInputs()
        });

        // Cache the challenge session
        var cacheKey = $"webauthn_authentication_{sessionId}";
        var cacheOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(effectiveOptions.ChallengeSessionLifetimeSeconds)
        };
        
        await _cache.SetAsync(cacheKey, new WebAuthnChallengeSession
        {
            Challenge = options.Challenge,
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
        AuthenticatorAssertionRawResponse assertionResponse,
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
                async (_, ct) => (WebAuthnChallengeSession?)null, // Factory returns null if not cached
                cancellationToken: cancellationToken);
            
            if (session == null)
            {
                return (false, null, "Authentication session not found or expired");
            }

            // Find the credential used for authentication
            var credentialId = Convert.ToBase64String(assertionResponse.RawId);
            var credential = await _db.WebAuthnCredentials
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId && 
                                         c.TenantId == session.TenantId && 
                                         c.IsActive, cancellationToken);

            if (credential == null)
            {
                return (false, null, "Credential not found");
            }

            // Create assertion options for verification
            var allowedCredentials = new List<PublicKeyCredentialDescriptor>
            {
                new(Convert.FromBase64String(credentialId))
            };

            var assertionOptions = new AssertionOptions
            {
                Challenge = session.Challenge,
                RpId = effectiveOptions.RelyingPartyId,
                AllowCredentials = allowedCredentials
            };

            // Verify the assertion using the Fido2 service
            var verificationResult = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = assertionOptions,
                StoredPublicKey = Convert.FromBase64String(credential.PublicKey),
                StoredSignatureCounter = effectiveOptions.EnforceSignatureCounter ? credential.SignatureCounter : 0,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, ct) =>
                {
                    // Verify that the user handle matches the credential owner
                    if (args.UserHandle != null)
                    {
                        var userIdFromHandle = Encoding.UTF8.GetString(args.UserHandle);
                        return userIdFromHandle == credential.UserId.ToString();
                    }
                    return true; // Allow null user handle for resident credentials
                }
            }, cancellationToken);

            // Update signature counter and last used timestamp
            credential.SignatureCounter = verificationResult.SignCount;
            credential.LastUsedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            // Clear the session
            await _cache.RemoveAsync(cacheKey, cancellationToken);

            _logger.LogInformation("Successful WebAuthn authentication for user {UserId} using credential {CredentialId}",
                credential.UserId, credential.CredentialId);

            return (true, credential.User, null);
        }
        catch (Fido2VerificationException ex)
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

    private async Task<bool> IsCredentialIdUniqueToUserAsync(
        IsCredentialIdUniqueToUserParams args,
        CancellationToken cancellationToken)
    {
        var credentialId = Convert.ToBase64String(args.CredentialId);
        var exists = await _db.WebAuthnCredentials
            .AnyAsync(c => c.CredentialId == credentialId && c.IsActive, cancellationToken);
        
        return !exists; // Return true if credential ID is unique (doesn't exist)
    }

    private async Task<IReadOnlyList<PublicKeyCredentialDescriptor>> GetUserCredentialDescriptorsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var credentials = await GetUserCredentialsAsync(userId, cancellationToken);
        
        return credentials
            .Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId)))
            .ToList();
    }

    private async Task<IReadOnlyList<PublicKeyCredentialDescriptor>> GetCredentialsForUserAsync(
        string username,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var credentials = await _db.WebAuthnCredentials
            .Include(c => c.User)
            .Where(c => c.User.Username == username && 
                       c.TenantId == tenantId && 
                       c.IsActive)
            .ToListAsync(cancellationToken);

        return credentials
            .Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId)))
            .ToList();
    }

    private async Task<IReadOnlyList<PublicKeyCredentialDescriptor>> GetAllTenantCredentialsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var credentials = await _db.WebAuthnCredentials
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .ToListAsync(cancellationToken);

        return credentials
            .Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId)))
            .ToList();
    }

    private static AuthenticatorTransport[]? ParseTransports(string? transports)
    {
        if (string.IsNullOrEmpty(transports))
            return null;

        return transports.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Enum.TryParse<AuthenticatorTransport>(t.Trim(), true, out var transport) ? transport : (AuthenticatorTransport?)null)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToArray();
    }

    private string GetDefaultCredentialName(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return $"Security Key - {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}";

        return pattern.Replace("{datetime:yyyy-MM-dd HH:mm}", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal)
            .Replace("{datetime}", DateTimeOffset.UtcNow.ToString("u"), StringComparison.Ordinal)
            .Replace("{deviceType}", "security-key", StringComparison.Ordinal)
            .Replace("{transport}", "unknown", StringComparison.Ordinal);
    }

    private static string? GetDeviceTypeFromTransports(AuthenticatorTransport[]? transports)
    {
        if (transports?.Contains(AuthenticatorTransport.Internal) == true)
            return "platform";
        if (transports?.Any(t => t == AuthenticatorTransport.Usb || t == AuthenticatorTransport.Nfc || t == AuthenticatorTransport.Ble) == true)
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

        var enabled = hasOverride && tenantOverride!.Enabled.HasValue
            ? tenantOverride.Enabled.Value
            : root.Enabled;
        var excludeExisting = hasOverride && tenantOverride!.ExcludeExistingCredentials.HasValue
            ? tenantOverride.ExcludeExistingCredentials.Value
            : root.ExcludeExistingCredentials;
        var allowUsernameless = hasOverride && tenantOverride!.AllowUsernamelessAuthentication.HasValue
            ? tenantOverride.AllowUsernamelessAuthentication.Value
            : root.AllowUsernamelessAuthentication;
        var challengeLifetime = root.ChallengeSessionLifetimeSeconds;
        var maxCredentials = hasOverride && tenantOverride!.MaxCredentialsPerUser.HasValue
            ? tenantOverride.MaxCredentialsPerUser.Value
            : root.MaxCredentialsPerUser;
        var enforceSignatureCounter = hasOverride && tenantOverride!.EnforceSignatureCounter.HasValue
            ? tenantOverride.EnforceSignatureCounter.Value
            : root.EnforceSignatureCounter;

        var rpId = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.RelyingPartyId)
            ? tenantOverride.RelyingPartyId!
            : (string.IsNullOrWhiteSpace(root.RelyingPartyId) ? "localhost" : root.RelyingPartyId);

        var userVerificationRaw = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.UserVerification)
            ? tenantOverride.UserVerification!
            : root.UserVerification;
        var residentKeyRaw = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.ResidentKey)
            ? tenantOverride.ResidentKey!
            : root.ResidentKey;
        var attestationRaw = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.AttestationConveyance)
            ? tenantOverride.AttestationConveyance!
            : root.AttestationConveyance;
        var attachmentRaw = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.AuthenticatorAttachment)
            ? tenantOverride.AuthenticatorAttachment
            : root.AuthenticatorAttachment;
        var namePattern = hasOverride && !string.IsNullOrWhiteSpace(tenantOverride!.DefaultCredentialNamePattern)
            ? tenantOverride.DefaultCredentialNamePattern!
            : root.DefaultCredentialNamePattern;

        return new EffectiveWebAuthnOptions(
            Enabled: enabled,
            ExcludeExistingCredentials: excludeExisting,
            AllowUsernamelessAuthentication: allowUsernameless,
            ChallengeSessionLifetimeSeconds: challengeLifetime,
            MaxCredentialsPerUser: maxCredentials,
            EnforceSignatureCounter: enforceSignatureCounter,
            RelyingPartyId: rpId!,
            UserVerification: ParseUserVerification(userVerificationRaw),
            ResidentKey: ParseResidentKey(residentKeyRaw),
            AttestationConveyance: ParseAttestation(attestationRaw),
            AuthenticatorAttachment: ParseAttachment(attachmentRaw),
            DefaultCredentialNamePattern: namePattern);
    }

    private static UserVerificationRequirement ParseUserVerification(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "required" => UserVerificationRequirement.Required,
            "discouraged" => UserVerificationRequirement.Discouraged,
            _ => UserVerificationRequirement.Preferred
        };
    }

    private static ResidentKeyRequirement ParseResidentKey(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "required" => ResidentKeyRequirement.Required,
            "discouraged" => ResidentKeyRequirement.Discouraged,
            _ => ResidentKeyRequirement.Preferred
        };
    }

    private static AttestationConveyancePreference ParseAttestation(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "indirect" => AttestationConveyancePreference.Indirect,
            "direct" => AttestationConveyancePreference.Direct,
            "enterprise" => AttestationConveyancePreference.Enterprise,
            _ => AttestationConveyancePreference.None
        };
    }

    private static AuthenticatorAttachment? ParseAttachment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "platform" => AuthenticatorAttachment.Platform,
            "cross-platform" => AuthenticatorAttachment.CrossPlatform,
            _ => null
        };
    }

    private static (string? attestationType, string? aaguidBase64) ExtractAttestationMetadata(object credential)
    {
        var attestationType = GetStringProperty(credential, "AttestationFormat")
            ?? GetStringProperty(credential, "AttestationType")
            ?? GetStringProperty(credential, "Fmt");

        var aaguidBase64 = TryGetAaguidBase64(credential);

        // Some Fido2 models expose attestation metadata on a nested result object.
        var result = GetObjectProperty(credential, "Result");
        if (result != null)
        {
            attestationType ??= GetStringProperty(result, "AttestationFormat")
                ?? GetStringProperty(result, "AttestationType")
                ?? GetStringProperty(result, "Fmt");

            aaguidBase64 ??= TryGetAaguidBase64(result);
        }

        return (attestationType, aaguidBase64);
    }

    private static string? TryGetAaguidBase64(object source)
    {
        var value = GetObjectProperty(source, "AaGuid")
            ?? GetObjectProperty(source, "Aaguid")
            ?? GetObjectProperty(source, "AAGUID");

        if (value is null)
            return null;

        if (value is byte[] bytes && bytes.Length > 0)
            return Convert.ToBase64String(bytes);

        if (value is Guid guid && guid != Guid.Empty)
            return Convert.ToBase64String(guid.ToByteArray());

        if (value is string str && Guid.TryParse(str, out var parsedGuid))
            return Convert.ToBase64String(parsedGuid.ToByteArray());

        return null;
    }

    private static object? GetObjectProperty(object source, string propertyName)
    {
        var prop = source.GetType().GetProperty(propertyName);
        return prop?.GetValue(source);
    }

    private static string? GetStringProperty(object source, string propertyName)
    {
        var value = GetObjectProperty(source, propertyName);
        return value as string;
    }

    private sealed record EffectiveWebAuthnOptions(
        bool Enabled,
        bool ExcludeExistingCredentials,
        bool AllowUsernamelessAuthentication,
        int ChallengeSessionLifetimeSeconds,
        int MaxCredentialsPerUser,
        bool EnforceSignatureCounter,
        string RelyingPartyId,
        UserVerificationRequirement UserVerification,
        ResidentKeyRequirement ResidentKey,
        AttestationConveyancePreference AttestationConveyance,
        AuthenticatorAttachment? AuthenticatorAttachment,
        string DefaultCredentialNamePattern);
}

/// <summary>
/// Represents a cached WebAuthn challenge session.
/// </summary>
internal class WebAuthnChallengeSession
{
    public required byte[] Challenge { get; set; }
    public CredentialCreateOptions? Options { get; set; }
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