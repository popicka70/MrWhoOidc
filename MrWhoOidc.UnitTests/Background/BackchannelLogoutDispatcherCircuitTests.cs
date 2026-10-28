using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests.Background;

/// <summary>
/// M-Race: the backchannel dispatcher delivers several logout tokens in parallel, and every delivery may
/// bump, reset or open the circuit of its client. The per-client state was kept in a plain
/// <c>Dictionary</c> and mutated without synchronization, so a concurrent add could corrupt the
/// dictionary and the failure counts could be lost.
/// </summary>
[TestClass]
public sealed class BackchannelLogoutDispatcherCircuitTests
{
    private static BackchannelLogoutDispatcher CreateDispatcher(
        BackchannelRuntimeState state,
        BackchannelDispatchOptions? options = null)
    {
        var feature = new Mock<IOptionsMonitor<BackchannelFeatureOptions>>();
        feature.Setup(f => f.CurrentValue).Returns(new BackchannelFeatureOptions { Enabled = true });

        return new BackchannelLogoutDispatcher(
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<BackchannelLogoutDispatcher>.Instance,
            Mock.Of<IOidcMetrics>(),
            Mock.Of<IAlertPublisher>(),
            Mock.Of<MrWhoOidc.WebAuth.Observability.IAuditSink>(),
            options ?? new BackchannelDispatchOptions(),
            feature.Object,
            state,
            Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>());
    }

    [TestMethod]
    public async Task ConcurrentBumps_DoNotLoseFailureCounts()
    {
        var state = new BackchannelRuntimeState();
        var dispatcher = CreateDispatcher(state, new BackchannelDispatchOptions
        {
            // Never open on the failure count, so every bump only increments.
            CircuitOpenFailures = int.MaxValue
        });

        const int workers = 16;
        const int perWorker = 250;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                dispatcher.BumpCircuit("client-a");
            }
        })));

        Assert.AreEqual(workers * perWorker, state.Circuits["client-a"].Failures,
            "every concurrent failure must be counted");
    }

    [TestMethod]
    public async Task ConcurrentBumpsAndResets_DoNotCorruptTheState()
    {
        var state = new BackchannelRuntimeState();
        var dispatcher = CreateDispatcher(state, new BackchannelDispatchOptions
        {
            CircuitOpenFailures = int.MaxValue
        });

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            if (i % 4 == 0)
            {
                dispatcher.ResetCircuit("client-b");
            }
            else
            {
                dispatcher.BumpCircuit("client-b");
            }
        })));

        var snapshot = state.Circuits["client-b"];
        Assert.IsTrue(snapshot.Failures >= 0);
        Assert.IsFalse(dispatcher.IsCircuitOpen("client-b"), "the circuit must never be open with an unreachable threshold");
    }

    [TestMethod]
    public void Circuit_Opens_AfterTheConfiguredNumberOfFailures()
    {
        var state = new BackchannelRuntimeState();
        var dispatcher = CreateDispatcher(state, new BackchannelDispatchOptions
        {
            CircuitOpenFailures = 3,
            CircuitOpenDuration = TimeSpan.FromMinutes(5)
        });

        dispatcher.BumpCircuit("client-c");
        dispatcher.BumpCircuit("client-c");
        Assert.IsFalse(dispatcher.IsCircuitOpen("client-c"));

        dispatcher.BumpCircuit("client-c");
        Assert.IsTrue(dispatcher.IsCircuitOpen("client-c"), "the circuit must open after the configured failures");
        Assert.AreEqual(3, state.Circuits["client-c"].Failures);

        dispatcher.ResetCircuit("client-c");
        Assert.IsFalse(dispatcher.IsCircuitOpen("client-c"), "a successful delivery must close the circuit");
        Assert.AreEqual(0, state.Circuits["client-c"].Failures);
    }
}
