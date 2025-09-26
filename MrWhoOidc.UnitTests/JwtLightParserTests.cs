using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class JwtLightParserTests
{
    // header: {"alg":"none"}
    // payload: {"aud":"api","sid":"abc123","sub":"user1"}
    private const string SampleJwt = "eyJhbGciOiJub25lIn0.eyJhdWQiOiJhcGkiLCJzaWQiOiJhYmMxMjMiLCJzdWIiOiJ1c2VyMSJ9.x";

    [TestMethod]
    public void IsProbablyJwt_DetectsFormat()
    {
        Assert.IsTrue(JwtLightParser.IsProbablyJwt(SampleJwt));
        Assert.IsFalse(JwtLightParser.IsProbablyJwt("not-a-jwt"));
    }

    [TestMethod]
    public void TryGetAudience_FindsAudience()
    {
        var aud = JwtLightParser.TryGetAudience(SampleJwt);
        Assert.AreEqual("api", aud);
    }

    [TestMethod]
    public void TryGetClaim_Sid()
    {
        var sid = JwtLightParser.TryGetClaim(SampleJwt, "sid");
        Assert.AreEqual("abc123", sid);
    }

    [TestMethod]
    public void TryGetClaim_Sub()
    {
        var sub = JwtLightParser.TryGetClaim(SampleJwt, "sub");
        Assert.AreEqual("user1", sub);
    }
}
