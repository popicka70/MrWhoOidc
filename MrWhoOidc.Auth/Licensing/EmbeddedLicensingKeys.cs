namespace MrWhoOidc.Auth.Licensing;

internal static class EmbeddedLicensingKeys
{
    // Primary licensing public key compiled into the assembly to validate issued tokens.
    public const string PrimaryPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExJk50yzVYlYceGmRMUys03tM1AFR
4umFuog3oT1oTSlYozrMfsbX0wDgSFOTeAUBWlSsxB4pgE1cyjjrB+VHTg==
-----END PUBLIC KEY-----
""";
}
