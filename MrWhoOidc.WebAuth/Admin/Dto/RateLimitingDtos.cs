using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.WebAuth.Admin.Dto;

public record RateLimitStatusDto(
    string PolicyName,
    bool IsEnabled,
    long CurrentRequests,
    long? MaxRequests,
    TimeSpan? TimeWindow,
    DateTimeOffset? WindowResetTime,
    double PercentUsed = 0.0
);

public record ClientRateLimitDto(
    string ClientId,
    string ClientName,
    Guid TenantId,
    IReadOnlyList<PolicyUsageDto> PolicyUsages,
    DateTimeOffset LastRequestTime,
    bool IsBlocked
);

public record PolicyUsageDto(
    string PolicyName,
    long RequestsInWindow,
    long? MaxRequests,
    TimeSpan? TimeWindow,
    double PercentUsed,
    bool IsExceeded
);

public record RateLimitingOverviewDto(
    IReadOnlyList<RateLimitStatusDto> ActivePolicies,
    IReadOnlyList<ClientRateLimitDto> TopBlockedClients,
    DateTimeOffset SnapshotTime,
    long TotalBlockedRequests24H,
    long TotalAllowedRequests24H
);

public record QueryRateLimitsRequest(
    [Range(1, 100)] int Page = 1,
    [Range(1, 100)] int PageSize = 25,
    string? ClientIdFilter = null,
    string? PolicyNameFilter = null,
    DateTimeOffset? FromTime = null,
    DateTimeOffset? ToTime = null
);

public record RateLimitEventDto(
    DateTimeOffset Timestamp,
    string PolicyName,
    string ClientId,
    bool WasBlocked,
    string? IpAddress,
    int? RetryAfterSeconds
);

public record RateLimitEventsResponseDto(
    IReadOnlyList<RateLimitEventDto> Events,
    int TotalCount,
    int Page,
    int PageSize
);
