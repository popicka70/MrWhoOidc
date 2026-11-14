namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>Backend selected for <see cref="IDistributedStateStore"/>.</summary>
public enum DistributedStateProvider
{
    /// <summary>Database (<c>AuthDbContext</c>). The default when no provider is configured.</summary>
    Database,
    /// <summary>Redis via <c>ConnectionStrings:redis</c>.</summary>
    Redis,
    /// <summary>In-process memory. Only safe with a single replica; intended for tests/development.</summary>
    Memory
}

public sealed class DistributedStateOptions
{
    public const string SectionName = "DistributedState";

    /// <summary>
    /// Backend selection. Defaults to <see cref="DistributedStateProvider.Database"/>.
    /// Accepts <c>Database</c>/<c>Db</c>, <c>Redis</c>, and <c>Memory</c> (case-insensitive).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>How often the database backend deletes expired rows. Default 5 minutes.</summary>
    public int CleanupIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Default lifetime used by the <c>IDistributedCache</c> adapter when a caller supplies no
    /// expiration. Default 20 minutes.
    /// </summary>
    public int DefaultEntryTtlMinutes { get; set; } = 20;

    public DistributedStateProvider ResolveProvider()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            return DistributedStateProvider.Database;
        }

        return Provider.Trim() switch
        {
            var value when value.Equals("Database", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("Db", StringComparison.OrdinalIgnoreCase)
                => DistributedStateProvider.Database,
            var value when value.Equals("Redis", StringComparison.OrdinalIgnoreCase)
                => DistributedStateProvider.Redis,
            var value when value.Equals("Memory", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("InMemory", StringComparison.OrdinalIgnoreCase)
                => DistributedStateProvider.Memory,
            _ => throw new InvalidOperationException(
                $"Unknown DistributedState:Provider '{Provider}'. Use 'Database', 'Redis', or 'Memory'.")
        };
    }
}
