using System.Collections.Generic;

namespace MrWhoOidc.Auth.Services.Authorization;

public record ProviderOption(
    string Name,
    string DisplayName,
    bool IsDefaultForClient,
    bool AutoRedirectIfSingle
);

public record ProviderSelectionResult(
    bool RequiresSelection,
    string? AutoRedirectProvider = null,
    IEnumerable<ProviderOption>? AvailableProviders = null,
    bool AllowLocal = true,
    bool AllowExternal = true,
    bool AllowQr = false
);
