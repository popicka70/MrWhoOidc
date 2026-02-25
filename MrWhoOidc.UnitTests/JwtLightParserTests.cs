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
    [DataRow(SampleJwt, true)]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("not-a-jwt", false)]
    [DataRow("one.dot", false)]
    [DataRow("two.dots.here", true)]
    [DataRow("three.dots.here.extra", true)]
    public void IsProbablyJwt_DetectsFormat(string? input, bool expected)
    {
        Assert.AreEqual(expected, JwtLightParser.IsProbablyJwt(input!));
    }

    [TestMethod]
    [DataRow(SampleJwt, "api")]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("no-dots", null)]
    [DataRow("one.dot", null)]
    [DataRow("a.b.c", null)] // invalid base64
    [DataRow("a.eyJhIjpifQ.c", null)] // invalid JSON
    [DataRow("a.eyJhIjoxfQ.c", null)] // valid JSON, missing aud
    [DataRow("a.eyJhdWQiOiJhcGkifQ.c", "api")] // valid aud string
    [DataRow("a.eyJhdWQiOlsibXktYXBpIl19.c", "my-api")] // valid aud array (1 element)
    [DataRow("a.eyJhdWQiOlsibXktYXBpMSIsICJteS1hcGkyIl19.c", "my-api1")] // valid aud array (2 elements)
    [DataRow("a.eyJhdWQiOltdfQ.c", null)] // empty aud array
    [DataRow("a.eyJhdWQiOjEyM30.c", null)] // aud as number
    public void TryGetAudience_VariousInputs(string? input, string? expected)
    {
        var aud = JwtLightParser.TryGetAudience(input!);
        Assert.AreEqual(expected, aud);
    }

    [TestMethod]
    [DataRow(SampleJwt, "sid", "abc123")]
    [DataRow(SampleJwt, "sub", "user1")]
    [DataRow(null, "sid", null)]
    [DataRow("", "sid", null)]
    [DataRow("one.dot", "sid", null)]
    [DataRow("three.dots.here.extra", "sid", null)]
    [DataRow("a.eyJhIjoibSJ9.c", "missing", null)]
    [DataRow("a.eyJhIjoxMjN9.c", "a", null)] // 'a' is a number
    public void TryGetClaim_VariousInputs(string? input, string claim, string? expected)
    {
        var val = JwtLightParser.TryGetClaim(input!, claim);
        Assert.AreEqual(expected, val);
    }
}
