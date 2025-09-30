using System;
using System.Collections.Generic;

namespace MrWhoOidc.Client.Logout;

public sealed record FrontChannelLogoutRequest
{
    public required Uri LogoutUri { get; init; }

    public string? State { get; init; }

    public bool HasPostLogoutRedirect { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}
