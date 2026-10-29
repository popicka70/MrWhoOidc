using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// The protector of the device authorization grant's codes (M-Plain). Tests use it to seed and look up codes the
/// way the server does.
/// </summary>
internal static class TestDeviceCodes
{
    /// <summary>
    /// A protector with a keyed hash, like a deployment that configures <c>Security:LookupHashKey</c>. Without a key
    /// (<paramref name="lookupHashKey"/> null) the user code is protected reversibly instead.
    /// </summary>
    public static IDeviceCodeProtector Protector(string? lookupHashKey = "unit-test-lookup-hash-key-0123456789") =>
        new DeviceCodeProtector(
            Options.Create(new LookupHashOptions { LookupHashKey = lookupHashKey ?? string.Empty }),
            new StubSecretProtector(),
            NullLogger<DeviceCodeProtector>.Instance);
}
