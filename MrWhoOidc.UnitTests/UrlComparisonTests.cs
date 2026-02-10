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
}
