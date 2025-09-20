using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorPages();

// Wire up Auth persistence (PostgreSQL via Aspire connection)
builder.Services.AddAuthPersistence(builder.Configuration);

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
app.UseAuthorization();

// Delay DB migration until after the host is fully started to avoid container startup races
app.Lifetime.ApplicationStarted.Register(() =>
{
    Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var retries = 5;
        while (retries-- > 0)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    });
});

app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) => Results.Json(new
{
    issuer = $"{ctx.Request.Scheme}://{ctx.Request.Host}",
    authorization_endpoint = "/authorize",
    token_endpoint = "/token",
    userinfo_endpoint = "/userinfo",
    jwks_uri = "/jwks",
    response_types_supported = new[] { "code" },
    grant_types_supported = new[] { "authorization_code", "refresh_token" },
    code_challenge_methods_supported = new[] { "S256" },
    id_token_signing_alg_values_supported = new[] { "RS256" },
    scopes_supported = new[] { "openid", "profile", "email" }
}));

app.MapGet("/jwks", () => Results.Json(new { keys = Array.Empty<object>() }));

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
