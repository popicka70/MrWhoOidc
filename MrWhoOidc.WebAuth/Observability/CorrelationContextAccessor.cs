using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Observability;

public interface ICorrelationContextAccessor
{
    string? CorrelationId { get; }
    bool HasCorrelation { get; }
    bool IsFromHeader { get; }
    void Set(string correlationId, bool fromHeader);
}

public sealed class CorrelationContextAccessor(IHttpContextAccessor httpContextAccessor) : ICorrelationContextAccessor
{
    internal const string ItemKey = "__mrwhooidc.cid";
    internal const string SourceKey = "__mrwhooidc.cid.src";

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public string? CorrelationId
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return null;
            if (ctx.Items.TryGetValue(ItemKey, out var value) && value is string cid)
            {
                return cid;
            }
            return null;
        }
    }

    public bool HasCorrelation => CorrelationId is not null;

    public bool IsFromHeader
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return false;
            if (ctx.Items.TryGetValue(SourceKey, out var value) && value is bool fromHeader)
            {
                return fromHeader;
            }
            return false;
        }
    }

    public void Set(string correlationId, bool fromHeader)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return;
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return;

        ctx.Items[ItemKey] = correlationId;
        ctx.Items[SourceKey] = fromHeader;

        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Headers["X-Correlation-Id"] = correlationId;
        }

        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("correlation_id", correlationId);
            if (!activity.Baggage.Any(b => string.Equals(b.Key, "cid", StringComparison.Ordinal)))
            {
                activity.AddBaggage("cid", correlationId);
            }
        }
    }
}
