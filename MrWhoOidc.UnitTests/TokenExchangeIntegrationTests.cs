using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore.Diagnostics;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres")]
public sealed class TokenExchangeIntegrationTests
{
    private const string Issuer = "https://test";
    private static readonly Guid DefaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");

    private sealed record TestHostBundle(IHost Host, string ClientId, string ClientSecret, Guid UserId);

    private static async Task<TestHostBundle> CreateHostAsync(Action<ClientEntity>? configureClient = null, Action<AuthOptions>? configureOptions = null, Action<PlatformSettings>? configurePlatformSettings = null)
    {
        var dbName = "te-integ-" + Guid.NewGuid().ToString("N");
        var clientId = "app1";
        var clientSecret = "secret";
        var userId = Guid.NewGuid();

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
                    // Core auth services (TokenService, JwtService, etc.)
                    services.AddMrWhoOidcAuthCore();
                    services.AddSingleton<IFeatureService, StubFeatureService>();
                    services.AddSingleton<IAuditSink, NoopAuditSink>();

                    // Override ITenantAccessor with test implementation that automatically sets default tenant
                    services.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor>(sp =>
                    {
                        var db = sp.GetRequiredService<AuthDbContext>();
                        var logger = sp.GetService<ILogger<MrWhoOidc.UnitTests.Testing.TestTenantAccessor>>();
                        return new MrWhoOidc.UnitTests.Testing.TestTenantAccessor(db, DefaultTenantId, logger);
                    });

                    // WebAuth endpoint dependencies
                    services.AddSingleton<OidcEndpointMetrics>();
                    services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcEndpointMetrics>());
                    services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
                    services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
                    services.AddScoped<IClientAuthenticator, ClientAuthenticator>();
                    services.AddSingleton<MrWhoOidc.Security.IDPoPValidator, TestCryptoDpopValidator>();
                    services.AddSingleton<MrWhoOidc.Security.IDPoPReplayCache, MrWhoOidc.Security.InMemoryDPoPReplayCache>();
                    services.AddScoped<MrWhoOidc.WebAuth.Handlers.ITokenHandler, MrWhoOidc.WebAuth.Handlers.TokenHandler>();
                    // Register grant handlers explicitly for strategy dispatch
                    services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.AuthorizationCodeGrantHandler>();
                    services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.RefreshTokenGrantHandler>();
                    services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.ClientCredentialsGrantHandler>();
                    services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.TokenExchangeGrantHandler>();
                    services.AddSingleton<MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.ITokenExchangeRateLimiter, MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.InMemoryTokenExchangeRateLimiter>();
                    services.Configure<MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.TokenExchangeRateLimitOptions>(o => { o.Enabled = true; o.PerClientPerMinute = 60; });
                    services.AddSingleton(new OidcOptions { Issuer = Issuer });
                    services.Configure<AuthOptions>(o =>
                    {
                        o.EnableTokenExchange = true;
                        o.ApiAudiences = new[] { "api-a", "api-b", "api-c" };
                        o.OpaqueAccessTokens.Enabled = false; // JWT for easier assertions (override per-test with configureOptions)
                        configureOptions?.Invoke(o);
                    });
                });
                webBuilder.Configure(async app =>
                {
                    // Seed minimal data
                    using (var scope = app.ApplicationServices.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                        var hasher = new TestPasswordHasher();

                        // Seed default tenant
                        var tenant = new Tenant
                        {
                            Id = DefaultTenantId,
                            Slug = "default",
                            Name = "Default Tenant",
                            IssuerUri = Issuer,
                            Status = TenantStatus.Active,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        db.Tenants.Add(tenant);

                        if (configurePlatformSettings != null)
                        {
                            var platformSettings = new PlatformSettings();
                            configurePlatformSettings(platformSettings);
                            db.PlatformSettings.Add(platformSettings);
                        }

                        var realm = new Realm { Name = "default", TenantId = DefaultTenantId };
                        db.Realms.Add(realm);
                        var client = new ClientEntity
                        {
                            ClientId = clientId,
                            ClientName = "App1",
                            ClientSecretHash = hasher.Hash(clientSecret),
                            RealmId = realm.Id,
                            TenantId = DefaultTenantId,
                            OboEnabled = true,
                            // Allow target audience api-b only by policy
                            OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api-b" }),
                            // No allowed source audience restriction
                            OboAllowedScopesJson = null,
                            OboMaxLifetimeMinutes = 3 // cap 3 minutes
                        };
                        configureClient?.Invoke(client);
                        db.Clients.Add(client);

                        db.Users.Add(new User { Id = userId, Username = "bob", Name = "Bob", TenantId = DefaultTenantId });
                        await db.SaveChangesAsync();
                    }

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx));
                    });
                });
            });

        var host = await builder.StartAsync();
        return new TestHostBundle(host, clientId, clientSecret, userId);
    }

    private static async Task<string> CreateSubjectJwtAsync(IHost host, Guid userId, string audience, string scopes, TimeSpan? lifetime = null, string? cnfJkt = null)
    {
        using var scope = host.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var exp = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10));
        var claimList = new List<Claim> { new Claim("sub", userId.ToString()), new Claim("scope", scopes) };
        if (!string.IsNullOrEmpty(cnfJkt))
        {
            var cnfJson = JsonSerializer.Serialize(new { jkt = cnfJkt });
            claimList.Add(new Claim("cnf", cnfJson));
        }
        return await jwt.CreateJwtAsync(Issuer, audience, claimList, exp);
    }

    private static AuthenticationHeaderValue Basic(string id, string secret)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    private sealed class StubFeatureService : IFeatureService
    {
        public Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            IReadOnlySet<string> enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                FeatureFlags.TokenExchange,
                FeatureFlags.BasicOidc,
                FeatureFlags.BasicAdminUi,
                FeatureFlags.AdvancedSecurity,
                FeatureFlags.DPoP
            };
            return Task.FromResult(enabled);
        }

        public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(Guid? tenantId = null, string? featureName = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
    }

    [TestMethod]
    public async Task TokenExchange_HappyPath_JwtSubject_ToAllowedTarget()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host; // ensure proper disposal after test
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write").ConfigureAwait(false);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Bearer", doc.GetProperty("token_type").GetString());
        Assert.AreEqual("urn:ietf:params:oauth:token-type:access_token", doc.GetProperty("issued_token_type").GetString());
        Assert.AreEqual("read", doc.GetProperty("scope").GetString());
        var expiresIn = doc.GetProperty("expires_in").GetInt32();
        Assert.IsTrue(expiresIn > 0 && expiresIn <= 180, $"expires_in out of expected cap: {expiresIn}");
        var access = doc.GetProperty("access_token").GetString();
        Assert.IsNotNull(access);
        Assert.HasCount(3, access!.Split('.'), "Expected JWT access token");
    }

    [TestMethod]
    public async Task TokenExchange_InvalidTarget_ByPolicy()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write").ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-c" // not in client's allowed target list (only api-b allowed)
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_target", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_Insufficient_Scope()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read").ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "write" // not present in subject scopes
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("insufficient_scope", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_ReturnsUnsupportedGrantType_WhenPlatformSettingDisabled()
    {
        var bundle = await CreateHostAsync(configurePlatformSettings: s => s.EnableTokenExchange = false);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write").ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("unsupported_grant_type", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_WithDPoP_AthBound_Succeeds()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write").ConfigureAwait(false);

        // Create DPoP header with ath bound to subject token
        var key = GenerateDpopKey(); // Generate DPoP key
        var dpop = CreateDpopProof(key, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Remove("DPoP");
        client.DefaultRequestHeaders.Add("DPoP", dpop);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Bearer", doc.GetProperty("token_type").GetString());
    }

    // === DPoP test helpers (crypto-backed) ===
    private sealed record DpopKey(ECDsa Key, string Crv, string X, string Y, string Jkt);

    private static DpopKey GenerateDpopKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(false);
        var x = Base64UrlEncoder.Encode(p.Q.X!);
        var y = Base64UrlEncoder.Encode(p.Q.Y!);
        var jkt = ComputeJwkThumbprint("EC", ("crv", "P-256"), ("x", x), ("y", y));
        return new DpopKey(ecdsa, "P-256", x, y, jkt);
    }

    private static string CreateDpopProof(DpopKey key, string method, string htu, string? accessToken = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<System.Security.Claims.Claim>
        {
            new("htm", method),
            new("htu", htu),
            new("iat", now.ToString()),
            new("jti", jti)
        };
        if (!string.IsNullOrEmpty(accessToken))
        {
            var ath = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64Url(accessToken);
            claims.Add(new("ath", ath));
        }

        var securityKey = new ECDsaSecurityKey(key.Key);
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);

        // Build header with jwk and typ=dpop+jwt
        var header = new JwtHeader(creds);
        header["typ"] = "dpop+jwt";
        header["jwk"] = new Dictionary<string, object>
        {
            {"kty", "EC"},
            {"crv", key.Crv},
            {"x", key.X},
            {"y", key.Y}
        };

        var token = new JwtSecurityToken(header, new JwtPayload(claims));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class TestCryptoDpopValidator : MrWhoOidc.Security.IDPoPValidator
    {
        private static readonly string[] AllowedAlgs = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256];

        public Task<MrWhoOidc.Security.DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        {
            var header = http.Request.Headers["DPoP"].ToString();
            if (string.IsNullOrWhiteSpace(header))
            {
                Console.WriteLine("[TestCryptoDpopValidator] missing DPoP header");
                return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "missing_dpop"));
            }

            try
            {
                var handler = new JwtSecurityTokenHandler();
                var unsigned = handler.ReadJwtToken(header);
                // Some libraries may not map 'typ' into Header.Typ; we won't strictly enforce it in tests.

                // Extract jwk from header, and immediately materialize key and thumbprint while JSON doc is alive
                SecurityKey? key = null;
                string? jktStr = null;
                var headerJson = Base64UrlEncoder.DecodeBytes(unsigned.EncodedHeader);
                using (var hdr = JsonDocument.Parse(headerJson))
                {
                    if (!hdr.RootElement.TryGetProperty("jwk", out var jwkElement))
                        return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "missing_jwk"));
                    key = CreateKeyFromJwk(jwkElement);
                    if (key is null)
                    {
                        Console.WriteLine("[TestCryptoDpopValidator] unsupported_jwk");
                        return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "unsupported_jwk"));
                    }
                    jktStr = ComputeJwkThumbprintFromElement(jwkElement);
                }

                var parameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateAudience = false,
                    ValidateIssuer = false,
                    ValidateLifetime = false,
                    ValidAlgorithms = AllowedAlgs
                };

                handler.ValidateToken(header, parameters, out var validatedToken);
                var jwt = (JwtSecurityToken)validatedToken;

                // Lax claim checks in tests: don't strictly enforce htm/htu/iat window here
                var jti = jwt.Payload.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
                long? iatSec = null;
                if (jwt.Payload.TryGetValue("iat", out var iatObj) && long.TryParse(iatObj?.ToString(), out var i)) iatSec = i;

                if (!string.IsNullOrEmpty(accessToken))
                {
                    var ath = jwt.Payload.TryGetValue("ath", out var athObj) ? athObj?.ToString() : null;
                    if (string.IsNullOrEmpty(ath))
                    {
                        Console.WriteLine("[TestCryptoDpopValidator] missing ath");
                        return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "missing_ath"));
                    }
                    var tokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
                    var tokenHashB64Url = Base64UrlEncoder.Encode(tokenHash);
                    if (!string.Equals(ath, tokenHashB64Url, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"[TestCryptoDpopValidator] ath_mismatch expected={tokenHashB64Url} got={ath}");
                        return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "ath_mismatch"));
                    }
                }

                return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(true, jktStr, jti, iatSec, null, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TestCryptoDpopValidator] exception: " + ex);
                return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, ex.Message));
            }
        }

        private static SecurityKey? CreateKeyFromJwk(JsonElement jwk)
        {
            if (!jwk.TryGetProperty("kty", out var ktyEl)) return null;
            var kty = ktyEl.GetString();
            if (kty == "EC")
            {
                if (!jwk.TryGetProperty("crv", out var crvEl) || !jwk.TryGetProperty("x", out var xEl) || !jwk.TryGetProperty("y", out var yEl)) return null;
                var crv = crvEl.GetString();
                var x = Base64UrlEncoder.DecodeBytes(xEl.GetString());
                var y = Base64UrlEncoder.DecodeBytes(yEl.GetString());
                var ecParams = new ECParameters
                {
                    Q = new ECPoint { X = x, Y = y },
                    Curve = crv switch
                    {
                        "P-256" => ECCurve.NamedCurves.nistP256,
                        "P-384" => ECCurve.NamedCurves.nistP384,
                        "P-521" => ECCurve.NamedCurves.nistP521,
                        _ => ECCurve.NamedCurves.nistP256
                    }
                };
                var ecdsa = ECDsa.Create();
                ecdsa.ImportParameters(ecParams);
                return new ECDsaSecurityKey(ecdsa);
            }
            return null;
        }
    }

    private static string ComputeJwkThumbprint(string kty, params (string name, string value)[] attrs)
    {
        // RFC 7638 requires lexicographic order of members; we build string explicitly
        if (kty == "EC")
        {
            string crv = attrs.First(a => a.name == "crv").value;
            string x = attrs.First(a => a.name == "x").value;
            string y = attrs.First(a => a.name == "y").value;
            var json = "{\"crv\":\"" + crv + "\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var hash = SHA256.HashData(bytes);
            return Base64UrlEncoder.Encode(hash);
        }
        throw new NotSupportedException("Only EC keys supported in tests");
    }

    private static string ComputeJwkThumbprintFromElement(JsonElement jwk)
    {
        if (!jwk.TryGetProperty("kty", out var ktyEl)) return string.Empty;
        var kty = ktyEl.GetString();
        if (kty == "EC")
        {
            var crv = jwk.GetProperty("crv").GetString();
            var x = jwk.GetProperty("x").GetString();
            var y = jwk.GetProperty("y").GetString();
            return ComputeJwkThumbprint("EC", ("crv", crv!), ("x", x!), ("y", y!));
        }
        return string.Empty;
    }

    [TestMethod]
    public async Task TokenExchange_Bridging_RequireSameJkt_Mismatch_Rejects()
    {
        // Configure client to require same JKT
        var bundle = await CreateHostAsync(c => c.OboDpopMode = OboDpopMode.RequireSameJkt);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subjectKey = GenerateDpopKey();
        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read", cnfJkt: subjectKey.Jkt).ConfigureAwait(false);
        client.DefaultRequestHeaders.Remove("DPoP");
        // Send a proof with a different key/jkt
        var otherKey = GenerateDpopKey();
        var dpopMismatch = CreateDpopProof(otherKey, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Add("DPoP", dpopMismatch);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_dpop_proof", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_Bridging_RequireSameJkt_Match_BindsCnf()
    {
        var bundle = await CreateHostAsync(c => c.OboDpopMode = OboDpopMode.RequireSameJkt);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var keyMatch = GenerateDpopKey();
        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write", cnfJkt: keyMatch.Jkt).ConfigureAwait(false);
        client.DefaultRequestHeaders.Remove("DPoP");
        var dpopMatch = CreateDpopProof(keyMatch, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Add("DPoP", dpopMatch);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var access = doc.GetProperty("access_token").GetString();
        Assert.IsNotNull(access);
        var cnfJkt = TryGetJwtCnfJkt(access!);
        Assert.AreEqual(keyMatch.Jkt, cnfJkt, "Outgoing access token should be bound to the same JKT");
    }

    [TestMethod]
    public async Task TokenExchange_Bridging_AllowSameJktOnly_SubjectNotBound_Rejects()
    {
        var bundle = await CreateHostAsync(c => c.OboDpopMode = OboDpopMode.AllowSameJktOnly);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        // Subject without cnf
        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write").ConfigureAwait(false);
        client.DefaultRequestHeaders.Remove("DPoP");
        var anyKey = GenerateDpopKey();
        var dpopAny = CreateDpopProof(anyKey, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Add("DPoP", dpopAny);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_dpop_proof", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_Bridging_AllowSameJktOnly_Match_BindsCnf()
    {
        var bundle = await CreateHostAsync(c => c.OboDpopMode = OboDpopMode.AllowSameJktOnly);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var keyAllow = GenerateDpopKey();
        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read", cnfJkt: keyAllow.Jkt).ConfigureAwait(false);
        client.DefaultRequestHeaders.Remove("DPoP");
        var dpopAllow = CreateDpopProof(keyAllow, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Add("DPoP", dpopAllow);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var access = doc.GetProperty("access_token").GetString();
        var cnfJkt = TryGetJwtCnfJkt(access!);
        Assert.AreEqual(keyAllow.Jkt, cnfJkt);
    }

    [TestMethod]
    public async Task TokenExchange_Bridging_Deny_SubjectHasCnf_Rejects()
    {
        var bundle = await CreateHostAsync(c => c.OboDpopMode = OboDpopMode.Deny);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var keyDeny = GenerateDpopKey();
        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read", cnfJkt: keyDeny.Jkt).ConfigureAwait(false);
        client.DefaultRequestHeaders.Remove("DPoP");
        var dpopDeny = CreateDpopProof(keyDeny, "POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Add("DPoP", dpopDeny);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_dpop_proof", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_SourceAudience_Invalid_ByPolicy()
    {
        // Client only allows source audience api-a, but subject uses api-x
        var bundle = await CreateHostAsync(c => c.OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { "api-a" }));
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = await CreateSubjectJwtAsync(bundle.Host, bundle.UserId, audience: "api-x", scopes: "read write").ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_grant", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_OpaqueSubject_DelegationDepth_Enforced()
    {
        // Set max delegation depth = 1
        var bundle = await CreateHostAsync(c => c.OboMaxDelegationDepth = 1);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        // Insert opaque access token with depth=1 (already exchanged once)
        var subjectRaw = await InsertOpaqueAccessAsync(bundle.Host, bundle.UserId, clientId: bundle.ClientId, audience: "api-a", scopes: new[] { "read" }, depth: 1);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subjectRaw,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var payloadText = await resp.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(payloadText);
        var doc = parsed.RootElement;
        Assert.AreEqual("invalid_grant", doc.GetProperty("error").GetString(), $"payload: {payloadText}");
        Assert.IsTrue(doc.TryGetProperty("error_description", out var desc), $"payload: {payloadText}");
        Assert.AreEqual("max_delegation_depth_exceeded", desc.GetString(), $"payload: {payloadText}");
    }

    [TestMethod]
    public async Task TokenExchange_OpaqueSubject_AllowedDepth_Succeeds_And_Increments()
    {
        // Enable opaque access tokens and allow max delegation depth = 2
        var bundle = await CreateHostAsync(c => c.OboMaxDelegationDepth = 2, o => o.OpaqueAccessTokens.Enabled = true);
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        // Insert opaque subject at depth=1 (within allowed depth)
        var subjectRaw = await InsertOpaqueAccessAsync(bundle.Host, bundle.UserId, clientId: bundle.ClientId, audience: "api-a", scopes: new[] { "read" }, depth: 1);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subjectRaw,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Bearer", doc.GetProperty("token_type").GetString());
        Assert.AreEqual("urn:ietf:params:oauth:token-type:access_token", doc.GetProperty("issued_token_type").GetString());
        Assert.AreEqual("read", doc.GetProperty("scope").GetString());

        var access = doc.GetProperty("access_token").GetString();
        Assert.IsNotNull(access);
        // Opaque access token: should not be a JWT
        Assert.DoesNotContain('.', access!);

        // Verify stored token has DelegationDepth incremented to 2 and expected fields
        using var scope = bundle.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hash = Hash(access!);
        var stored = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(stored, "Persisted opaque token not found by hash");
        Assert.AreEqual(2, stored!.DelegationDepth);
        Assert.AreEqual("api-b", stored.Audience);
        Assert.AreEqual(bundle.ClientId, stored.ClientId);
        Assert.AreEqual(bundle.UserId, stored.UserId);
        // Scopes include 'read'
        Assert.IsTrue(stored.ScopesJson?.Contains("read") ?? false);
    }

    private static string? TryGetJwtCnfJkt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            static string Pad(string s) => s.Length % 4 == 2 ? s + "==" : (s.Length % 4 == 3 ? s + "=" : (s.Length % 4 == 1 ? s + "===" : s));
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1].Replace('-', '+').Replace('_', '/'))));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cnf", out var cnfEl)) return null;
            if (cnfEl.ValueKind == JsonValueKind.String)
            {
                using var cnfDoc = JsonDocument.Parse(cnfEl.GetString()!);
                if (cnfDoc.RootElement.TryGetProperty("jkt", out var jktEl)) return jktEl.GetString();
            }
            else if (cnfEl.ValueKind == JsonValueKind.Object)
            {
                if (cnfEl.TryGetProperty("jkt", out var jktEl)) return jktEl.GetString();
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> InsertOpaqueAccessAsync(IHost host, Guid userId, string clientId, string audience, string[] scopes, int depth)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var raw = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var hash = Hash(raw);
        var token = new Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = JsonSerializer.Serialize(scopes),
            Audience = audience,
            Jti = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            DelegationDepth = depth
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();
        return raw;
    }

    private static string Hash(string raw)
    {
        // Match production hashing in TokenService.Hash: Base64 of SHA-256 bytes
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(bytes);
    }
}

