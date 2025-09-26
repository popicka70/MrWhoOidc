using System.Diagnostics.Metrics;
using System.Collections.Generic;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.WebAuth.Observability;

/// <summary>
/// Abstraction over raw OidcMetrics for uniform token endpoint metric emission.
/// Keeps grant handlers free of metric tagging logic and centralizes naming.
/// </summary>
public interface ITokenMetricsRecorder
{
    void RecordTokenRequest(string grantType, string outcome);
    void RecordTokenSuccess(string grantType);
    void RecordTokenFailure(string grantType);
    void RecordTokenDuration(string grantType, string outcome, double ms);

    // Token exchange rich metrics
    void RecordTokenExchange(string outcome, string clientBucket, string targetAudBucket, string dpopMode, string sourceTokenType, double? durationMs = null);
    void RecordTokenExchangeFailure(string clientBucket, string? targetAudBucket, string dpopMode, string sourceTokenType, string reason);
}

public sealed class DefaultTokenMetricsRecorder(OidcMetrics metrics) : ITokenMetricsRecorder
{
    public void RecordTokenRequest(string grantType, string outcome)
        => metrics.TokenRequests.Add(1, new KeyValuePair<string, object?>[] { new("grant_type", grantType), new("outcome", outcome) });

    public void RecordTokenSuccess(string grantType)
        => metrics.TokenSuccess.Add(1, new KeyValuePair<string, object?>[] { new("grant_type", grantType) });

    public void RecordTokenFailure(string grantType)
        => metrics.TokenFailures.Add(1, new KeyValuePair<string, object?>[] { new("grant_type", grantType) });

    public void RecordTokenDuration(string grantType, string outcome, double ms)
        => metrics.TokenDurationMs.Record(ms, new KeyValuePair<string, object?>[] { new("grant_type", grantType), new("outcome", outcome) });

    public void RecordTokenExchange(string outcome, string clientBucket, string targetAudBucket, string dpopMode, string sourceTokenType, double? durationMs = null)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("outcome", outcome),
            new("client_bucket", clientBucket),
            new("target_aud", targetAudBucket),
            new("dpop_mode", dpopMode),
            new("source_token_type", sourceTokenType)
        };
        metrics.TokenExchangeRequests.Add(1, tags);
        if (string.Equals(outcome, "success", System.StringComparison.Ordinal)) metrics.TokenExchangeSuccess.Add(1, tags); else metrics.TokenExchangeFailures.Add(1, tags);
        if (durationMs.HasValue) metrics.TokenExchangeDurationMs.Record(durationMs.Value, tags);
    }

    public void RecordTokenExchangeFailure(string clientBucket, string? targetAudBucket, string dpopMode, string sourceTokenType, string reason)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("outcome", "failure"),
            new("client_bucket", clientBucket),
            new("target_aud", targetAudBucket ?? "none"),
            new("dpop_mode", dpopMode),
            new("source_token_type", sourceTokenType),
            new("reason", reason)
        };
        metrics.TokenExchangeRequests.Add(1, tags);
        metrics.TokenExchangeFailures.Add(1, tags);
    }
}
