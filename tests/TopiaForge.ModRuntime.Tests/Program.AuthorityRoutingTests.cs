using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string AuthorityModEntry = "TopiaForge.ValidTestMod.RuntimeAuthorityMod";
        private const string AuthorityMod = "tests.authority";

        /// <summary>
        /// Authority routing in the real runtime (WS3.7): three games over the loopback network, each loading the same mod. A
        /// joined player's run of the mod's scene script shadows the host's crate, so each sees one; its player's dropped
        /// crate is adopted by the host as the player's own, under the same handle, and moves and goes for everyone at the
        /// player's command; a crate the host refuses dies with a toast; a player who leaves takes its crates with it once
        /// its grace is over, and nothing else; and the frames allocate nothing offline or online at rest.
        /// </summary>
        private static void TestAuthorityRoutingAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new WorldGame(NewFixture(Path.Combine(root, "authority-host"), "authority", AuthorityModEntry), network.Host, clock);
            var player = new WorldGame(NewFixture(Path.Combine(root, "authority-player"), "authority", AuthorityModEntry), network.AddPlayer("p1"), clock);
            var other = new WorldGame(NewFixture(Path.Combine(root, "authority-other"), "authority", AuthorityModEntry), network.AddPlayer("p2"), clock);
            var games = new[] { host, player, other };
            Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", Path.Combine(root, "authority-trace.txt"));
            try
            {
                foreach (var game in games) game.Start();

                // Single player: the host loads the yard, and the mod's script spawns a crate there, its first shadowable spawn.
                host.LoadScene("Yard", true);
                Assert(Crates(host) == 1 && host.Runtime.Multiplayer.Spawns.Live().Single().ShadowKey == 1, "the host's script spawns the yard's crate, numbered one");
                var offline = MeasureWorldFrames(host);
                Assert(offline == 0, "offline, a frame with the mod and its crate allocates nothing (" + offline + " bytes over 10,000 frames)");
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("World", 4)).Succeeded, "the host opens its game");
                WorldFrames(games, clock, 3);

                // Each player joins and loads the yard: its own run of the script shadows the host's crate, so it sees one.
                foreach (var joining in new[] { player, other })
                {
                    Assert(joining.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a player joins");
                    Assert(WorldRunUntil(games, clock, () => joining.World.Progress.Phase == NetworkWorldPhase.Synchronized && !joining.World.IsHoldingReadiness, 120),
                        "it synchronizes (" + joining.World.Progress + ")");
                    joining.LoadScene("Yard", false);
                    WorldFrames(games, clock, 2);
                    Assert(Crates(joining) == 1 && joining.World.Counters.ShadowsBound == 1 && joining.Runtime.Multiplayer.Spawns.Live().Count == 0,
                        "its script's crate stands for the host's: one crate, not two, and nothing recorded (" + Crates(joining) + ")");
                }

                // The player drops a crate: made at once, then adopted by the host as the player's, under the same handle.
                var handle = Drop(player, "4");
                Assert(WorldRunUntil(games, clock, () => player.World.Client.OwnedObjects == 1, 60), "the host adopts the player's crate");
                var id = new NetworkEntityId(handle.Object.Mark);
                Assert(handle.IsAlive && player.World.Client.TryGetEntity(id, out var bound) && bound.Id == handle.Id &&
                       player.World.Client.BindingOf(id) == ClientEntityBinding.Own, "the mod's own handle stands for it, under the same id");
                Assert(host.World.Host.TryGetHosted(id, out var hosted) && hosted.OwnerSlot != 0, "on the host it is the player's");
                Assert(WorldRunUntil(games, clock, () => other.World.Client.BindingOf(id) == ClientEntityBinding.Asset, 60) && Crates(other) == 2 && Crates(player) == 2 &&
                       Crates(host) == 2, "and everyone sees it: two crates each");

                // It moves, then goes, at the player's command, for everyone.
                Assert(player.Runtime.TryExecutePlayerCommand(AuthorityMod + ":move", new[] { "8" }, out var moved) && moved!.Succeeded, "the player moves its crate");
                var hostCrate = (FakeWorldObject)hosted.Object!;
                Assert(WorldRunUntil(games, clock, () => Near(hostCrate.Position, new NetVector3(8, 1, 0)) && Near(handle.Object.Position, new NetVector3(8, 1, 0)) &&
                       other.World.Client.TryGetEntity(id, out var seen) && seen is FakeWorldEntity replica && Near(replica.Object.Position, new NetVector3(8, 1, 0)), 120),
                    "the host moves it, and both players see it there");
                Assert(player.Runtime.TryExecutePlayerCommand(AuthorityMod + ":remove", Array.Empty<string>(), out var removed) && removed!.Succeeded, "the player removes it");
                Assert(WorldRunUntil(games, clock, () => !handle.IsAlive && !host.World.Host.Knows(id) && !other.World.Client.Knows(id), 60),
                    "the host ends it for everyone, and the handle dies with it");

                // A crate the host will not make, from a bundle outside the mod's package, dies with a toast.
                var refused = Drop(player, "6", "../outside.bundle");
                Assert(WorldRunUntil(games, clock, () => !refused.IsAlive, 60), "the host refuses a crate from outside the mod's package, and its handle dies");
                var toast = player.Game.Toasts.Single();
                Assert(toast.Owner == AuthorityMod && toast.Tone == UiTone.Warning && toast.Message == SpawnAuthorityRules.RefusalText(SpawnRefusal.NotPermitted),
                    "the player is told why, once, through the mod's own toasts");
                Assert(Crates(host) == 1 && Crates(other) == 1 && Crates(player) == 1, "nothing of it stands anywhere");

                // At rest, with the player holding adopted crates, nothing allocates.
                Drop(player, "1");
                Drop(player, "2");
                Assert(WorldRunUntil(games, clock, () => player.World.Client.OwnedObjects == 2 && Crates(other) == 3, 60), "two more crates are adopted");
                WorldFrames(games, clock, 240);
                var resting = MeasureGames(games, clock);
                Assert(resting == 0, "online at rest, a host and two players allocate nothing (" + resting + " bytes over 2,000 frames)");

                // The player leaves: its copies of the host's world end at once, its shadow and its own crates among them, and the
                // host ends its crates once its grace is over, and nothing else.
                Assert(player.Runtime.Multiplayer.Leave().Succeeded, "the player leaves");
                WorldFrames(games, clock, 3);
                Assert(Crates(player) == 0 && Crates(host) == 3, "its copies ended with its leaving, and the host keeps its crates through its grace");
                clock.Now += SpawnAuthorityRules.DepartureGraceMicros;
                Assert(WorldRunUntil(games, clock, () => Crates(host) == 1 && Crates(other) == 1, 60), "then the host ends them, for everyone");
                Assert(host.Runtime.Multiplayer.Spawns.Live().Single().ShadowKey == 1 && host.Runtime.IsLoaded(AuthorityMod) && host.World.Proxies.ProxyCount == 0,
                    "the mod's own crate and the mod stand: only the leaver's spawns ended");
                foreach (var game in games) Assert(game.Fixture.Logger.Errors.Count == 0, "no game logged an error (" + string.Join(" | ", game.Fixture.Logger.Errors) + ")");
            }
            finally
            {
                foreach (var game in games) game.Runtime.UnloadAll();
            }
        }

        /// <summary>Runs the mod's drop command as the player, and finds the handle the mod holds by the id it gave.</summary>
        private static FakeSpawnedEntity Drop(WorldGame game, params string[] arguments)
        {
            Assert(game.Runtime.TryExecutePlayerCommand(AuthorityMod + ":drop", arguments, out var dropped) && dropped!.Succeeded, "the player drops a crate (" + dropped + ")");
            var id = dropped!.Value;
            return game.PackageAssets.Spawned.Single(item => item.Id == id);
        }

        /// <summary>Counts the crates a game's scene holds: what its player sees.</summary>
        private static int Crates(WorldGame game) => game.Game.Scene.AllObjects().Count(item => item.IsAlive && item.Name == "Crate");

        /// <summary>Counts what 2,000 frames of every game allocate, the fewest bytes of up to three passes after one to warm.</summary>
        private static long MeasureGames(WorldGame[] games, WorldTestClock clock)
        {
            GamesPass(games, clock, 2_000);
            var allocated = long.MaxValue;
            for (var pass = 0; pass < 3 && allocated != 0; pass++) allocated = Math.Min(allocated, GamesPass(games, clock, 2_000));
            return allocated;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long GamesPass(WorldGame[] games, WorldTestClock clock, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                for (var game = 0; game < games.Length; game++)
                {
                    games[game].Runtime.DispatchUpdate(0f);
                    games[game].Game.RaiseLateUpdate();
                }

                clock.Now += 16_000;
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>A UI service that records each toast with the mod that showed it, and leaves the rest to the unavailable one.</summary>
        private sealed class WorldToasts : IUiService
        {
            private readonly WorldGameHost host;
            private readonly string owner;
            private readonly IUiService other;

            internal WorldToasts(WorldGameHost host, string owner, IUiService other)
            {
                this.host = host;
                this.owner = owner;
                this.other = other;
            }

            public UiAccessibilityPreferences Accessibility => other.Accessibility;

            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) => other.ApplyAccessibility(preferences);

            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => other.CreateSurface(request);

            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => other.ShowModal(request, completed);

            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
            {
                host.Toasts.Add((owner, message, tone));
                return OperationResult<bool>.Success(true);
            }
        }

        /// <summary>An entity service over the fake engine: it moves and destroys the objects it is given, as the game's does.</summary>
        private sealed class WorldEntities : IEntityService
        {
            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                var target = ObjectOf(entity);
                transform = target == null ? TransformState.Identity : new TransformState(new Vec3(target.Position.X, target.Position.Y, target.Position.Z), Quat.Identity, new Vec3(1, 1, 1));
                return target != null && target.IsAlive;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
            {
                var target = ObjectOf(entity);
                if (target == null || !target.IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.NotFound, "The entity is no longer alive.");
                target.Position = new NetVector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
                return OperationResult<TransformState>.Success(transform);
            }

            public IReadOnlyList<IEntity> Query(EntityQuery query) => Array.Empty<IEntity>();

            public OperationResult<bool> Destroy(IEntity entity)
            {
                if (!(entity is FakeSpawnedEntity spawned) || !spawned.IsAlive) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Only a live spawn can be destroyed.");
                spawned.Dispose();
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "No motion here.");

            private static FakeWorldObject? ObjectOf(IEntity entity) =>
                entity is FakeSpawnedEntity spawned ? spawned.Object : entity is FakeWorldEntity fake ? fake.Object : null;
        }
    }
}
