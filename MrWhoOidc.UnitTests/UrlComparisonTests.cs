using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Utils;
using System.Collections.Generic;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class UrlComparisonTests
{
    [TestMethod]
    public void IsAllowed_ShouldRejectQueryParameters_IfStrictMatchingIsRequired()
    {
        var allowed = new[] { "https://client.com/callback" };
        var requested = "https://client.com/callback?s=evil";

        bool isAllowed = UrlComparison.IsAllowed(requested, allowed);

        // Current vulnerable behavior: returns true
        // Desired secure behavior: returns false
        Assert.IsFalse(isAllowed, "Query parameters should not be ignored in redirect_uri validation.");
    }

    [TestMethod]
    public void IsAllowed_ShouldRejectFragment_IfStrictMatchingIsRequired()
    {
        var allowed = new[] { "https://client.com/callback" };
        var requested = "https://client.com/callback#evil";

        bool isAllowed = UrlComparison.IsAllowed(requested, allowed);

        // Current vulnerable behavior: returns true
        // Desired secure behavior: returns false
        Assert.IsFalse(isAllowed, "Fragment should not be ignored in redirect_uri validation.");
    }

    // Third 2026-10-04 review: normalisation hid userinfo, fragments and dot-segments, while the raw requested value
    // is what the user is redirected to.
    [TestMethod]
    [DataRow("https://attacker@rp.example/cb")]
    [DataRow("https://rp.example/cb#frag")]
    [DataRow("https://rp.example/x/../cb")]
    [DataRow("https://rp.example/x/%2e%2e/cb")]
    [DataRow("https://rp.example/./cb")]
    public void IsAllowed_RejectsUserinfoFragmentsAndDotSegments(string requested)
        => Assert.IsFalse(UrlComparison.IsAllowed(requested, ["https://rp.example/cb"]));

    [TestMethod]
    [DataRow("https://rp.example/cb")]
    [DataRow("https://RP.example/cb")]
    [DataRow("https://rp.example:443/cb")]
    [DataRow("https://rp.example/cb/")]
    public void IsAllowed_AcceptsEquivalentForms(string requested)
        => Assert.IsTrue(UrlComparison.IsAllowed(requested, ["https://rp.example/cb"]));
}
