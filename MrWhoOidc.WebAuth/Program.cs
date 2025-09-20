using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure issuer from configuration (fallback to runtime URL)
var configuredIssuer = builder.Configuration["Oidc:Issuer"];

// Add services to the container.
builder.Services.AddRazorPages();

// Cookies for local login session
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".mrwhooidc.auth";
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

// Wire up Auth persistence (PostgreSQL via Aspire connection)
builder.Services.AddAuthPersistence(builder.Configuration);
// Register Auth core services
builder.Services.AddMrWhoOidcAuthCore();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Delay DB migration and seeding until after the host is fully started
app.Lifetime.ApplicationStarted.Register(() =>
{
    Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();

        // Ensure at least one signing key exists
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
        await keyStore.GetActiveSigningKeyAsync();

        // Seed default user and client
        await MrWhoOidc.Auth.Seeding.DatabaseSeeder.EnsureSeedDataAsync(app.Services);
    });
});

app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) =>
{
    var issuer = configuredIssuer ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var body = new
    {
        issuer,
        authorization_endpoint = "/authorize",
        token_endpoint = "/token",
        userinfo_endpoint = "/userinfo",
        jwks_uri = "/jwks",
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        code_challenge_methods_supported = new[] { "S256" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
        scopes_supported = new[] { "openid", "profile", "email" }
    };
    ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
    return Results.Json(body);
});

app.MapGet("/authorize", async (
    HttpContext http,
    IAuthorizeService authorize,
    IAuthorizationCodeService codes
) =>
{
    var req = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
    {
        response_type = http.Request.Query["response_type"],
        client_id = http.Request.Query["client_id"],
        redirect_uri = http.Request.Query["redirect_uri"],
        scope = http.Request.Query["scope"],
        state = http.Request.Query["state"],
        nonce = http.Request.Query["nonce"],
        code_challenge = http.Request.Query["code_challenge"],
        code_challenge_method = http.Request.Query["code_challenge_method"],
    };

    var validation = await authorize.ValidateAsync(req);
    if (!validation.IsValid)
    {
        // Redirect back with error to the provided redirect_uri if valid, else 400
        if (!string.IsNullOrEmpty(req.redirect_uri))
        {
            var uri = new UriBuilder(req.redirect_uri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query["error"] = validation.Error;
            query["error_description"] = validation.ErrorDescription;
            if (!string.IsNullOrEmpty(req.state)) query["state"] = req.state;
            uri.Query = query.ToString();
            return Results.Redirect(uri.ToString());
        }
        return Results.BadRequest(new { error = validation.Error, error_description = validation.ErrorDescription });
    }

    // Require authenticated user
    if (!http.User.Identity?.IsAuthenticated ?? true)
    {
        var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
        return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    // TODO: check consent here; if required and not granted, redirect to /consent with ReturnUrl

    // Issue auth code
    var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
        return Results.Unauthorized();

    var (ok, _, redirect) = await codes.IssueAsync(validation, userId);
    if (!ok || redirect is null) return Results.Problem("Failed to issue code");

    // Preserve state
    if (!string.IsNullOrEmpty(req.state))
    {
        var uri = new UriBuilder(redirect);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["state"] = req.state;
        uri.Query = query.ToString();
        return Results.Redirect(uri.ToString());
    }

    return Results.Redirect(redirect);
});

app.MapPost("/token", async (HttpContext http, ITokenService tokens) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "invalid_request" });

    var form = await http.Request.ReadFormAsync();
    var grantType = form["grant_type"].ToString();
    if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "unsupported_grant_type" });

    var code = form["code"].ToString();
    var redirectUri = form["redirect_uri"].ToString();
    var clientId = form["client_id"].ToString();
    var codeVerifier = form["code_verifier"].ToString();

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(clientId))
        return Results.BadRequest(new { error = "invalid_request" });

    var issuer = configuredIssuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
    var (ok, payload, _, status) = await tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, clientId, codeVerifier, issuer);
    return Results.Json(payload!, statusCode: status);
});

app.MapGet("/jwks", async (HttpContext ctx, IKeyStore keys, CancellationToken ct) =>
{
    var jwks = await keys.GetPublicJwksAsync(ct);
    ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
    return Results.Json(new { keys = jwks });
});

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
