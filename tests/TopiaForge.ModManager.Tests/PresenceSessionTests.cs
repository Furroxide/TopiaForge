using System;
using System.IO;
using System.Threading.Tasks;
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
