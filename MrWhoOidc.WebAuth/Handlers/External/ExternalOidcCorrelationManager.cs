using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Manages correlation tracking for external OIDC flows.
/// </summary>
public interface IExternalOidcCorrelationManager
{
    Task<CorrelationSnapshot> EnsureCorrelationAsync(HttpContext http, string? currentCorrelationId, string? requestedHandle);
    Task<CorrelationResolutionResult> ResolveCorrelationAsync(HttpContext http, StateModel state);
}

internal sealed class ExternalOidcCorrelationManager : IExternalOidcCorrelationManager
{
    private static readonly object CorrelationHandleItemKey = new();

    private readonly ICorrelationContextAccessor _correlationContext;
    private readonly ICorrelationStateCache _correlationCache;
    private readonly ICorrelationIdGenerator _correlationGenerator;
    private readonly ILogger<ExternalOidcCorrelationManager> _logger;

    public ExternalOidcCorrelationManager(
        ICorrelationContextAccessor correlationContext,
        ICorrelationStateCache correlationCache,
        ICorrelationIdGenerator correlationGenerator,
        ILogger<ExternalOidcCorrelationManager> logger)
    {
        _correlationContext = correlationContext;
        _correlationCache = correlationCache;
        _correlationGenerator = correlationGenerator;
        _logger = logger;
    }

    public async Task<CorrelationSnapshot> EnsureCorrelationAsync(
        HttpContext http,
        string? currentCorrelationId,
        string? requestedHandle)
    {
        var correlationId = currentCorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = _correlationGenerator.GenerateCorrelationId();
            _logger.LogDebug("Generated new correlation id {CorrelationId} for external flow", correlationId);
        }

        if (!_correlationContext.HasCorrelation ||
            !string.Equals(_correlationContext.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            _correlationContext.Set(correlationId, false);
        }

        if (http.Items.TryGetValue(CorrelationHandleItemKey, out var cached) && cached is string cachedHandle)
        {
            return new CorrelationSnapshot(correlationId, cachedHandle);
        }

        if (!string.IsNullOrEmpty(requestedHandle) && ExternalOidcUrlHelpers.LooksLikeHandle(requestedHandle))
        {
            var resolved = await _correlationCache.TryGetAsync(requestedHandle, consume: false, http.RequestAborted);
            if (!string.IsNullOrEmpty(resolved))
            {
                if (!string.Equals(resolved, correlationId, StringComparison.Ordinal))
                {
                    correlationId = resolved;
                    _correlationContext.Set(correlationId, false);
                }
                http.Items[CorrelationHandleItemKey] = requestedHandle;
                return new CorrelationSnapshot(correlationId, requestedHandle);
            }
        }

        var handle = await _correlationCache.StoreAsync(correlationId!, http.RequestAborted);
        http.Items[CorrelationHandleItemKey] = handle;
        return new CorrelationSnapshot(correlationId!, handle);
    }

    public async Task<CorrelationResolutionResult> ResolveCorrelationAsync(HttpContext http, StateModel state)
    {
        var requestedHandle = state.CorrelationHandle;
        if (string.IsNullOrEmpty(requestedHandle))
        {
            var queryHandle = http.Request.Query["cid_ref"].ToString();
            if (!string.IsNullOrEmpty(queryHandle))
                requestedHandle = queryHandle;
        }

        if (!string.IsNullOrEmpty(requestedHandle) && ExternalOidcUrlHelpers.LooksLikeHandle(requestedHandle))
        {
            var resolved = await _correlationCache.TryGetAsync(requestedHandle, consume: false, http.RequestAborted);
            if (!string.IsNullOrEmpty(resolved))
            {
                state.CorrelationHandle = requestedHandle;
                state.CorrelationId = resolved;
                if (!string.IsNullOrEmpty(state.ReturnUrl))
                {
                    state.ReturnUrl = ExternalOidcUrlHelpers.EnsureCidRef(state.ReturnUrl, requestedHandle);
                }
                _correlationContext.Set(resolved, false);
                http.Items[CorrelationHandleItemKey] = requestedHandle;
                return new CorrelationResolutionResult(true, resolved, requestedHandle, true, false);
            }
            return new CorrelationResolutionResult(false, state.CorrelationId ?? string.Empty, requestedHandle, true, true);
        }

        if (!string.IsNullOrEmpty(state.CorrelationId))
        {
            var handle = await _correlationCache.StoreAsync(state.CorrelationId, http.RequestAborted);
            state.CorrelationHandle = handle;
            if (!string.IsNullOrEmpty(state.ReturnUrl))
            {
                state.ReturnUrl = ExternalOidcUrlHelpers.EnsureCidRef(state.ReturnUrl, handle);
            }
            _correlationContext.Set(state.CorrelationId, false);
            http.Items[CorrelationHandleItemKey] = handle;
            return new CorrelationResolutionResult(true, state.CorrelationId, handle, false, false);
        }

        var generated = _correlationGenerator.GenerateCorrelationId();
        var generatedHandle = await _correlationCache.StoreAsync(generated, http.RequestAborted);
        state.CorrelationId = generated;
        state.CorrelationHandle = generatedHandle;
        if (!string.IsNullOrEmpty(state.ReturnUrl))
        {
            state.ReturnUrl = ExternalOidcUrlHelpers.EnsureCidRef(state.ReturnUrl, generatedHandle);
        }
        _correlationContext.Set(generated, false);
        http.Items[CorrelationHandleItemKey] = generatedHandle;
        return new CorrelationResolutionResult(true, generated, generatedHandle, false, false);
    }

    public static string HashHandleForLog(string handle)
        => string.IsNullOrEmpty(handle) ? string.Empty : CorrelationFormatting.ShortHash(handle);
}
