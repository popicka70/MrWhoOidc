using System;

namespace MrWhoOidc.Auth.Licensing.Models;

public sealed record UsageLimitInfo(
    string LimitType,
    long CurrentUsage,
    long LimitValue,
    double UtilizationPercentage,
    bool IsNearLimit,
    bool IsAtLimit)
{
    public bool IsUnlimited => LimitValue == -1;

    public bool IsDisabled => LimitValue == 0;

    public long RemainingCapacity => IsUnlimited ? long.MaxValue : Math.Max(0, LimitValue - CurrentUsage);
}
