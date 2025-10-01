namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Logs audit events for introspection operations.
/// </summary>
internal static class IntrospectionAuditor
{
    public static void LogAudit(
        ILogger logger,
        string clientId,
        string? ipAddress,
        string outcome,
        string? audience)
    {
        var clientBucket = clientId.BucketizeClientId();
        logger.LogInformation(
            "Introspection audit: client={ClientBucket} ip={IP} outcome={Outcome} aud={Audience}",
            clientBucket,
            ipAddress ?? "unknown",
            outcome,
            audience ?? "none"
        );
    }
}
