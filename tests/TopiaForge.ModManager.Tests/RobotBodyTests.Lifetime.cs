using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        private static void LifetimeAndCatchup()
        {
            var rig = NewRig();
            using var host = new Bodies(rig.Host);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var service = host.Bind(owner);
            var body = service.Spawn(Request()).Value!;
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("guest"); using var guest = new Bodies(player);
            rig.JoinAndSynchronize(player);
            Check(guest.Native.Count == 1 && guest.Native[0].Appearance.Equals(body.Appearance), "offline body enters late-join baseline");
            Check(guest.Native[0].Position.Equals(body.Position), "replica captured position");
            var original = guest.Native[0];
            var networkId = original.Object.Mark;
            player.Control.Leave(); rig.Frames(3);
            Check(!original.IsAlive && body.IsAlive, "departure cleans replica but not server body");
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros + 1); rig.Frames(3);
            Check(body.IsAlive, "participant departure grace does not own body");
            rig.JoinAndSynchronize(player);
            Check(guest.Native.Count == 2 && guest.Native[1].Object.Mark == networkId, "reconnect restores same canonical identity");
            guest.Native[1].Object.Destroy();
            rig.Clock.Advance(5_000_001);
            Check(rig.Run(() => guest.Native.Count == 3 && guest.Native[2].IsAlive), "remote visual teardown rebuilds from retained payload");
            Check(guest.Native[2].Object.Mark == networkId && guest.Native[2].Appearance.Equals(Appearance), "rebuild retains identity and appearance");
            var scene = player.Scene.ActiveScene!;
            player.UnloadScene(scene);
            player.Scene.AddScene("Replacement"); player.LoadScenes();
            guest.Provider.EndWorld();
            rig.Clock.Advance(5_000_001);
            Check(rig.Run(() => guest.Native.Count == 4 && guest.Native[3].IsAlive), "actual remote scene replacement rebuilds the retained host body");
            Check(body.IsAlive && guest.Native[3].Object.Mark == networkId, "remote scene does not retire host lease or canonical identity");
            var pump = typeof(NetworkWorldClient).GetMethod("PumpProviders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .CreateDelegate<Action<long>>(player.Client);
            for (var i = 0; i < 200; i++) pump(rig.Clock.Now);
            var firstAllocation = -1;
            long firstBytes = 0;
            var elapsedStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long firstElapsed = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++)
            {
                pump(rig.Clock.Now);
                var current = GC.GetAllocatedBytesForCurrentThread() - before;
                if (current != 0 && firstAllocation < 0)
                { firstAllocation = i; firstBytes = current; firstElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - elapsedStart; }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated != 0) Console.WriteLine("First recovery allocation: frame " + firstAllocation + ", bytes " + firstBytes
                + ", elapsed ticks " + firstElapsed + ", frequency " + System.Diagnostics.Stopwatch.Frequency);
            Check(allocated == 0, "idle provider recovery allocates exactly zero bytes: " + allocated);
            Console.WriteLine("Idle provider recovery: " + allocated + " bytes (200 warmups, 10000 frames).");
            body.Dispose(); body.Dispose();
            Check(!body.IsAlive && rig.Run(() => guest.Native.All(copy => !copy.IsAlive)), "explicit lease release automatically despawns replica");
            var another = service.Spawn(Request(9)).Value!;
            Check(rig.Run(() => guest.Native.Any(copy => copy.IsAlive)), "second body replicated");
            player.Control.Leave(); rig.Frames(3);
            owner.Lifetime.Dispose();
            Check(!another.IsAlive, "scope cleanup releases host while recipient absent");
            rig.JoinAndSynchronize(player);
            Check(guest.Native.All(copy => !copy.IsAlive), "reconnect cannot resurrect missed scope cleanup");
            var replacement = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var next = host.Bind(replacement).Spawn(Request()).Value!;
            Check(next.Id != another.Id && rig.Run(() => guest.Native.Any(copy => copy.IsAlive)), "replacement scope owns distinct body");
            host.Provider.EndWorld();
            Check(!next.IsAlive && rig.Run(() => guest.Native.All(copy => !copy.IsAlive)), "world replacement actually cleans replicas");
        }

        private static void ProviderRecovery()
        {
            var rig = NewRig(); using var host = new Bodies(rig.Host);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var service = host.Bind(owner);
            host.Refuse = true;
            var refused = service.Spawn(Request());
            Check(!refused.Succeeded && !refused.ErrorMessage.Contains("private", StringComparison.Ordinal), "provider refusal returns safe error");
            host.Refuse = false; host.Throw = true;
            Check(service.Spawn(Request()).ErrorCode == ModErrorCode.External && host.Native.Count == 0, "provider exception leaves no successful lease");
            host.Throw = false;
            var body = service.Spawn(Request()).Value!;
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("guest");
            rig.JoinAndSynchronize(player); // No provider yet: retained canonical description must be retried.
            using var guest = new Bodies(player) { Refuse = true };
            rig.Clock.Advance(5_000_001); rig.Frames(2);
            Check(guest.Attempts == 1 && guest.Native.Count == 0, "provider refusal creates no fallback");
            rig.Frames(100);
            Check(guest.Attempts == 1, "failed provider is rate limited");
            guest.Refuse = false;
            rig.Clock.Advance(5_000_001);
            Check(rig.Run(() => guest.Native.Count == 1), "provider appearing after baseline catches up automatically");
            var old = guest.Native[0];
            guest.Dispose(); rig.Frames(2);
            Check(!old.IsAlive && body.IsAlive, "remote provider loss removes actual replica only");
            using var replaced = new Bodies(player);
            rig.Clock.Advance(5_000_001);
            Check(rig.Run(() => replaced.Native.Count == 1), "replacement provider rebuilds current body");
            host.Dispose();
            Check(!body.IsAlive && rig.Run(() => replaced.Native.All(copy => !copy.IsAlive)), "host provider loss automatically despawns all replicas");
        }

        private static void TestingKit()
        {
            using var lifetime = new FakeModLifetime();
            using var service = new FakeRobotBodyService(lifetime);
            var body = service.Spawn(Request()).Value!;
            Check(body.Appearance.Equals(Appearance) && body.Position.Equals(Request().Position), "fake captures values");
            body.Dispose();
            Check(service.Bodies.Count == 0 && lifetime.TrackedResourceCount == 0, "explicit fake disposal releases tracking");
            service.IsAuthoritative = false;
            Check(service.Spawn(Request()).ErrorCode == ModErrorCode.NotAuthoritative, "fake authority refusal");
            service.IsAuthoritative = true; service.IsAvailable = false;
            Check(service.Spawn(Request()).ErrorCode == ModErrorCode.Unavailable, "fake provider refusal");
            service.IsAvailable = true;
            var retained = service.Spawn(Request()).Value!;
            lifetime.Dispose();
            Check(!retained.IsAlive && service.Spawn(Request()).ErrorCode == ModErrorCode.Cancelled, "fake lifetime matches contract");
        }

        private static void NativeConstructionContract()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "TopiaForge.slnx"))) root = root.Parent;
            var source = File.ReadAllText(Path.Combine(root!.FullName, "mods", "TopiaForge.RobotKit", "RobotPersistentBody.cs"));
            Check(source.Contains("Instantiate(prefab, incubator!.transform, false)", StringComparison.Ordinal)
                && source.Contains("clone.SetActive(false)", StringComparison.Ordinal)
                && !source.Contains("clone.SetActive(true)", StringComparison.Ordinal), "native composition clone remains inactive");
            Check(!source.Contains(".Kill(", StringComparison.Ordinal) && !source.Contains("ApplyDamage(", StringComparison.Ordinal)
                && !source.Contains("AddComponent<Rigidbody>", StringComparison.Ordinal) && !source.Contains("AddComponent<Animator>", StringComparison.Ordinal), "persistent visual has no native death or physics activation");
            Check(source.IndexOf("RobotAvatarAppearance.Apply", StringComparison.Ordinal) < source.IndexOf("RobotBodyVisual.Copy", StringComparison.Ordinal)
                && source.IndexOf("RobotBodyVisual.Copy", StringComparison.Ordinal) < source.IndexOf("anchor.SetActive(true)", StringComparison.Ordinal), "compose and snapshot before activating visual");
        }
    }
}
