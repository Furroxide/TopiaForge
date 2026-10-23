extern alias provider;

using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Each rig identifies stuck work by its test peer and operation, without a network address or task result.</summary>
internal static class BackgroundWorkWaitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(100);

    internal static void LiveTimeoutNamesEveryPeerAndOperation()
    {
        using var rig = new LiveRig(8_011) { BackgroundWorkBound = Bound };
        var peer = rig.AddClient("Sam");
        using var held = new HeldIdentity();
        held.Hold(rig.HostFiles, rig.Host.Live!);
        held.Hold(rig.ClientFiles(peer), peer.Live!);
        var before = rig.Network.Now;
        TimesOut(() => rig.Run(() => false), "host: identity load; Sam: identity load");
        That(rig.Network.Now == before, "stuck real work never advances the live rig's virtual clock");
    }

    internal static void ControlTimeoutNamesThePeerAndOperation()
    {
        using var rig = new ControlRig(8_012) { BackgroundWorkBound = Bound };
        var peer = rig.AddPeer("Sam");
        using var held = new HeldIdentity();
        held.Hold(peer.Files, peer.Provider.Live!);
        var before = rig.Network.Now;
        TimesOut(() => rig.Run(() => false), "Sam: identity load");
        That(rig.Network.Now == before, "stuck real work never advances the control rig's virtual clock");
    }

    internal static void InternetTimeoutNamesThePeerAndOperation()
    {
        using var rig = new InternetRig(8_013) { BackgroundWorkBound = Bound };
        var peer = rig.AddPeer("Sam", NatBehaviour.Symmetric, "198.51.100.10", "192.168.1.10");
        using var held = new HeldIdentity();
        held.Hold(peer.Files, peer.Provider.Live!);
        var before = rig.Now;
        TimesOut(() => rig.Run(() => false), "Sam: identity load");
        That(rig.Now == before, "stuck real work never advances the Internet rig's virtual clock");
    }

    private static void TimesOut(Func<bool> run, string operations)
    {
        string? message = null;
        try { run(); }
        catch (InvalidOperationException exception) { message = exception.Message; }
        That(message == "Assertion failed: background work exceeded 0.1 s of real time: " + operations,
            "the timeout names exactly the stuck test peers and operations, without an address");
    }

    private sealed class HeldIdentity : IDisposable
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> loads = new();

        internal void Hold(MemoryDataFiles files, LiveSessions live)
        {
            files.BeforeRead = _ => release.Task;
            loads.Add(live.PrepareAsync());
        }

        public void Dispose()
        {
            release.TrySetResult(true);
            Task.WhenAll(loads).GetAwaiter().GetResult();
        }
    }
}
