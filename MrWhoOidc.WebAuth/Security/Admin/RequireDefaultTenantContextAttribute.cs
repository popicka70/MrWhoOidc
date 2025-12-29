using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Guards platform admin pages so they are only reachable when the user is operating within the default tenant context.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireDefaultTenantContextAttribute : TypeFilterAttribute
{
    public RequireDefaultTenantContextAttribute() : base(typeof(RequireDefaultTenantContextFilter))
    {
    }

    private sealed class RequireDefaultTenantContextFilter(
        IDefaultTenantContextResolver defaultTenantContextResolver,
        IMultiTenancyOptions multiTenancyOptions,
        ITempDataDictionaryFactory tempDataFactory,
        ILogger<RequireDefaultTenantContextFilter> logger) : IAsyncPageFilter
    {
        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            if (defaultTenantContextResolver.IsDefaultTenantContext(context.HttpContext))
            {
                await next();
                return;
            }

            logger.LogWarning("Platform admin route {Path} blocked because user is not in default tenant context", context.HttpContext.Request.Path);

            if (multiTenancyOptions.Enabled)
            {
                var tempData = tempDataFactory.GetTempData(context.HttpContext);
                tempData["Error"] = "Platform administration is only available from the default tenant. Please switch back to continue.";

                var redirectPath = BuildDefaultTenantRedirectPath(context.HttpContext, multiTenancyOptions.DefaultTenantSlug);
                context.Result = new RedirectResult(redirectPath);
            }
            else
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }

        private static string BuildDefaultTenantRedirectPath(HttpContext httpContext, string defaultSlug)
        {
            var path = httpContext.Request.Path.Value ?? "/";
            var query = httpContext.Request.QueryString.HasValue ? httpContext.Request.QueryString.Value : string.Empty;

            if (path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
            {
                var indexOfSecondSlash = path.IndexOf('/', 3);
                var remainder = indexOfSecondSlash >= 0 ? path[indexOfSecondSlash..] : "/";
                return $"/t/{defaultSlug}{remainder}{query}";
            }

            return $"/t/{defaultSlug}{path}{query}";
        }
    }
}
