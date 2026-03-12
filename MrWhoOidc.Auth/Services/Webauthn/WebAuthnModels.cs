using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Services.Webauthn;

// ── Options sent TO the browser ────────────────────────────────────────────

/// <summary>W3C PublicKeyCredentialCreationOptions – sent to the browser to start registration.</summary>
public sealed class WebAuthnRegistrationOptions
{
    [JsonPropertyName("rp")]
    public required WebAuthnRp Rp { get; init; }

    [JsonPropertyName("user")]
    public required WebAuthnUser User { get; init; }

    [JsonPropertyName("challenge")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public required byte[] Challenge { get; init; }

    [JsonPropertyName("pubKeyCredParams")]
    public required WebAuthnPubKeyParam[] PubKeyCredParams { get; init; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; init; } = 60000;

    [JsonPropertyName("attestation")]
    public string Attestation { get; init; } = "none";

    [JsonPropertyName("authenticatorSelection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebAuthnAuthenticatorSelection? AuthenticatorSelection { get; init; }

    [JsonPropertyName("excludeCredentials")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebAuthnCredentialDescriptor[]? ExcludeCredentials { get; init; }
}

/// <summary>W3C PublicKeyCredentialRequestOptions – sent to the browser to start authentication.</summary>
public sealed class WebAuthnAssertionOptions
{
    [JsonPropertyName("rpId")]
    public required string RpId { get; init; }

    [JsonPropertyName("challenge")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public required byte[] Challenge { get; init; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; init; } = 60000;

    [JsonPropertyName("userVerification")]
    public string UserVerification { get; init; } = "preferred";

    [JsonPropertyName("allowCredentials")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebAuthnCredentialDescriptor[]? AllowCredentials { get; init; }
}

public sealed class WebAuthnRp
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class WebAuthnUser
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public required byte[] Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }
}

public sealed class WebAuthnPubKeyParam
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "public-key";

    [JsonPropertyName("alg")]
    public int Alg { get; init; }
}

public sealed class WebAuthnAuthenticatorSelection
{
    [JsonPropertyName("residentKey")]
    public string ResidentKey { get; init; } = "preferred";

    [JsonPropertyName("requireResidentKey")]
    public bool RequireResidentKey { get; init; }

    [JsonPropertyName("userVerification")]
    public string UserVerification { get; init; } = "preferred";

    [JsonPropertyName("authenticatorAttachment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthenticatorAttachment { get; init; }
}

public sealed class WebAuthnCredentialDescriptor
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "public-key";

    [JsonPropertyName("id")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public required byte[] Id { get; init; }

    [JsonPropertyName("transports")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Transports { get; init; }
}

// ── Responses received FROM the browser ────────────────────────────────────

/// <summary>Browser response to navigator.credentials.create().</summary>
public sealed class WebAuthnAttestationResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("rawId")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? RawId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("response")]
    public WebAuthnAttestationResponseData? Response { get; init; }

    [JsonPropertyName("transports")]
    public string[]? Transports { get; init; }
}

public sealed class WebAuthnAttestationResponseData
{
    [JsonPropertyName("clientDataJSON")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? ClientDataJSON { get; init; }

    [JsonPropertyName("attestationObject")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? AttestationObject { get; init; }

    // Some browsers report transports here instead of the top-level response
    [JsonPropertyName("transports")]
    public string[]? Transports { get; init; }
}

/// <summary>Browser response to navigator.credentials.get().</summary>
public sealed class WebAuthnAssertionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("rawId")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? RawId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("response")]
    public WebAuthnAssertionResponseData? Response { get; init; }
}

public sealed class WebAuthnAssertionResponseData
{
    [JsonPropertyName("clientDataJSON")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? ClientDataJSON { get; init; }

    [JsonPropertyName("authenticatorData")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? AuthenticatorData { get; init; }

    [JsonPropertyName("signature")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    public byte[]? Signature { get; init; }

    [JsonPropertyName("userHandle")]
    [JsonConverter(typeof(Base64UrlByteArrayConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? UserHandle { get; init; }
}

// ── JSON converter ──────────────────────────────────────────────────────────

/// <summary>
/// JSON converter that serialises byte[] as base64url (no padding) and deserialises
/// both base64url (with - and _) and standard base64 (with + and / and =).
/// </summary>
public sealed class Base64UrlByteArrayConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("Expected base64url string, got null");
        return Decode(s);
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        => writer.WriteStringValue(Encode(value));

    internal static byte[] Decode(string base64Url)
    {
        // Accept both base64url (- _) and standard base64 (+ /)
        string base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        // Re-add padding if stripped
        base64 = (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            _ => base64
        };
        return Convert.FromBase64String(base64);
    }

    internal static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Thrown when WebAuthn registration or authentication verification fails.</summary>
public sealed class WebAuthnVerificationException : Exception
{
    public WebAuthnVerificationException(string message) : base(message) { }
}
