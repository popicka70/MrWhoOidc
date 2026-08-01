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
using MrWhoOidc.TestApi.Services;

var builder = WebApplication.CreateBuilder(args);

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

builder.Services.AddMrWhoOidcClient(builder.Configuration, "MrWhoOidc");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks();
builder.Services.AddHttpClient<DelegatedTokenIntrospectionService>();

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

app.MapGet("/profiles/{profileId:guid}/summary", async (
    Guid profileId,
    HttpContext context,
    DelegatedTokenIntrospectionService introspection,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var accessToken = ReadBearerToken(context.Request);
    if (accessToken is null)
    {
        return Results.Unauthorized();
    }

    var token = await introspection.IntrospectAsync(accessToken, cancellationToken).ConfigureAwait(false);
    if (token is null)
    {
        return Results.Unauthorized();
    }

    if (!token.Audience.Contains("api", StringComparer.Ordinal)
        || !token.Scopes.Contains("profile", StringComparer.Ordinal))
    {
        return Results.Json(new { error = "insufficient_scope" }, statusCode: StatusCodes.Status403Forbidden);
    }

    var profileIdText = profileId.ToString();
    var isDelegated = !string.IsNullOrWhiteSpace(token.DelegationId);
    if (isDelegated)
    {
        var expectedClientId = configuration["MrWhoOidc:DelegatedClientId"] ?? "blazor-web";
        if (!string.Equals(token.ClientId, expectedClientId, StringComparison.Ordinal)
            || !Guid.TryParse(token.Subject, out _)
            || !Guid.TryParse(token.Actor, out _)
            || string.Equals(token.Subject, token.Actor, StringComparison.Ordinal)
            || !token.AllowsResource("profile.read", "user", profileIdText))
        {
            return Results.Json(new { error = "delegation_not_authorized" }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
    else if (!string.Equals(token.Subject, profileIdText, StringComparison.Ordinal))
    {
        return Results.Json(new { error = "resource_not_owned" }, statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new
    {
        profileId = profileIdText,
        owner = token.Subject,
        actor = token.Actor ?? token.Subject,
        delegated = isDelegated,
        delegationId = token.DelegationId,
        clientId = token.ClientId,
        capability = isDelegated ? "profile.read" : "profile",
        resourceType = "user",
        resourceId = profileIdText,
        auditReference = context.TraceIdentifier
    });
})
    .RequireAuthorization()
    .WithName("GetProfileSummary");

static string? ReadBearerToken(HttpRequest request)
{
    var authorization = request.Headers.Authorization.ToString();
    const string bearerPrefix = "Bearer ";
    return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[bearerPrefix.Length..].Trim()
        : null;
}

app.Run();
