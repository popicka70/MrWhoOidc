using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers presentation-layer services: Razor Pages (with admin folder authorization),
/// MVC with a global antiforgery filter, customized antiforgery options, and localization resources.
/// Extracted from Program.cs as part of composition root slimming (Phase 2: AddLocalizationAndMvc).
/// Idempotent – safe to call multiple times.
/// </summary>
public static class LocalizationAndMvcExtensions
{
    /// <summary>
    /// Adds Razor Pages, MVC (with global antiforgery filter), antiforgery configuration, and localization.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">App configuration (reserved for future culture customization)</param>
    /// <returns>IServiceCollection for chaining</returns>
    public static IServiceCollection AddLocalizationAndMvc(this IServiceCollection services, IConfiguration configuration)
    {
        // Razor Pages (admin folder locked down by policy = admin)
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Admin", "admin");
        });

        // MVC with global antiforgery auto-validation
        services.AddMvc(options =>
        {
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        });

        // Antiforgery explicit cookie + header settings (moved from security core)
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = ".mrwhooidc.af";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.FormFieldName = "__RequestVerificationToken";
            options.HeaderName = "X-CSRF-TOKEN";
        });

        // Localization support (resource path maintained as before)
        services.AddLocalization(o => o.ResourcesPath = "Resources");

        return services;
    }
}
