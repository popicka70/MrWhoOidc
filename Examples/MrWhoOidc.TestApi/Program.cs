using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.DependencyInjection;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Options;

var builder = WebApplication.CreateBuilder(args);

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

builder.Services.AddMrWhoOidcClient(builder.Configuration, "MrWhoOidc");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IServiceProvider>((options, sp) =>
    {
        var clientOptions = sp.GetRequiredService<IOptionsMonitor<MrWhoOidcClientOptions>>().CurrentValue;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        options.RequireHttpsMetadata = clientOptions.RequireHttpsMetadata;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidIssuer = clientOptions.Issuer;

        var expectedAudience = clientOptions.Audience ?? clientOptions.Resource ?? "api";
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = expectedAudience;

        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.RequireSignedTokens = true;

        options.TokenValidationParameters.IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
        {
            var cache = sp.GetRequiredService<IMrWhoJwksCache>();
            var jwks = cache.GetAsync().AsTask().GetAwaiter().GetResult();
            IEnumerable<JsonWebKey> keys = jwks.Keys;
            if (!string.IsNullOrEmpty(kid))
            {
                keys = keys.Where(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
            }
            return keys.Cast<SecurityKey>();
        };

        options.Events ??= new JwtBearerEvents();
        options.Events.OnAuthenticationFailed = context =>
        {
            loggerFactory.CreateLogger("MrWhoOidc.TestApi.Authentication").LogWarning(context.Exception, "Bearer token validation failed.");
            return Task.CompletedTask;
        };
        options.Events.OnChallenge = context =>
        {
            if (context.AuthenticateFailure != null)
            {
                loggerFactory.CreateLogger("MrWhoOidc.TestApi.Authentication").LogWarning(context.AuthenticateFailure, "Bearer challenge triggered.");
            }
            return Task.CompletedTask;
        };
    });

var app = builder.Build();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/me", (ClaimsPrincipal user) =>
    {
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        var scopeClaim = user.FindFirst("scope")?.Value ?? string.Empty;
        var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actorJson = user.FindFirst("act")?.Value;
        string? actorClient = null;
        if (!string.IsNullOrEmpty(actorJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(actorJson);
                if (doc.RootElement.TryGetProperty("sub", out var subEl))
                {
                    actorClient = subEl.GetString();
                }
            }
            catch
            {
                actorClient = actorJson;
            }
        }

        return Results.Ok(new
        {
            subject = user.FindFirst("sub")?.Value,
            name = user.FindFirst("name")?.Value,
            email = user.FindFirst("email")?.Value,
            audience = user.FindFirst("aud")?.Value,
            scopes,
            actorClient,
            delegationId = user.FindFirst("delegation_id")?.Value,
            authorizedClient = user.FindFirst("client_id")?.Value ?? user.FindFirst("azp")?.Value,
            issuedAt = user.FindFirst("iat")?.Value,
            expiresAt = user.FindFirst("exp")?.Value
        });
    })
    .RequireAuthorization()
    .WithName("GetCurrentUser");

app.Run();
