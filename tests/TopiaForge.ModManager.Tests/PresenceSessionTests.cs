using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// What presence reads of the session orchestrator (WS3.10): the world session running, by its id, which a joined
    /// player's arrival names, and where that world put its player, which the spawn ring is around. Over the real
    /// orchestrator with a world of the test's own.
    /// </summary>
    internal static class PresenceSessionTests
    {
        internal static void Run()
        {
            var root = Directory.CreateTempSubdirectory("TopiaForgePresenceSession-");
            try
            {
                TheRunningWorldNamesItsSessionAndSpawn(root.FullName);
                foreach (var change in new[] { SceneLifecyclePhase.Activated, SceneLifecyclePhase.Loaded, SceneLifecyclePhase.Unloaded })
                    ReplacedScenesEndWithAReadinessFailure(root.FullName, change);
            }
            finally
            {
                try
                {
                    root.Delete(true);
                }
                catch (IOException)
                {
                    // A file still held open is left for the next clean of the temporary folder.
                }
            }

            Console.WriteLine("PresenceSessionTests passed.");
        }

        private static void TheRunningWorldNamesItsSessionAndSpawn(string root)
        {
            using var test = new SessionFixture(root);
            var spawn = new TransformState(new Vec3(4f, 1f, -2f), new Quat(0f, 0.70710677f, 0f, 0.70710677f), new Vec3(1f, 1f, 1f));
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SpawnWorld(spawn)));
            That(test.Hosted.RunningWorldId == null && test.Hosted.RunningSpawn == null, "at the menu no world session runs");

            test.Launch();
            var running = SessionFixture.ModeSession!.SessionId;
            That(test.Hosted.RunningWorldId == running, "a running world session is named by its id");
            var placed = test.Hosted.RunningSpawn;
            That(placed == spawn && placed.HasValue && placed.Value.Position.X == 4f && placed.Value.Position.Z == -2f && placed.Value.Rotation.Y > 0.7f,
                "and the spawn is where the world put its player as it became ready");

            var restarted = SessionFixture.ModeSession!.RestartAsync();
            test.Wait(restarted);
            That(restarted.Result.Succeeded && test.Hosted.RunningWorldId == SessionFixture.ModeSession!.SessionId && test.Hosted.RunningWorldId != running,
                "a restarted world is another world session, so every player is placed afresh");

            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            test.Wait(menu);
            That(menu.Result.Succeeded && test.Hosted.RunningWorldId == null && test.Hosted.RunningSpawn == null, "back at the menu, nothing runs");
        }

        private static void ReplacedScenesEndWithAReadinessFailure(string root, SceneLifecyclePhase change)
        {
            // The host starts its world alone and opens it to players afterwards, so the realm at launch is single player.
            var rig = new PresenceRig();
            using var test = new SessionFixture(root, replication: rig.Host.Replication);
            rig.Host.Runtime.SessionPhaseSource = () => test.Hosted.RealmPhase;
            var spawn = new TransformState(new Vec3(900, 0, 0), Quat.Identity, new Vec3(1, 1, 1));
            var world = new SpawnWorld(spawn);
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(world));
            var controllersDisposed = 0;
            SessionFixture.Start = (session, _) =>
            {
                SessionFixture.ModeSession = session;
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController(() => controllersDisposed++)));
            };
            rig.Frames(1);
            That(rig.Host.Runtime.Realm == NetRealm.Offline, "the world starts in single player");
            test.Launch();
            var original = SessionFixture.ModeSession!;
            var scene = new FollowedScene();
            scene.Activated("World", -12);
            rig.Host.Presence.RunningWorld = () => test.Hosted.RunningWorldId;
            rig.Host.Presence.RunningSpawn = () => test.Hosted.RunningSpawn;
            rig.Host.Presence.SceneVersion = () => scene.Version;
            rig.OpenToMultiplayer();
            var raw = rig.AddRawPlayer("raw");
            rig.Frames(3);
            raw.SendPose(new NetVector3(900, 0, 0));
            rig.Frames(3);
            That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue && rig.Host.Presence.TryGetAvatar(raw.Id, out _),
                "real running readiness establishes the original pose and avatar");
            test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-14, "Background", SceneLifecyclePhase.Loaded,
                SceneLoadMode.Additive, isActive: false));
            test.Host.Drain();
            That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue && controllersDisposed == 0,
                "unrelated additive loading retains the controller and current readiness");

            if (change != SceneLifecyclePhase.Unloaded) scene.Activated("World", -13);
            var replacement = new SceneLifecycleEvent(change == SceneLifecyclePhase.Unloaded ? -12 : -13, "World", change,
                SceneLoadMode.Single, isActive: change != SceneLifecyclePhase.Unloaded);
            test.Dispatch.HoldNextPost = true;
            test.Hosted.OnSceneLifecycle(replacement);
            That(test.Hosted.RunningWorldId == null && test.Hosted.RunningSpawn == null,
                "owned-scene loss immediately invalidates both world and settled spawn");
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null,
                "scene evidence invalidates range before queued policy handling");
            raw.SendPose(new NetVector3(900, 0, 0), PresenceFlags.Teleported);
            rig.Frames(3);
            That(test.Hosted.Current.Phase == Core.SessionPhase.Running && controllersDisposed == 0,
                "the held stop exposes the interval between immediate invalidation and queued cleanup");
            AssertNoRemotePose(rig, raw, "old-scene packets cannot bypass closed readiness while the stop is queued");

            test.Hosted.OnSceneLifecycle(replacement); // A duplicate may reach the queue before the held callback.
            test.Dispatch.Release();
            test.Until(() => test.Hosted.Current.Phase == Core.SessionPhase.Idle);
            var terminal = test.Outcomes.Where(item => item.Kind == "session" && item.SessionId == original.SessionId).ToArray();
            That(terminal.Length == 1 && terminal[0].Status == "failed"
                && terminal[0].Error?.Message == GamemodeSessionOrchestrator.LostWorldReadinessMessage,
                "one actionable failed outcome replaces a permanently hidden Running session");
            That(world.Reads == 1 && world.Disposals == 1 && controllersDisposed == 1 && original.Lifetime.IsStopping,
                "cleanup disposes the old world and controller once without rereading immutable readiness");
            raw.SendPose(new NetVector3(900, 0, 0), PresenceFlags.Teleported);
            rig.Frames(3);
            AssertNoRemotePose(rig, raw, "Idle cannot reuse the lost world's spawn as a trusted anchor");

            // Recovery is a new provider load, not a new label on the old world's immutable evidence.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var nextSpawn = new TransformState(new Vec3(-900 - attempt * 100, 0, 0), Quat.Identity, new Vec3(1, 1, 1));
                var nextScene = -20 - attempt;
                var nextWorld = new SpawnWorld(nextSpawn, nextScene);
                SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(nextWorld));
                scene.Activated("World", nextScene);
                test.Launch("fresh-world-" + attempt);
                var nextSession = SessionFixture.ModeSession!;
                That(nextSession.SessionId != original.SessionId && test.Hosted.RunningSpawn == nextSpawn,
                    "a new world start commits a new identity and a genuinely different spawn");
                var stale = original.StopAsync();
                test.Wait(stale);
                That(!stale.Result.Succeeded && test.Hosted.RunningWorldId == nextSession.SessionId,
                    "an old session handle cannot stop its replacement");
                rig.Frames(1);
                raw.SendPose(new NetVector3(nextSpawn.Position.X, 0, 0));
                rig.Frames(3);
                That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue && rig.Host.Presence.TryGetAvatar(raw.Id, out _)
                    && rig.Host.Presence.TryGetPose(raw.Id, out var pose) && pose.Position.X == nextSpawn.Position.X,
                    "fresh provider readiness restores avatar presentation at the new world's spawn");
                test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(nextScene, "World", SceneLifecyclePhase.Unloaded,
                    SceneLoadMode.Single, isActive: false));
                test.Until(() => test.Hosted.Current.Phase == Core.SessionPhase.Idle);
                rig.Frames(1);
                AssertNoRemotePose(rig, raw, "each later scene loss retires its own avatar and readiness");
                That(nextWorld.Reads == 1 && nextWorld.Disposals == 1 && controllersDisposed == attempt + 2
                    && test.Outcomes.Count(item => item.Kind == "session" && item.SessionId == nextSession.SessionId
                        && item.Status == "failed" && item.Error?.Message == GamemodeSessionOrchestrator.LostWorldReadinessMessage) == 1,
                    "repeated replacements each terminate once with the same bounded failure");
            }
        }

        private static void AssertNoRemotePose(PresenceRig rig, RawPresencePlayer raw, string message) =>
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null && !rig.Host.Presence.TryGetAvatar(raw.Id, out _)
                && !rig.Host.Presence.TryGetPose(raw.Id, out _), message);

        /// <summary>A world whose readiness puts the player at a spawn of the test's choosing.</summary>
        private sealed class SpawnWorld : IWorldInstance
        {
            private readonly WorldReadiness readiness;
            internal int Reads;
            internal int Disposals;
            internal SpawnWorld(TransformState spawn, int scene = -12)
            {
                readiness = new WorldReadiness(new WorldSceneIdentity(scene, "World"), spawn,
                    new TransformState(new Vec3(spawn.Position.X, spawn.Position.Y - 0.13f, spawn.Position.Z), spawn.Rotation, spawn.Scale));
            }

            public WorldReadiness Readiness { get { Reads++; return readiness; } }

            public void Dispose() => Disposals++;
        }
    }
}
