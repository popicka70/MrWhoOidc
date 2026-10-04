using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using MrWhoOidc.WebAuth.Infrastructure.Pipeline;

namespace MrWhoOidc.UnitTests;

/// <summary>C12 of the 2026-10-04 assessment: X-Client-Cert was trusted from any peer.</summary>
[TestClass]
public sealed class ForwardedClientCertificateTests
{
    private static ForwardedHeadersOptions Trusted()
    {
        var o = new ForwardedHeadersOptions();
        o.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        return o;
    }

    [TestMethod]
    [DataRow("10.0.0.5", true)]
    [DataRow("172.20.1.1", true)]
    [DataRow("127.0.0.1", true)]
    [DataRow("::ffff:10.0.0.5", true)]
    [DataRow("203.0.113.9", false)]
    [DataRow("10.0.0.6", false)]
    public void IsTrustedPeer_OnlyConfiguredProxies(string peer, bool expected)
        => Assert.AreEqual(expected, ForwardedClientCertificate.IsTrustedPeer(IPAddress.Parse(peer), Trusted(), trustAllProxies: false));

    [TestMethod]
    public void IsTrustedPeer_NoProxyConfig_RejectsRemotePeers()
        => Assert.IsFalse(ForwardedClientCertificate.IsTrustedPeer(IPAddress.Parse("203.0.113.9"), null, trustAllProxies: false));

    [TestMethod]
    public void Parse_AcceptsBase64DerAndUrlEncodedPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var der = Convert.ToBase64String(cert.RawData);
        var pemEscaped = Uri.EscapeDataString(cert.ExportCertificatePem());

        Assert.AreEqual(cert.Thumbprint, ForwardedClientCertificate.Parse(der)!.Thumbprint);
        Assert.AreEqual(cert.Thumbprint, ForwardedClientCertificate.Parse(pemEscaped)!.Thumbprint);
        Assert.IsNull(ForwardedClientCertificate.Parse("not a certificate"));
    }
}
