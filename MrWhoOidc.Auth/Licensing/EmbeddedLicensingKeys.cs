namespace MrWhoOidc.Auth.Licensing;

internal static class EmbeddedLicensingKeys
{
    // Primary licensing public key compiled into the assembly to validate issued tokens.
    public const string PrimaryPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAET+sK1gJtoS6M/ng/CfbT6Bu4bRQX
oe7mUcAhn1KquTioCGEl/tM3XeBSRhh5qDx9njU67C2fwvOWm1ay3mHKrg==
-----END PUBLIC KEY-----
""";
}
