extern alias provider;

using System.Reflection;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A host's password key is wiped however hosting ends. Stopping wipes a key derived for an attempt not yet open, and the
/// open host's own copy; a key still being derived when hosting stops is wiped the moment it is done, and starts nothing;
/// a host that could not start wipes its copy at once. A late derivation wipes only its own key, never a newer attempt's.
/// </summary>
internal static class HostKeyWipeTests
{
    internal static void StoppingWipesTheKeyHoweverHostingEnds()
    {
        // A key derived for an attempt that has not opened yet: no frame has run, so hosting waits, as it does while the
        // ban list is read. Stopping wipes it at once.
        using (var rig = new LiveRig(203))
        {
            var live = rig.Host.Live!;
            live.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
            var held = KeyTask(live).GetAwaiter().GetResult()!;
            That(held.Any(value => value != 0) && live.HostPhase == HostPhase.Preparing, "the key is derived and held while hosting waits");
            live.StopHosting();
            That(SpinWait.SpinUntil(() => held.All(value => value == 0), 5_000) && live.HostPhase == HostPhase.Idle,
                "stopping wipes a key derived for an attempt not yet open");
        }

        // A key still being derived when hosting stops: the derivation wipes its own result once it is done, and opens nothing.
        using (var rig = new LiveRig(204))
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.HostFiles.BeforeRead = path => path == IdentityStore.IdentityPath ? release.Task : Task.CompletedTask;
            var live = rig.Host.Live!;
            live.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
            var deriving = KeyTask(live);
            That(!deriving.IsCompleted, "the key is still being derived, the identity it is salted with still loading");
            live.StopHosting();
            release.SetResult();
            var late = deriving.GetAwaiter().GetResult()!;
            That(SpinWait.SpinUntil(() => late.All(value => value == 0), 5_000),
                "a derivation that completes after hosting stopped leaves its bytes all zero once it is done");
            rig.Run(() => false, 500_000);
            That(live.HostPhase == HostPhase.Idle && live.Host == null && rig.HostFactory.Networks.Count == 0, "and starts nothing");
        }

        // An open host keeps its own copy in its policy; stopping wipes that copy.
        using (var rig = new LiveRig(205))
        {
            var live = rig.Host.Live!;
            rig.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
            That(live.HostPhase == HostPhase.Hosting, "the host opens");
            var copy = PolicyKey(live.Host!.Policy);
            That(copy.Any(value => value != 0), "its policy holds the key");
            live.StopHosting();
            That(copy.All(value => value == 0), "stopping an open host wipes its policy's key");
        }

        // A host that cannot start, here because no port can be bound, wipes its policy's copy at once.
        using (var rig = new LiveRig(206))
        using (var identity = LocalIdentity.Create(1_760_000_000))
        {
            rig.HostFactory.FailStarts = true;
            var host = new LiveHost(rig.Host.Engine, identity, new BanList(), new HostSettings("Unbound") { Port = 0 }, rig.HostContent,
                () => rig.Network.Now, () => 1_760_000_000, Enumerable.Repeat((byte)7, 32).ToArray());
            var copy = PolicyKey(host.Policy);
            That(copy.All(value => value == 7), "a host's policy holds its own copy of the key");
            That(host.Start(rig.HostFactory) == LiveFailure.PortInUse, "the host cannot bind a port");
            That(copy.All(value => value == 0), "a host that could not start wipes its policy's key at once");
        }
    }

    internal static void ALateDerivationWipesOnlyItsOwnKey()
    {
        // The first attempt stops while its key is derived, and a second begins before either derivation is done.
        using var rig = new LiveRig(207);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.HostFiles.BeforeRead = path => path == IdentityStore.IdentityPath ? release.Task : Task.CompletedTask;
        var live = rig.Host.Live!;
        live.StartHosting(new HostSettings("First") { Port = 0, Password = "first password" });
        var first = KeyTask(live);
        live.StopHosting();
        live.StartHosting(new HostSettings("Second") { Port = 0, Password = "second password" });
        var second = KeyTask(live);
        That(!first.IsCompleted && !second.IsCompleted && !ReferenceEquals(first, second), "both keys are still being derived");
        release.SetResult();
        var firstKey = first.GetAwaiter().GetResult()!;
        var secondKey = second.GetAwaiter().GetResult()!;
        That(SpinWait.SpinUntil(() => firstKey.All(value => value == 0), 5_000), "the first attempt's late derivation wipes its own key");
        var expected = secondKey.ToArray();
        That(expected.Any(value => value != 0), "and leaves the second attempt's key whole");

        // The second attempt opens with exactly the key derived for it.
        That(rig.Run(() => live.HostPhase == HostPhase.Hosting) && live.Host!.Descriptor!.Descriptor.Name == "Second", "the second attempt opens");
        That(PolicyKey(live.Host!.Policy).SequenceEqual(expected), "with the key derived for it, whole");
    }

    /// <summary>Closing releases the attempt's key at every stage, including a derivation that finishes after disposal.</summary>
    internal static void ClosingWipesTheHostKeyAtEveryStage()
    {
        foreach (var late in new[] { false, true })
        {
            using var rig = new LiveRig(late ? 209 : 208);
            LiveRig.SeedIdentity(rig.HostFiles, late ? 2091 : 2081);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (late) rig.HostFiles.BeforeRead = path => path == IdentityStore.IdentityPath ? release.Task : Task.CompletedTask;
            var live = rig.Host.Live!;
            live.StartHosting(new HostSettings("Closing") { Port = 0, Password = "closing password" });
            var pending = KeyTask(live);
            if (late) That(!pending.IsCompleted, "closing starts while the key is still being derived");
            else That(pending.GetAwaiter().GetResult()!.Any(value => value != 0), "closing starts with a prepared key");
            try
            {
                rig.Host.Dispose();
            }
            finally
            {
                release.TrySetResult();
            }

            var key = pending.GetAwaiter().GetResult()!;
            That(SpinWait.SpinUntil(() => key.All(value => value == 0), 5_000),
                late ? "closing wipes a derivation that completes after disposal" : "closing wipes a prepared key");
            That(live.HostPhase == HostPhase.Idle && live.Host == null && rig.HostFactory.Networks.Count == 0,
                "closing never opens a prepared or late host");
        }

        using var opened = new LiveRig(210);
        opened.StartHosting(new HostSettings("Closing open host") { Port = 0, Password = "closing password" });
        var host = opened.Host.Live!.Host!;
        var policyKey = PolicyKey(host.Policy);
        That(policyKey.Any(value => value != 0), "the open host holds its policy key before closing");
        opened.Host.Dispose();
        That(policyKey.All(value => value == 0) && !host.IsRunning, "closing stops the open host and wipes its policy key");
    }

    /// <summary>The password key a hosting attempt is deriving, or has derived, before the host takes its own copy.</summary>
    private static Task<byte[]?> KeyTask(LiveSessions live)
    {
        var field = typeof(LiveSessions).GetField("hostKey", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    throw new InvalidOperationException("Assertion failed: live sessions hold the key being derived");
        return (Task<byte[]?>?)field.GetValue(live) ?? throw new InvalidOperationException("Assertion failed: a password host derives a key");
    }

    /// <summary>The copy of the password key a host's policy keeps.</summary>
    private static byte[] PolicyKey(HostPolicy policy)
    {
        var field = typeof(HostPolicy).GetField("passwordKey", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    throw new InvalidOperationException("Assertion failed: a host's policy keeps the password key");
        return (byte[]?)field.GetValue(policy) ?? throw new InvalidOperationException("Assertion failed: a password host's policy holds a key");
    }
}
