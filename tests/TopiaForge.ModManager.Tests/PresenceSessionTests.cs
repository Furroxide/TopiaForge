using System;
using System.IO;
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
                    ReplacedScenesCannotReuseReadiness(root.FullName, change);
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
            That(placed.HasValue && placed.Value.Position.X == 4f && placed.Value.Position.Z == -2f && placed.Value.Rotation.Y > 0.7f,
                "and the spawn is where the world put its player as it became ready");

            var restarted = SessionFixture.ModeSession!.RestartAsync();
            test.Wait(restarted);
            That(restarted.Result.Succeeded && test.Hosted.RunningWorldId == SessionFixture.ModeSession!.SessionId && test.Hosted.RunningWorldId != running,
                "a restarted world is another world session, so every player is placed afresh");

            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            test.Wait(menu);
            That(menu.Result.Succeeded && test.Hosted.RunningWorldId == null && test.Hosted.RunningSpawn == null, "back at the menu, nothing runs");
        }

        private static void ReplacedScenesCannotReuseReadiness(string root, SceneLifecyclePhase change)
        {
            using var test = new SessionFixture(root);
            var spawn = new TransformState(new Vec3(900, 0, 0), Quat.Identity, new Vec3(1, 1, 1));
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SpawnWorld(spawn)));
            test.Launch();
            var rig = new PresenceRig();
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
            That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue, "real running readiness establishes the original pose");
            test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-14, "Background", SceneLifecyclePhase.Loaded,
                SceneLoadMode.Additive, isActive: false));
            test.Host.Drain();
            That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue, "unrelated additive loading retains current readiness");
            if (change != SceneLifecyclePhase.Unloaded) scene.Activated("World", -13);
            test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(change == SceneLifecyclePhase.Unloaded ? -12 : -13, "World", change,
                SceneLoadMode.Single, isActive: change != SceneLifecyclePhase.Unloaded));
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null, "scene evidence invalidates range before queued policy handling");
            test.Host.Drain();
            That(test.Hosted.Current.Phase == Core.SessionPhase.Running, "keep-controller policy retains the session across native replacement");
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null, "replacement invalidates range before receive");
            raw.SendPose(new NetVector3(900, 0, 0), PresenceFlags.Teleported);
            rig.Frames(3);
            Console.WriteLine("Retained readiness " + change + ": avatar=" + rig.Host.Presence.TryGetAvatar(raw.Id, out _) +
                ", validated=" + rig.Host.Presence.ValidatedPosition(raw.Id).HasValue);
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null && !rig.Host.Presence.TryGetAvatar(raw.Id, out _) &&
                !rig.Host.Presence.TryGetPose(raw.Id, out _), "a retained session cannot reuse old-scene readiness to validate a late first pose");
            var restart = SessionFixture.ModeSession!.RestartAsync();
            test.Wait(restart);
            rig.Frames(1);
            raw.SendPose(new NetVector3(900, 0, 0));
            rig.Frames(3);
            That(rig.Host.Presence.ValidatedPosition(raw.Id).HasValue, "new world readiness restores trust without changing keep-controller policy");
        }

        /// <summary>A world whose readiness puts the player at a spawn of the test's choosing.</summary>
        private sealed class SpawnWorld : IWorldInstance
        {
            internal SpawnWorld(TransformState spawn)
            {
                Readiness = new WorldReadiness(new WorldSceneIdentity(-12, "World"), spawn);
            }

            public WorldReadiness Readiness { get; }

            public void Dispose()
            {
            }
        }
    }
}
