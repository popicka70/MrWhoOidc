using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Services.Webauthn;

/// <summary>
/// Implements WebAuthn registration and authentication ceremony verification
/// using only .NET built-in APIs (System.Security.Cryptography, System.Formats.Cbor).
/// Supports ES256 (P-256 ECDSA) and RS256 (RSA PKCS#1 v1.5).
/// </summary>
internal static class WebAuthnCrypto
{
    // COSE algorithm identifiers (https://www.iana.org/assignments/cose/cose.xhtml)
    private const int AlgES256 = -7;   // ECDSA w/ SHA-256 over P-256
    private const int AlgRS256 = -257; // RSASSA-PKCS1-v1_5 w/ SHA-256

    // ── Public API ──────────────────────────────────────────────────────────

    internal sealed class RegistrationResult
    {
        public required byte[] CredentialId { get; init; }
        public required byte[] CosePublicKey { get; init; }
        public required uint SignCount { get; init; }
        public required byte[] AaGuid { get; init; }
        public required string? AttestationFormat { get; init; }
        public required string? Transports { get; init; }
    }

    /// <summary>
    /// Verifies a WebAuthn registration ceremony and extracts the new credential.
    /// </summary>
    internal static RegistrationResult VerifyRegistration(
        byte[] clientDataJson,
        byte[] attestationObject,
        string[]? transports,
        byte[] expectedChallenge,
        string rpId,
        IReadOnlyCollection<string> expectedOrigins)
    {
        // Step 1 – verify clientDataJSON
        var clientData = ParseClientData(clientDataJson);

        if (!string.Equals(clientData.Type, "webauthn.create", StringComparison.Ordinal))
            throw new WebAuthnVerificationException($"Invalid clientDataJSON type '{clientData.Type}', expected 'webauthn.create'");

        var challengeBytes = Base64UrlByteArrayConverter.Decode(clientData.Challenge);
        if (!CryptographicOperations.FixedTimeEquals(challengeBytes, expectedChallenge))
            throw new WebAuthnVerificationException("Challenge mismatch");

        if (!expectedOrigins.Contains(clientData.Origin, StringComparer.OrdinalIgnoreCase))
            throw new WebAuthnVerificationException($"Origin '{clientData.Origin}' is not in the allowed origins list");

        // Step 2 – parse attestationObject (CBOR)
        var (fmt, authData) = ParseAttestationObject(attestationObject);

        // Step 3 – parse authenticatorData binary structure
        var (rpIdHash, flags, signCount, aaguid, credentialId, coseKey) = ParseAuthData(authData);

        // Step 4 – verify RP ID hash
        var expectedRpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(rpIdHash, expectedRpIdHash))
            throw new WebAuthnVerificationException("rpIdHash mismatch");

        // Step 5 – user presence (bit 0 of flags) MUST be set
        if ((flags & 0x01) == 0)
            throw new WebAuthnVerificationException("User Presence flag is not set in authenticatorData");

        // Step 6 – AT flag: attestedCredentialData MUST be present for registration
        if ((flags & 0x40) == 0 || credentialId is null || coseKey is null)
            throw new WebAuthnVerificationException("attestedCredentialData is missing from authenticatorData");

        // Step 7 – attestation statement (we accept all formats; signature checking is
        //           only meaningful for "direct"/"enterprise" attestation which requires
        //           a metadata service; for "none" and "packed" self there is nothing extra to verify)
        _ = fmt; // recorded in the credential for audit purposes

        var combinedTransports = transports is { Length: > 0 } ? string.Join(",", transports) : null;

