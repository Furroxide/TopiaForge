using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        // Registration and repeated-session tests define the cleanup standard expected from a production mod.
        private static void RegistrationConflictsFailClosed()
        {
            var gamemodeContext = new FakeModContext();
            var gamemodeWorlds = new FakeWorldGamemodeService(gamemodeContext.Lifetime);
            var gamemodeRobots = new FakeRobotKit(gamemodeContext.Lifetime);
            Assert(gamemodeWorlds.RegisterWorld(new WorldDefinition(
                    WellKnownWorldIds.OpenSandboxWorld,
                    "Open Sandbox",
                    "Conflict test world")).Succeeded
                && gamemodeWorlds.RegisterGamemode(new GamemodeDefinition(
                    ZombiesMod.GamemodeId,
                    "Conflicting Zombies",
                    "Owned by another provider")).Succeeded
                && gamemodeContext.Extensions.Register<IWorldGamemodeService>(gamemodeWorlds).Succeeded
                && gamemodeContext.Extensions.Register<IRobotAgentService>(gamemodeRobots.Agents).Succeeded,
                "the gamemode-conflict harness should expose both required dependencies");
            using (var runner = ModLifecycleRunner.Create<ZombiesMod>(gamemodeContext))
            {
                runner.Load();
                Assert(gamemodeWorlds.Gamemodes.Count == 1
                    && gamemodeWorlds.MenuEntries.Count == 0
                    && CountDiagnostics(gamemodeContext, "ZOMBIES_REGISTRATION_FAILED") == 1,
                    "a gamemode-id conflict must not publish a Zombies menu entry or continue startup");
                var launched = gamemodeWorlds.LoadAsync(new WorldLoadRequest(
                    WellKnownWorldIds.OpenSandboxWorld,
                    ZombiesMod.GamemodeId)).GetAwaiter().GetResult();
                Assert(launched.Succeeded && gamemodeContext.Ui.Surfaces.Count == 0,
                    "a session owned by the conflicting gamemode must not start the Zombies controller");
                runner.Unload();
            }
            gamemodeContext.AssertNoLeaks();

            var menuContext = new FakeModContext();
            var menuWorlds = new FakeWorldGamemodeService(menuContext.Lifetime);
            var menuRobots = new FakeRobotKit(menuContext.Lifetime);
            Assert(menuWorlds.RegisterMenuEntry(new GamemodeMenuEntry(
                    ZombiesMod.MenuEntryId,
                    "Conflicting entry",
                    "Owned by another provider",
                    "other.mode",
                    "other.world")).Succeeded
                && menuContext.Extensions.Register<IWorldGamemodeService>(menuWorlds).Succeeded
                && menuContext.Extensions.Register<IRobotAgentService>(menuRobots.Agents).Succeeded,
                "the menu-conflict harness should expose both required dependencies");
            using (var runner = ModLifecycleRunner.Create<ZombiesMod>(menuContext))
            {
                runner.Load();
                Assert(menuWorlds.Gamemodes.Count == 0
                    && menuWorlds.MenuEntries.Count == 1
                    && CountDiagnostics(menuContext, "ZOMBIES_REGISTRATION_FAILED") == 1,
                    "a menu-id conflict must immediately roll back the partial Zombies gamemode registration");
                runner.Unload();
            }
            menuContext.AssertNoLeaks();
        }

        private static void SuccessfulModLifecycleReusesOneContextWithoutSessionLeaks()
        {
            const string arenaScene = "ZombiesArena";
            var context = new FakeModContext(new ModIdentity(
                "io.github.furroxide.topiaforge.zombies",
                "Zombies",
                SemanticVersion.Parse("1.0.0")));
            var config = FastConfig();
            context.Config.Seed(2, config);
            context.Scenes.Load(arenaScene);
            context.Player.Snapshot = new PlayerSnapshot(
                Vec3.Zero,
                new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            context.Player.Health = new PlayerHealthSnapshot(config.PlayerIntegrity, config.PlayerIntegrity);

            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            robots.Agents.AutoCompleteAgentMovement = false;
            var pauseMenu = new TestWorldPauseMenuService(context.Lifetime);
            Assert(worlds.RegisterWorld(new WorldDefinition(
                    WellKnownWorldIds.OpenSandboxWorld,
                    "Open Sandbox",
                    "Successful Zombies lifecycle world",
                    arenaScene)).Succeeded
                && context.Extensions.Register<IWorldGamemodeService>(worlds).Succeeded
                && context.Extensions.Register<IRobotAgentService>(robots.Agents).Succeeded
                && context.Extensions.Register<IWorldPauseMenuService>(pauseMenu).Succeeded,
                "the successful lifecycle harness should publish every Zombies dependency");

            using var runner = ModLifecycleRunner.Create<ZombiesMod>(context);
            runner.Load();
            Assert(worlds.Gamemodes.Count == 1
                && worlds.MenuEntries.Count == 1
                && context.Commands.ActiveCommandCount == 3,
                "successful mod load publishes one gamemode, one menu entry, and all commands");
            var loadedBaseline = context.Lifetime.TrackedResourceCount;

            for (var cycle = 0; cycle < 10; cycle++)
            {
                var player = new FakeEntity(
                    "zombies-player-" + cycle,
                    "Player",
                    new Vec3(cycle, 0f, 0f));
                robots.Agents.PlayerEntity = player;
                context.Player.Snapshot = new PlayerSnapshot(
                    player.Position,
                    new Ray(player.Position, new Vec3(0f, 0f, 1f)));
                context.Player.Health = new PlayerHealthSnapshot(config.PlayerIntegrity, config.PlayerIntegrity);

                var launched = worlds.LaunchMenuEntryAsync(ZombiesMod.MenuEntryId).GetAwaiter().GetResult();
                Assert(launched.Succeeded
                    && pauseMenu.ActiveActionCount == 1
                    && context.Ui.Surfaces.Count == 1
                    && context.Input.ActiveActionCount == 1
                    && context.Events.ActiveSubscriptionCount == 1
                    && context.Lifetime.TrackedResourceCount > loadedBaseline,
                    "each successful session binds its controller, HUD, input, update, and pause action once");
                Assert(pauseMenu.Invoke("zombies-restart"),
                    "the session-scoped destructive pause action remains callable after every rebind");

                Assert(worlds.EndSession(WorldSessionEndReason.EndedByGamemode).Succeeded
                    && pauseMenu.ActiveActionCount == 0
                    && context.Ui.Surfaces.Count == 0
                    && context.Input.ActiveActionCount == 0
                    && context.Events.ActiveSubscriptionCount == 0
                    && robots.Agents.ActiveAgents.Count == 0
                    && context.Lifetime.TrackedResourceCount == loadedBaseline,
                    "ending each session returns the still-loaded mod to its exact lifetime/resource baseline");
            }

            runner.Unload();
            Assert(worlds.Gamemodes.Count == 0
                && worlds.MenuEntries.Count == 0
                && pauseMenu.ActiveActionCount == 0
                && context.Commands.ActiveCommandCount == 0,
                "successful mod unload retracts registrations, actions, and commands after repeated sessions");
            context.AssertNoLeaks();
        }

        private static void SceneReadinessRequiresTheSessionScene()
        {
            var config = FastConfig();
            using var harness = new Harness(config, activeScene: "WrongArena", sessionScene: "ZombiesArena");

            for (var index = 0; index < 8; index++)
            {
                harness.Advance(0.25f);
            }

            Assert(harness.Controller.TestingPhase == ZombiesPhase.WaitingForWorld
                && harness.Controller.TestingWave == 0
                && harness.Robots.Agents.ActiveAgents.Count == 0,
                "Zombies must not begin from the early Worlds session event while another scene is active");

            harness.Context.Scenes.Load("ZombiesArena");
            harness.Advance(0.01f);
            harness.Advance(0.01f);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.Wave
                && harness.Controller.TestingWave == 1,
                "Zombies begins only after the authoritative session scene and safe player entity are ready");
        }



        private static void RepeatedLifecycleReturnsToLeakBaseline()
        {
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var harness = new Harness(FastConfig(), withChronos: true);
                harness.Advance(0.01f);
                harness.Controller.TestingSetWavePhase();
                Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 4f)),
                    "each lifecycle cycle should own one agent");
                harness.Controller.TestingDamagePlayer(harness.Config.PlayerIntegrity);
                var gameOver = FindSurface(harness.Context, "zombies-game-over");
                Assert(gameOver.ActivateButton("zombies-game-over-return").Succeeded,
                    "each lifecycle cycle should open a retained confirmation modal");
                harness.Context.Ui.Modals[0].Close();
                Assert(harness.Controller.Restart().Succeeded,
                    "each lifecycle cycle should release run state before unload");
                harness.Dispose();
            }
        }
    }
}
