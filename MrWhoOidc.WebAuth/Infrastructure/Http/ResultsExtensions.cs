using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MrWhoOidc.WebAuth.Infrastructure.Http;

public static class ResultsExtensions
{
    public static IResult RazorPage(this IResultExtensions extensions, string pageName, object? routeValues = null)
    {
        return new RazorPageResult(pageName, routeValues);
    }
}

internal sealed class RazorPageResult(string pageName, object? routeValues) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, httpContext.GetRouteData(), new ActionDescriptor());
        
        // This is a simplified implementation for FormPost.jwt scenario.
        // In a real app, we'd use the full Razor Pages executor, but for OIDC FormPost 
        // we just need to render the specific /FormPost page with the provided model.
        
        var executor = httpContext.RequestServices.GetRequiredService<IActionResultExecutor<PartialViewResult>>();
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = routeValues
        };

        var result = new PartialViewResult
        {
            ViewName = pageName,
            ViewData = viewData
        };

        await executor.ExecuteAsync(actionContext, result);
    }
}
