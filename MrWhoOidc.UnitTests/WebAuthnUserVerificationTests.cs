using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services.Webauthn;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C15 of the 2026-10-04 assessment: the UV flag was never checked, yet passkey logins claimed acr=passkey.
/// </summary>
[TestClass]
public sealed class WebAuthnUserVerificationTests
{
    private const string RpId = "login.example.com";
    private const string Origin = "https://login.example.com";
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;

    [TestMethod]
    public void Assertion_WithoutUv_RejectedWhenRequired()
    {
        var (args, _) = BuildAssertion(UserPresent);
        Assert.ThrowsExactly<WebAuthnVerificationException>(() => Verify(args, requireUv: true));
    }

    [TestMethod]
    public void Assertion_WithoutUv_AcceptedWhenNotRequired_ButReportedUnverified()
    {
        var (args, _) = BuildAssertion(UserPresent);
        var result = Verify(args, requireUv: false);
        Assert.IsFalse(result.UserVerified);
    }

    [TestMethod]
    public void Assertion_WithUv_ReportedVerified()
    {
        var (args, _) = BuildAssertion(UserPresent | UserVerified);
        var result = Verify(args, requireUv: true);
        Assert.IsTrue(result.UserVerified);
    }

    private sealed record AssertionArgs(byte[] ClientData, byte[] AuthData, byte[] Signature, byte[] CoseKey, byte[] Challenge);

    private static WebAuthnCrypto.AssertionResult Verify(AssertionArgs a, bool requireUv)
        => WebAuthnCrypto.VerifyAuthentication(a.ClientData, a.AuthData, a.Signature, userHandle: null, a.CoseKey,
            storedSignCount: 0, enforceSignatureCounter: false, a.Challenge, RpId, new[] { Origin }, requireUv);

    private static (AssertionArgs, ECDsa) BuildAssertion(int flags)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);    // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);   // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);   // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();

        var challenge = RandomNumberGenerator.GetBytes(32);
        var clientData = Encoding.UTF8.GetBytes(
            $"{{\"type\":\"webauthn.get\",\"challenge\":\"{Base64UrlEncoder.Encode(challenge)}\",\"origin\":\"{Origin}\"}}");

        var authData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(RpId)).CopyTo(authData, 0);
        authData[32] = (byte)flags;
        BinaryPrimitives.WriteUInt32BigEndian(authData.AsSpan(33), 1);

        var message = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (new AssertionArgs(clientData, authData, signature, cose.Encode(), challenge), key);
    }
}
