using System;
using System.Collections.Generic;

namespace MrWhoOidc.Client.Logout;

public sealed class FrontChannelLogoutOptions
{
    /// <summary>
    /// Optional post-logout redirect URI registered with the identity provider.
    /// Must be absolute when provided.
    /// </summary>
    public Uri? PostLogoutRedirectUri { get; set; }

    /// <summary>
    /// Optional state value to round-trip through the logout flow. When not provided, a state value will be generated.
    /// Set <see cref="SuppressState"/> to true to prevent sending any state parameter.
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// When true, the state parameter will not be sent even if <see cref="State"/> is provided or generated.
    /// </summary>
    public bool SuppressState { get; set; }

    /// <summary>
    /// Optional ID token hint issued by the identity provider for the current session.
    /// </summary>
    public string? IdTokenHint { get; set; }

    /// <summary>
    /// Optional session identifier (sid) associated with the current session.
    /// </summary>
    public string? Sid { get; set; }

    /// <summary>
    /// Optional hint that can help the identity provider identify the user.
    /// </summary>
    public string? LogoutHint { get; set; }

    /// <summary>
    /// Additional query parameters to append to the end session request.
    /// </summary>
    public IDictionary<string, string?> AdditionalParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// When true (default), the client_id parameter will be included in the request.
    /// </summary>
    public bool IncludeClientId { get; set; } = true;
}
