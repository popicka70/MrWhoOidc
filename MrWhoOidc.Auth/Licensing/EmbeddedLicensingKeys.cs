namespace MrWhoOidc.Auth.Licensing;

internal static class EmbeddedLicensingKeys
{
    // Primary licensing public key compiled into the assembly to validate issued tokens.
    public const string PrimaryPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEy1QDfXjTcxgIzqvTMSD4lINslz33
+VNv3p7FmTpn79UhyQ3x5UqudN81WQi0XVYVGtEETtADyJcgbDSeYNC3rA==
-----END PUBLIC KEY-----
""";
}
