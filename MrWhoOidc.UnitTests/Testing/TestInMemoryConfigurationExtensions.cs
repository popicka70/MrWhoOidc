using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace MrWhoOidc.UnitTests.Testing;

public static class TestInMemoryConfigurationExtensions
{
    public static IConfigurationBuilder AddTestInMemoryCollection(
        this IConfigurationBuilder builder,
        IDictionary<string, string?> initialData)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(initialData);
        return builder.Add(new TestInMemoryConfigurationSource(initialData));
    }

    private sealed class TestInMemoryConfigurationSource(IDictionary<string, string?> initialData) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
            => new TestInMemoryConfigurationProvider(initialData);
    }

    private sealed class TestInMemoryConfigurationProvider(IDictionary<string, string?> initialData) : ConfigurationProvider
    {
        public override void Load()
        {
            foreach (var kvp in initialData)
            {
                if (kvp.Key is null)
                {
                    continue;
                }

                Data[kvp.Key] = kvp.Value;
            }
        }
    }
}
