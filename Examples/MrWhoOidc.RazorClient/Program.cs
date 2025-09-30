using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MrWhoOidc.Client.DependencyInjection;
using MrWhoOidc.RazorClient.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMrWhoOidcClient(builder.Configuration, "MrWhoOidc");

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
});

builder.Services.AddAuthorization();

var app = builder.Build();

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
