using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class BucketizationTests
{
    [TestMethod]
    public void Bucket_MatchesOriginalLogic()
    {
        var input = "client-123";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 8));
        var actual = Bucketization.Bucket(input);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void BucketizeAudience_Host()
    {
        var aud = "https://api.example.com/v1";
        var bucket = Bucketization.BucketizeAudience(aud);
        Assert.AreEqual("api.example.com", bucket);
    }

    [TestMethod]
    public void BucketizeAudience_UrnTruncates()
    {
        var aud = "urn:example:resource:sub:extra";
        var bucket = Bucketization.BucketizeAudience(aud);
        Assert.AreEqual("urn:example:resource", bucket);
    }
}
