using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests.Observability;

[TestClass]
public sealed class DbAuditSinkTests
{
    [TestMethod]
    public void Emit_Persists_AuditEvent_With_Tenant_And_HashedActorIp()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ITenantAccessor, TenantAccessor>();

        var provider = services.BuildServiceProvider();
        var accessor = new HttpContextAccessor();

        using (var requestScope = provider.CreateScope())
        {
            var tenantAccessor = (TenantAccessor)requestScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
            var tenantId = Guid.NewGuid();
            tenantAccessor.SetTenant(new TenantContext
            {
                TenantId = tenantId,
                Slug = "test",
                Name = "Test Tenant",
                IssuerUri = "https://issuer.example.com"
            });

            var http = new DefaultHttpContext
            {
                RequestServices = requestScope.ServiceProvider,
                TraceIdentifier = "trace-123"
            };
            http.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));
            accessor.HttpContext = http;

            var sink = new DbAuditSink(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DbAuditSink>.Instance,
                Options.Create(new AuditOptions { Enabled = true, PersistToDatabase = true, Pepper = "pepper" }),
                accessor);

            sink.Emit("security.test", new { status = "ok", code = 200 });

            using var verifyScope = provider.CreateScope();
            var db = verifyScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var ev = db.AuditEvents.Single();

            Assert.AreEqual("security.test", ev.EventType);
            Assert.AreEqual(tenantId, ev.TenantId);
            Assert.AreEqual("trace-123", ev.TraceId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(ev.ActorHash));
            Assert.IsFalse(string.IsNullOrWhiteSpace(ev.IpHash));
            StringAssert.Contains(ev.PayloadJson, "\"status\":\"ok\"");
        }
    }
}