        return new RegistrationResult
        {
            CredentialId = credentialId,
            CosePublicKey = coseKey,
            SignCount = signCount,
            AaGuid = aaguid,
            AttestationFormat = fmt,
            Transports = combinedTransports
        };
    }

    internal sealed class AssertionResult
    {
        public required uint NewSignCount { get; init; }
        public required byte[]? UserHandle { get; init; }
    }

    /// <summary>
    /// Verifies a WebAuthn authentication assertion.
    /// </summary>
    internal static AssertionResult VerifyAuthentication(
        byte[] clientDataJson,
        byte[] authenticatorData,
        byte[] signature,
        byte[]? userHandle,
        byte[] storedCosePublicKey,
        uint storedSignCount,
        bool enforceSignatureCounter,
        byte[] expectedChallenge,
        string rpId,
        IReadOnlyCollection<string> expectedOrigins)
    {
        // Step 1 – verify clientDataJSON
        var clientData = ParseClientData(clientDataJson);

        if (!string.Equals(clientData.Type, "webauthn.get", StringComparison.Ordinal))
            throw new WebAuthnVerificationException($"Invalid clientDataJSON type '{clientData.Type}', expected 'webauthn.get'");

        var challengeBytes = Base64UrlByteArrayConverter.Decode(clientData.Challenge);
        if (!CryptographicOperations.FixedTimeEquals(challengeBytes, expectedChallenge))
            throw new WebAuthnVerificationException("Challenge mismatch");

        if (!expectedOrigins.Contains(clientData.Origin, StringComparer.OrdinalIgnoreCase))
            throw new WebAuthnVerificationException($"Origin '{clientData.Origin}' is not in the allowed origins list");

        // Step 2 – parse authenticatorData (assertion form: no attestedCredentialData)
        var (rpIdHash, flags, signCount, _, _, _) = ParseAuthData(authenticatorData);

        // Step 3 – verify RP ID hash
        var expectedRpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(rpIdHash, expectedRpIdHash))
            throw new WebAuthnVerificationException("rpIdHash mismatch");

        // Step 4 – user presence MUST be set
        if ((flags & 0x01) == 0)
            throw new WebAuthnVerificationException("User Presence flag is not set in authenticatorData");

        // Step 5 – verify the signature: sig is over authData || SHA-256(clientDataJSON)
        var clientDataHash = SHA256.HashData(clientDataJson);
        var message = Concat(authenticatorData, clientDataHash);
        VerifySignature(storedCosePublicKey, message, signature);

        // Step 6 – signature counter check (detect cloned authenticators)
        if (enforceSignatureCounter && storedSignCount > 0 && signCount <= storedSignCount)
            throw new WebAuthnVerificationException(
                "Signature counter did not increase — possible cloned authenticator");

        return new AssertionResult { NewSignCount = signCount, UserHandle = userHandle };
    }

    // ── CBOR / binary parsing ───────────────────────────────────────────────

    private static (string fmt, byte[] authData) ParseAttestationObject(byte[] data)
    {
        var reader = new CborReader(data, CborConformanceMode.Lax);
        reader.ReadStartMap();

        string? fmt = null;
        byte[]? authData = null;

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "fmt":
                    fmt = reader.ReadTextString();
                    break;
                case "authData":
                    authData = reader.ReadByteString();
                    break;
                case "attStmt":
                    reader.SkipValue(); // not verifying attStmt signature in this implementation
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();

        if (fmt is null) throw new WebAuthnVerificationException("'fmt' is missing from attestationObject");
        if (authData is null) throw new WebAuthnVerificationException("'authData' is missing from attestationObject");

        return (fmt, authData);
    }

    /// <summary>
    /// Parses the authenticatorData binary structure.
    /// Returns (rpIdHash[32], flags, signCount, aaguid[16], credentialId?, coseKey?).
    /// credentialId and coseKey are non-null only when the AT flag (bit 6) is set.
    /// </summary>
    private static (byte[] rpIdHash, byte flags, uint signCount, byte[] aaguid, byte[]? credentialId, byte[]? coseKey)
        ParseAuthData(byte[] authData)
    {
        if (authData.Length < 37)
            throw new WebAuthnVerificationException("authenticatorData is too short (expected ≥37 bytes)");

        var rpIdHash = authData[0..32];
        var flags = authData[32];
        var signCount = (uint)((authData[33] << 24) | (authData[34] << 16) | (authData[35] << 8) | authData[36]);

        byte[]? aaguid = null;
        byte[]? credentialId = null;
        byte[]? coseKey = null;

        var hasAt = (flags & 0x40) != 0; // attestedCredentialData present
        if (hasAt && authData.Length > 37)
        {
            var offset = 37;

            // AAGUID: 16 bytes
            if (offset + 16 > authData.Length)
                throw new WebAuthnVerificationException("authenticatorData truncated before AAGUID");
            aaguid = authData[offset..(offset + 16)];
            offset += 16;

            // credentialIdLength: 2 bytes big-endian
            if (offset + 2 > authData.Length)
                throw new WebAuthnVerificationException("authenticatorData truncated before credentialIdLength");
            var credentialIdLength = (ushort)((authData[offset] << 8) | authData[offset + 1]);
            offset += 2;

            // credentialId
            if (offset + credentialIdLength > authData.Length)
                throw new WebAuthnVerificationException("authenticatorData truncated before credentialId");
            credentialId = authData[offset..(offset + credentialIdLength)];
            offset += credentialIdLength;

            // COSE public key: remainder of authData (extensions, if any, follow the CBOR map
            // and are ignored by the CBOR reader since we use CborConformanceMode.Lax)
            coseKey = authData[offset..];
        }

        return (rpIdHash, flags, signCount, aaguid ?? Array.Empty<byte>(), credentialId, coseKey);
    }

    // ── Signature verification ──────────────────────────────────────────────

    private static void VerifySignature(byte[] cosePublicKey, byte[] message, byte[] signature)
    {
        var (kty, alg, x, y, n, e) = ParseCoseKey(cosePublicKey);

        switch (alg)
        {
            case AlgES256:
                {
                    if (x is null || y is null)
                        throw new WebAuthnVerificationException("EC2 COSE key is missing x or y coordinates");

                    var ecParams = new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = x, Y = y }
                    };
                    using var ecdsa = ECDsa.Create(ecParams);
                    // WebAuthn ES256 signatures are DER-encoded (Rfc3279DerSequence)
                    if (!ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                        throw new WebAuthnVerificationException("ES256 signature verification failed");
                    break;
                }

            case AlgRS256:
                {
                    if (n is null || e is null)
                        throw new WebAuthnVerificationException("RSA COSE key is missing modulus (n) or exponent (e)");

                    var rsaParams = new RSAParameters { Modulus = n, Exponent = e };
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(rsaParams);
                    if (!rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        throw new WebAuthnVerificationException("RS256 signature verification failed");
                    break;
                }

            default:
                throw new WebAuthnVerificationException(
                    $"Unsupported COSE algorithm {alg}. Only ES256 (-7) and RS256 (-257) are supported.");
        }
    }

    /// <summary>
    /// Parses a CBOR-encoded COSE key.
    /// Returns (kty, alg, x, y, n, e) where x/y are EC2 coordinates and n/e are RSA parameters.
    /// </summary>
    private static (int kty, int alg, byte[]? x, byte[]? y, byte[]? n, byte[]? e)
        ParseCoseKey(byte[] coseKeyBytes)
    {
        // The coseKeyBytes slice may include trailing extension bytes; CborConformanceMode.Lax
        // lets us read exactly one CBOR item without requiring the buffer to be fully consumed.
        var reader = new CborReader(coseKeyBytes, CborConformanceMode.Lax);
        reader.ReadStartMap();

        int kty = 0, alg = 0;
        byte[]? x = null, y = null, n = null, e = null;

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            // COSE key map uses integer labels
            int label;
            var state = reader.PeekState();
            if (state == CborReaderState.UnsignedInteger || state == CborReaderState.NegativeInteger)
            {
                label = reader.ReadInt32();
            }
            else
            {
                // Unexpected key type – skip key + value
                reader.SkipValue();
                reader.SkipValue();
                continue;
            }

            switch (label)
            {
                case 1:  // kty
                    kty = reader.ReadInt32();
                    break;
                case 3:  // alg
                    alg = reader.ReadInt32();
                    break;
                case -1: // crv (EC2 – integer) or n (RSA – bytes)
                    if (reader.PeekState() == CborReaderState.ByteString)
                        n = reader.ReadByteString(); // RSA: modulus
                    else
                        reader.ReadInt32();           // EC2: curve identifier (discard; inferred from alg)
                    break;
                case -2: // x (EC2 – bytes) or e (RSA – bytes)
                    var v2 = reader.ReadByteString();
                    x = v2; // EC2 x-coordinate
                    e = v2; // RSA exponent (only used if kty==3)
                    break;
                case -3: // y (EC2 – bytes)
                    y = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();

        // For RSA, -2 is the exponent (e), not x; disambiguate after reading all keys
        if (kty == 3 /* RSA */)
        {
            e = x; // label -2 was the exponent
            x = null;
        }

        return (kty, alg, x, y, n, e);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static WebAuthnClientData ParseClientData(byte[] clientDataJson)
    {
        var json = Encoding.UTF8.GetString(clientDataJson);
        return JsonSerializer.Deserialize<WebAuthnClientData>(json)
               ?? throw new WebAuthnVerificationException("Failed to parse clientDataJSON");
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private sealed class WebAuthnClientData
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("challenge")]
        public string Challenge { get; init; } = "";

        [JsonPropertyName("origin")]
        public string Origin { get; init; } = "";
    }
}
