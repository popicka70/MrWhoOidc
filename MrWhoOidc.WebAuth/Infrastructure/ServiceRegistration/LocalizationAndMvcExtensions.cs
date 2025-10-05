using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers presentation-layer services: Razor Pages (with admin folder authorization and multi-tenant routing),
/// MVC with a global antiforgery filter, customized antiforgery options, and localization resources.
/// Extracted from Program.cs as part of composition root slimming (Phase 2: AddLocalizationAndMvc).
/// Idempotent – safe to call multiple times.
/// </summary>
public static class LocalizationAndMvcExtensions
{
    /// <summary>
    /// Adds Razor Pages with multi-tenant routing support, MVC (with global antiforgery filter), 
    /// antiforgery configuration, and localization.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">App configuration</param>
    /// <returns>IServiceCollection for chaining</returns>
    public static IServiceCollection AddLocalizationAndMvc(this IServiceCollection services, IConfiguration configuration)
    {
        // Read multi-tenancy configuration directly from IConfiguration
        var multiTenancySection = configuration.GetSection("MultiTenancy");
        var isMultiTenantMode = multiTenancySection.GetValue<bool>("Enabled", false);

        // Razor Pages (admin folder locked down by policy = admin)
        // Multi-tenant routing added via conventions
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Admin", "admin");
            
            if (isMultiTenantMode)
            {
                // Helper method to add tenant-prefixed routes
                void AddTenantPrefixedRoutes(PageRouteModel model)
                {
                    var selectorsToAdd = new List<SelectorModel>();
                    
                    foreach (var selector in model.Selectors)
                    {
                        if (selector.AttributeRouteModel?.Template != null)
                        {
                            // Create a new selector with tenant-prefixed template
                            var tenantSelector = new SelectorModel(selector)
                            {
                                AttributeRouteModel = new AttributeRouteModel
                                {
                                    Template = $"t/{{slug}}/{selector.AttributeRouteModel.Template}",
                                    Order = selector.AttributeRouteModel.Order.HasValue 
                                        ? selector.AttributeRouteModel.Order.Value - 1 
                                        : -1 // Higher priority than fallback
                                }
                            };
                            selectorsToAdd.Add(tenantSelector);
                        }
                    }
                    
                    // Add all tenant-prefixed selectors to the model
                    foreach (var selector in selectorsToAdd)
                    {
                        model.Selectors.Add(selector);
                    }
                }
                
                // Add tenant-prefixed routes for all Admin pages: /t/{slug}/admin/*
                // Original /admin/* routes remain as fallback for backward compatibility
                options.Conventions.AddFolderRouteModelConvention("/Admin", model => AddTenantPrefixedRoutes(model));
                
                // Add tenant-prefixed routes for authentication-related pages
                // This allows: /t/{slug}/login, /t/{slug}/consent, etc.
                var authPages = new[] { "/Login", "/LoginTotp", "/Consent", "/Index" };
                foreach (var page in authPages)
                {
                    options.Conventions.AddPageRouteModelConvention(page, model => AddTenantPrefixedRoutes(model));
                }
                
                // Add tenant-prefixed routes for logout pages
                options.Conventions.AddFolderRouteModelConvention("/Logout", model => AddTenantPrefixedRoutes(model));
                
                // Add tenant-prefixed routes for auth flows (QR, external providers)
                options.Conventions.AddFolderRouteModelConvention("/Auth", model => AddTenantPrefixedRoutes(model));
                
                // Add tenant-prefixed routes for MFA/password management
                options.Conventions.AddFolderRouteModelConvention("/Mfa", model => AddTenantPrefixedRoutes(model));
                options.Conventions.AddFolderRouteModelConvention("/Password", model => AddTenantPrefixedRoutes(model));
                
                // Add tenant-prefixed routes for registrations
                options.Conventions.AddFolderRouteModelConvention("/Registrations", model => AddTenantPrefixedRoutes(model));
            }
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
