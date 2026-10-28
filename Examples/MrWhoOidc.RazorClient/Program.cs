using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MrWhoOidc.Client.DependencyInjection;
using MrWhoOidc.Client.Options;
using MrWhoOidc.RazorClient.Services;
using MrWhoOidc.Examples;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddExampleDataProtection(builder.Configuration);

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();

// The key pair the demo signs its JAR request objects with. The public part is published below so the
// identity provider can verify the signature (it only trusts a key registered for the client).
var jarSigningKey = new JarSigningKey(
    builder.Configuration,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger<JarSigningKey>());
builder.Services.AddSingleton(jarSigningKey);

builder.Services.AddMrWhoOidcClient(builder.Configuration, "MrWhoOidc");
builder.Services.PostConfigure<MrWhoOidcClientOptions>(options =>
{
    // The client library signs with the client secret (HS256) by default, which the server cannot verify:
    // it only stores a hash of the secret. Use the demo's own RSA key instead.
    options.Jar.SigningCredentialsResolver = _ => ValueTask.FromResult(jarSigningKey.SigningCredentials);
});

builder.Services.AddHttpClient<DelegatedApiClient>();

builder.Services.AddHttpClient<TestApiClient>((sp, client) =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var baseAddress = configuration["TestApi:BaseAddress"];
        if (!string.IsNullOrWhiteSpace(baseAddress) && Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            client.BaseAddress = uri;
        }
    })
    .AddMrWhoOnBehalfOfTokenHandler("examples-api", async (sp, ct) =>
    {
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var context = accessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        return await context.GetTokenAsync("access_token");
    });

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/auth/logout";
    // The cookie carries the access and the refresh token, so it is protected explicitly: secure, HTTP-only and
    // not readable by a script. It ends with the browser session (the callback signs in with IsPersistent = false).
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

builder.Services.AddAuthorization();

var app = builder.Build();

app.MapHealthChecks("/health");

// The public key of the demo's JAR signing pair: the identity provider fetches it to verify the request
// objects the demo signs.
app.MapGet("/.well-known/openid-client-jwks", (JarSigningKey key) => Results.Json(key.PublicJwkSet));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
