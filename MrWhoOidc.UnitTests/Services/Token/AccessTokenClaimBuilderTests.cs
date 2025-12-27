using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class AccessTokenClaimBuilderTests
{
    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    [TestMethod]
    public async Task BuildClaimsAsync_Includes_Basic_Claims()
    {
        var scopeResolver = new MockScopeResolver();
        var roleBuilder = new Mock<IRoleClaimBuilder>();
        var builder = new AccessTokenClaimBuilder(scopeResolver, roleBuilder.Object, Options());

        var request = new AccessTokenClaimRequest(
            UserId: Guid.NewGuid(),
            ClientId: "c1",
            Scopes: new[] { "openid", "profile" },
            Issuer: "https://issuer"
        );

        var claims = await builder.BuildClaimsAsync(request, CancellationToken.None);
        var list = claims.ToList();

        Assert.IsTrue(list.Any(c => c.Type == "sub" && c.Value == request.UserId.ToString()));
        Assert.IsTrue(list.Any(c => c.Type == "scope" && c.Value == "openid profile"));
        Assert.IsTrue(list.Any(c => c.Type == "jti"));
    }
}
