using System;

namespace MrWhoOidc.Client.Options;

public sealed class LogoutClientOptions
{
    public bool EnableFrontChannel { get; set; } = true;

    public bool EnableBackchannel { get; set; } = true;

    public TimeSpan BackchannelClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan BackchannelReplayCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}
