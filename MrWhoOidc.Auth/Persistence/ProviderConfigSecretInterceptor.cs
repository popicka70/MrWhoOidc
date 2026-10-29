using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MrWhoOidc.Auth.IdentityProviders;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Keeps the upstream client secret in <see cref="IdentityProvider.ConfigJson"/> and the secrets in
/// <see cref="IdentityProvider.ProviderSpecificConfigJson"/> (the Sign in with Apple private key) protected at rest
/// while every reader works with the plaintext config:
/// <list type="bullet">
/// <item>materialization (tracked and no-tracking queries) unprotects the secret members;</item>
/// <item>saving protects them in the outgoing values and puts the plaintext back afterwards, so the tracked entity
/// stays usable and unchanged.</item>
/// </list>
/// A context without an <see cref="Services.ISecretProtector"/> (design time, some tests) is left alone.
/// </summary>
internal sealed class ProviderConfigSecretInterceptor : IMaterializationInterceptor, ISaveChangesInterceptor
{
    public static readonly ProviderConfigSecretInterceptor Instance = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<(EntityEntry<IdentityProvider> Entry, string? ConfigJson, string? ProviderSpecificConfigJson)>> _pending = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is IdentityProvider provider
            && materializationData.Context is AuthDbContext { SecretProtectorForStorage: { } protector })
        {
            provider.ConfigJson = ProviderConfigSecrets.Unprotect(provider.ConfigJson, protector);
            provider.ProviderSpecificConfigJson = ProviderConfigSecrets.Unprotect(provider.ProviderSpecificConfigJson, protector);
        }

        return entity;
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Protect(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Protect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Restore(eventData.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Restore(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => Restore(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return Task.CompletedTask;
    }

    private void Protect(DbContext? context)
    {
        if (context is not AuthDbContext { SecretProtectorForStorage: { } protector })
        {
            return;
        }

        var restore = new List<(EntityEntry<IdentityProvider>, string?, string?)>();
        foreach (var entry in context.ChangeTracker.Entries<IdentityProvider>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var configPlaintext = entry.Entity.ConfigJson;
            var configProtected = ProviderConfigSecrets.Protect(configPlaintext, protector);
            var specificPlaintext = entry.Entity.ProviderSpecificConfigJson;
            var specificProtected = ProviderConfigSecrets.Protect(specificPlaintext, protector);

            var changed = false;
            if (!string.Equals(configProtected, configPlaintext, StringComparison.Ordinal))
            {
                entry.Entity.ConfigJson = configProtected;
                changed = true;
            }

            if (!string.Equals(specificProtected, specificPlaintext, StringComparison.Ordinal))
            {
                entry.Entity.ProviderSpecificConfigJson = specificProtected;
                changed = true;
            }

            if (changed)
            {
                restore.Add((entry, configPlaintext, specificPlaintext));
            }
        }

        _pending.AddOrUpdate(context, restore);
    }

    private void Restore(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var restore))
        {
            return;
        }

        _pending.Remove(context);
        foreach (var (entry, configPlaintext, specificPlaintext) in restore)
        {
            if (entry.State == EntityState.Detached)
            {
                continue;
            }

            var configProperty = entry.Property(p => p.ConfigJson);
            var specificProperty = entry.Property(p => p.ProviderSpecificConfigJson);
            var accepted = entry.State == EntityState.Unchanged;
            entry.Entity.ConfigJson = configPlaintext;
            configProperty.CurrentValue = configPlaintext;
            entry.Entity.ProviderSpecificConfigJson = specificPlaintext;
            specificProperty.CurrentValue = specificPlaintext;
            if (accepted)
            {
                // The rows now hold the protected values; in memory the entity is plaintext and unchanged.
                configProperty.OriginalValue = configPlaintext;
                configProperty.IsModified = false;
                specificProperty.OriginalValue = specificPlaintext;
                specificProperty.IsModified = false;
            }
        }
    }
}
