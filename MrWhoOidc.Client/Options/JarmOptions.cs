using System;

namespace MrWhoOidc.Client.Options;

public sealed class JarmOptions
{
    public bool Enabled { get; set; }

    public string ResponseMode { get; set; } = "query.jwt";

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    public bool ValidateHashes { get; set; } = true;
}
