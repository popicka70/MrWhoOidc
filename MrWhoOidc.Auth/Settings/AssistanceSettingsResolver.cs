using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Settings;

/// <summary>
/// Effective, validated consent-assistance settings for one tenant. The raw <see cref="AssistanceTenantSettings"/>
/// is stored as JSON; this type applies defaults and keeps the role lists normalized so every consumer makes the
/// same decision.
/// </summary>
public sealed class ResolvedAssistanceSettings
{
    public bool Enabled { get; init; } = true;
    public UserAssistanceRightsMode RightsMode { get; init; } = UserAssistanceRightsMode.Intersection;
    public IReadOnlyList<string> RequesterRoles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReadOnlyHelperRoles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReadWriteHelperRoles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ManagerRoles { get; init; } = Array.Empty<string>();
    public AssistanceReadOnlyTokenPolicy ReadOnlyTokenPolicy { get; init; } = AssistanceReadOnlyTokenPolicy.MarkerOnly;
    public int DefaultDurationMinutes { get; init; } = 15;
    public int MaxDurationMinutes { get; init; } = 60;
    public int RequestLifetimeMinutes { get; init; } = 10;

    /// <summary>Empty requester roles means every authenticated tenant user may ask for help.</summary>
    public bool IsRequesterAllowed(IEnumerable<string> roleNames)
        => RequesterRoles.Count == 0 || roleNames.Any(ContainsRequesterRole);

    public bool IsReadOnlyHelper(IEnumerable<string> roleNames)
        => roleNames.Any(ContainsReadOnlyHelperRole) || roleNames.Any(ContainsReadWriteHelperRole);

    public bool IsReadWriteHelper(IEnumerable<string> roleNames)
        => roleNames.Any(ContainsReadWriteHelperRole);

    public bool IsManager(IEnumerable<string> roleNames)
        => roleNames.Any(ContainsManagerRole);

    private bool ContainsRequesterRole(string role)
        => RequesterRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private bool ContainsReadOnlyHelperRole(string role)
        => ReadOnlyHelperRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private bool ContainsReadWriteHelperRole(string role)
        => ReadWriteHelperRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private bool ContainsManagerRole(string role)
        => ManagerRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public enum AssistanceReadOnlyTokenPolicy
{
    /// <summary>Issue tokens with assistance_mode=read_only; relying parties must honor the marker.</summary>
    MarkerOnly = 0,

    /// <summary>Refuse token issuance for read-only assistance; the helper may only use OP-hosted surfaces.</summary>
    Deny = 1
}

public static class AssistanceSettingsResolver
{
    public static readonly string[] DefaultReadOnlyHelperRoles = ["tenant-admin"];
    public static readonly string[] DefaultManagerRoles = ["tenant-admin"];

    public static ResolvedAssistanceSettings Resolve(AssistanceTenantSettings? settings)
    {
        settings ??= new AssistanceTenantSettings();

        var max = Clamp(settings.MaxDurationMinutes ?? 60, 1, 24 * 60);
        var defaultDuration = Clamp(settings.DefaultDurationMinutes ?? 15, 1, max);
        var requestLifetime = Clamp(settings.RequestLifetimeMinutes ?? 10, 1, 24 * 60);

        return new ResolvedAssistanceSettings
        {
            Enabled = settings.Enabled ?? true,
            RightsMode = ParseRightsMode(settings.RightsMode),
            RequesterRoles = NormalizeRoles(settings.RequesterRoles),
            ReadOnlyHelperRoles = NormalizeRoles(settings.ReadOnlyHelperRoles, DefaultReadOnlyHelperRoles),
            ReadWriteHelperRoles = NormalizeRoles(settings.ReadWriteHelperRoles),
            ManagerRoles = NormalizeRoles(settings.ManagerRoles, DefaultManagerRoles),
            ReadOnlyTokenPolicy = ParseTokenPolicy(settings.ReadOnlyTokenPolicy),
            DefaultDurationMinutes = defaultDuration,
            MaxDurationMinutes = max,
            RequestLifetimeMinutes = requestLifetime
        };
    }

    public static UserAssistanceRightsMode ParseRightsMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UserAssistanceRightsMode.Intersection;
        }

        var normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized.Equals("fulltarget", StringComparison.OrdinalIgnoreCase)
            ? UserAssistanceRightsMode.FullTarget
            : UserAssistanceRightsMode.Intersection;
    }

    public static string FormatRightsMode(UserAssistanceRightsMode mode)
        => mode == UserAssistanceRightsMode.FullTarget ? "fullTarget" : "intersection";

    public static AssistanceReadOnlyTokenPolicy ParseTokenPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AssistanceReadOnlyTokenPolicy.MarkerOnly;
        }

        var normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized.Equals("deny", StringComparison.OrdinalIgnoreCase)
            ? AssistanceReadOnlyTokenPolicy.Deny
            : AssistanceReadOnlyTokenPolicy.MarkerOnly;
    }

    public static string FormatTokenPolicy(AssistanceReadOnlyTokenPolicy policy)
        => policy == AssistanceReadOnlyTokenPolicy.Deny ? "deny" : "markerOnly";

    /// <summary>Used by the admin settings page to reject unknown role names.</summary>
    public static IReadOnlyList<string> NormalizeRoles(IEnumerable<string>? roles, IEnumerable<string>? fallback = null)
    {
        var source = roles ?? fallback;
        if (source is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var role in source)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var trimmed = role.Trim();
            if (trimmed.Length > 100)
            {
                trimmed = trimmed[..100];
            }

            if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
