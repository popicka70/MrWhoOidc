using System;

namespace MrWhoOidc.Client.Logout;

public sealed record BackchannelLogoutValidationResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public string? Sid { get; init; }

    public string? Subject { get; init; }

    public string? JwtId { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public static BackchannelLogoutValidationResult Disabled(string reason) => new()
    {
        Success = false,
        Error = reason
    };
}
