using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Helpers;

public sealed class StubLoginContinuationStore : ILoginContinuationStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private int _counter;

    public Task<string> StoreAsync(string continuation, CancellationToken cancellationToken)
    {
        var key = "ctx-" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
        _values[key] = continuation;
        return Task.FromResult(key);
    }

    public Task<string?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
