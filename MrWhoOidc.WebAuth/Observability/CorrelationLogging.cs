using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Observability;

public static class CorrelationLogging
{
    public static IDisposable BeginScope(ILogger logger, string? correlationId, string? provider = null, string? clientId = null)
    {
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        IDictionary<string, object?> scope;
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            scope = new Dictionary<string, object?>();
        }
        else
        {
            scope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["correlation_id"] = correlationId
            };
            if (!string.IsNullOrWhiteSpace(provider))
            {
                scope["provider"] = provider;
            }
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                scope["clientId"] = clientId;
            }
        }

        return logger.BeginScope(scope) ?? NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
