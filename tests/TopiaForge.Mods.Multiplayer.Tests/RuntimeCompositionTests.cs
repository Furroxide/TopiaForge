extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using Control = provider::TopiaForge.Multiplayer.InternalControl;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using RoutedDiagnostics = provider::TopiaForge.Multiplayer.RoutedDiagnostics;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// ModManager's composition root over the real provider: who pumps the provider, attaching and detaching, and the cost
/// of the offline frame, which must allocate nothing.
/// </summary>
internal static class RuntimeCompositionTests
{
    private const long FrameMicros = 16_667;

    internal static void TheCompositionRootDrivesTheProvider()
    {
        var clock = 0L;
        using var provider = new Provider(LiveRig.ProviderId, null, () => clock);
        using var control = new Control(provider, new RuntimeContent("2545", "0.2.0"), new RoutedDiagnostics(null));
        var log = new RecordingMultiplayerLog();
        var platform = new RecordingPlatform();
        var host = new MultiplayerRuntimeHost("2545", "0.2.0", log, platform);
        var runtime = new MultiplayerRuntime(() => control, host, log);
        var ticks = 0;
        using var counting = provider.SubscribeTick(_ => ticks++);

        clock = 200_000;
        control.PumpUnattached();
        That(ticks == 4, "with no runtime attached the provider pumps itself (" + ticks + " steps)");
        That(runtime.Connect() && ReferenceEquals(runtime.Control, control) && ReferenceEquals(control.Runtime, host), "the runtime attaches to the provider's control");
        That(runtime.Connect() && ReferenceEquals(runtime.Control, control), "attaching again changes nothing");
        clock = 400_000;
        control.PumpUnattached();
        That(ticks == 4, "once attached, the provider's own update no longer pumps");
        runtime.BeforeModsUpdate(0.016f);
        That(ticks == 8 && runtime.Role == InternalSessionRole.Solo, "the runtime's first frame step pumps it instead");

        var other = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
        That(!other.Connect() && other.Control == null && log.Lines.Any(line => line.Contains("another runtime", StringComparison.Ordinal)),
            "a second runtime cannot drive the same provider");

        runtime.Disconnect();
        That(runtime.Control == null && control.Runtime == null && runtime.Role == InternalSessionRole.Solo, "detaching lets go of the provider");
        clock = 600_000;
        control.PumpUnattached();
        That(ticks == 12, "which pumps itself again");
        That(new MultiplayerRuntime(() => null, host, log).Connect() == false, "a process without the provider stays offline");
        That(new MultiplayerRuntime(() => throw new InvalidOperationException("registry"), host, log).Connect() == false &&
             log.Lines.Any(line => line.StartsWith("error:", StringComparison.Ordinal)), "a registry that fails is reported, not thrown");
    }

    internal static void TheOfflineFrameAllocatesNothing()
    {
        // No provider: each hook is one field read.
        var log = new RecordingMultiplayerLog();
        var offline = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
        offline.Connect();
        That(Measure(() => Frame(offline)) == 0, "the frame without a provider allocates nothing");

        // A provider in single player: the frame pumps the solo engine and nothing else.
        var clock = 0L;
        using (var provider = new Provider(LiveRig.ProviderId, null, () => clock))
        using (var control = new Control(provider, new RuntimeContent("2545", "0.2.0"), new RoutedDiagnostics(null)))
        {
            var solo = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            That(solo.Connect(), "the runtime attaches to a solo provider");
            var allocated = Measure(() =>
            {
                clock += FrameMicros;
                Frame(solo);
            });
            That(allocated == 0, "the solo frame allocates nothing (" + allocated + " bytes)");
        }

        // Live sessions switched on but idle cost nothing per frame either, once the identity has loaded. Only the frame is
        // measured: the virtual network's own bookkeeping between frames is not the game's.
        var network = new VirtualDatagramNetwork(160);
        var root = Directory.CreateTempSubdirectory("TopiaForgeIdle-").FullName;
        var peer = new ControlPeer("idle", network, new SteppedResolver(), root, Array.Empty<PackageSpec>());
        try
        {
            That(peer.Composition.Connect(), "the runtime attaches to a provider with live sessions");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!peer.Provider.Live!.IsReady && DateTime.UtcNow < deadline)
            {
                peer.Frame();
                Thread.Sleep(1);
            }

            That(peer.Provider.Live.IsReady, "the identity loads off the main thread");
            for (var index = 0; index < 64; index++)
            {
                network.RunFor(FrameMicros);
                peer.Frame();
            }

            var idle = 0L;
            for (var index = 0; index < 10_000; index++)
            {
                network.RunFor(FrameMicros);
                var before = GC.GetAllocatedBytesForCurrentThread();
                peer.Frame();
                idle += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            That(idle == 0, "an idle live provider's frame allocates nothing (" + idle + " bytes)");
        }
        finally
        {
            peer.Composition.Disconnect();
            peer.Control.Dispose();
            peer.Provider.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static void Frame(MultiplayerRuntime runtime)
    {
        runtime.BeforeModsUpdate(0.016f);
        runtime.AfterModsUpdate(0.016f);
        runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
    }

    private static long Measure(Action frame)
    {
        for (var index = 0; index < 64; index++) frame();
        _ = GC.GetAllocatedBytesForCurrentThread();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++) frame();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
