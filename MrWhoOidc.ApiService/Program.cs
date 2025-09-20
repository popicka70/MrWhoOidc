using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// DPoP services
builder.Services.AddSingleton<MrWhoOidc.ApiService.IDPoPValidator, MrWhoOidc.ApiService.DPoPValidator>();
builder.Services.AddSingleton<MrWhoOidc.ApiService.IDPoPReplayCache, MrWhoOidc.ApiService.InMemoryDPoPReplayCache>();
builder.Services.AddSingleton<MrWhoOidc.ApiService.IDPoPNonceStore, MrWhoOidc.ApiService.InMemoryDPoPNonceStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

bool TryValidateJwt(string token, out System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwt, out ClaimsPrincipal principal)
{
    jwt = null!;
    principal = new ClaimsPrincipal();
    try
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        jwt = handler.ReadJwtToken(token);
        var identity = new ClaimsIdentity(jwt.Claims, "jwt");
        principal = new ClaimsPrincipal(identity);
        return true;
    }
    catch
    {
        return false;
    }
}

async Task<IResult> RequireDPoP(HttpContext http, string absoluteUrl, string accessToken, string? cnfJkt, MrWhoOidc.ApiService.IDPoPValidator validator, MrWhoOidc.ApiService.IDPoPReplayCache replayCache, MrWhoOidc.ApiService.IDPoPNonceStore nonceStore)
{
    if (string.IsNullOrEmpty(cnfJkt)) return Results.Unauthorized();

    var result = await validator.ValidateForEndpointAsync(http, absoluteUrl, accessToken);

    var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    (bool nonceOk, string nonce) = await nonceStore.ValidateOrIssueAsync(absoluteUrl, clientIp, result.Jkt, result.Nonce);
    if (!nonceOk)
    {
        http.Response.Headers["DPoP-Nonce"] = nonce;
        return Results.Unauthorized();
    }

    if (!result.Ok || string.IsNullOrEmpty(result.Jkt) || !string.Equals(result.Jkt, cnfJkt, StringComparison.Ordinal))
        return Results.Unauthorized();

    if (string.IsNullOrEmpty(result.Jti) || result.Iat is null)
        return Results.Unauthorized();

    var key = $"{result.Jkt}:{result.Jti}";
    var exp = DateTimeOffset.FromUnixTimeSeconds(result.Iat.Value).AddMinutes(5);
    if (!replayCache.TryAdd(key, exp)) return Results.Unauthorized();

    return Results.Ok();
}

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/weatherforecast", async (HttpContext http, MrWhoOidc.ApiService.IDPoPValidator validator, MrWhoOidc.ApiService.IDPoPReplayCache replay, MrWhoOidc.ApiService.IDPoPNonceStore nonce) =>
{
    var auth = http.Request.Headers["Authorization"].ToString();
    if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
        return Results.Unauthorized();

    var token = auth["Bearer ".Length..].Trim();

    if (!TryValidateJwt(token, out var jwt, out var principal))
        return Results.Unauthorized();

    var cnfClaim = principal.FindFirst("cnf")?.Value;
    string? jkt = null;
    if (!string.IsNullOrEmpty(cnfClaim))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(cnfClaim);
            if (doc.RootElement.TryGetProperty("jkt", out var j)) jkt = j.GetString();
        }
        catch { }
    }

    if (!string.IsNullOrEmpty(jkt))
    {
        var absolute = ($"{http.Request.Scheme}://{http.Request.Host}")!.TrimEnd('/') + "/weatherforecast";
        var ok = await RequireDPoP(http, absolute, token, jkt, validator, replay, nonce);
        if (ok is not IStatusCodeHttpResult { StatusCode: 200 }) return ok;
    }

    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return Results.Json(forecast);
})
.WithName("GetWeatherForecast");

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
