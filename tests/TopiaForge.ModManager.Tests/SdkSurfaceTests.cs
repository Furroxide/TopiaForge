using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free V1 contracts and specialist module data types. No GameCode/UnityEngine involved.
    internal static partial class SdkSurfaceTests
    {
        public static void Run()
        {
            TestVec3RoundTrip();
            TestVec3Equality();
            TestRobotColor();
            TestRobotAgentSpawnRequestDefaults();
            TestRobotTypeAndBrainSwitchContracts();
            TestRobotInteractionContracts();
            TestReachableSpawnRequestDefaults();
            TestRobotAgentEnums();
            TestRobotAgentSurface();
            TestBrainQueryContracts();
            TestConversationContracts();
            TestDialogueInputContracts();
            TestGameScenesClassifier();
            TestWorldSessionEndContracts();
            TestUnifiedExpectedFailureContracts();
            TestShopContracts();
            TestRobotObjectiveProgramContracts();
            Console.WriteLine("All SDK surface tests passed.");
        }

        private static void TestUnifiedExpectedFailureContracts()
        {
            var bespokeOperationResults = typeof(TopiaForgeMod).Assembly.GetExportedTypes()
                .Where(type => type != typeof(OperationResult<>))
                .Where(type => type.GetProperty("Succeeded") != null && type.GetProperty("ErrorCode") != null)
                .Select(type => type.FullName)
                .ToArray();
            Assert(bespokeOperationResults.Length == 0,
                "expected failures must not introduce result wrappers alongside OperationResult<T>: " +
                string.Join(", ", bespokeOperationResults));

            var configType = typeof(ConfigDefinition<object>);
            Assert(configType.GetProperty("Validate")?.PropertyType ==
                   typeof(Func<object, OperationResult<bool>>) &&
                   configType.GetProperty("Migrate")?.PropertyType ==
                   typeof(Func<int, object, OperationResult<object>>),
                "config validation and migration use the common stable result contract");

            var register = typeof(ICommandService).GetMethod("Register");
            Assert(register != null && register.GetParameters()[1].ParameterType ==
                   typeof(Func<CommandInvocation, OperationResult<string>>),
                "command handlers use OperationResult<string> for display text and stable failures");

            Assert(typeof(IRuntimeInfo).GetProperty("GameVersion") == null &&
                   typeof(IRuntimeInfo).GetMethod("TryGetGameVersion") != null,
                "optional runtime version discovery follows the cheap Try-query convention");
        }

        // The shared scene classifier every mod uses to agree on what counts as "the menu" vs gameplay.
        private static void TestGameScenesClassifier()
        {
            Assert(GameScenes.MainMenuSceneName == "TestCityStartMenu", "MainMenuSceneName is pinned to the verified menu scene");
            Assert(GameScenes.IsMainMenuScene("TestCityStartMenu") && GameScenes.IsMainMenuScene("testcitystartmenu"),
                "IsMainMenuScene matches the menu scene case-insensitively");
            Assert(!GameScenes.IsMainMenuScene("TestCity") && !GameScenes.IsMainMenuScene(null!),
                "IsMainMenuScene rejects other scenes and null");

            foreach (var scene in new[] { "TestCityStartMenu", "MainMenu_X", "BootScene", "LevelLoader", "SplashIntro" })
            {
                Assert(GameScenes.IsNonGameplayScene(scene), scene + " should classify as non-gameplay");
            }

            foreach (var scene in new[] { "UgcPlay", "TestCity", "02 City Streets" })
            {
                Assert(!GameScenes.IsNonGameplayScene(scene), scene + " should classify as gameplay");
            }

            Assert(!GameScenes.IsNonGameplayScene(null!) && !GameScenes.IsNonGameplayScene(string.Empty),
                "IsNonGameplayScene is null/empty safe");
        }

        // The session-end lifecycle contract (the fix for gamemodes staying active over the menu).
        private static void TestWorldSessionEndContracts()
        {
            var sessionEnded = typeof(IWorldGamemodeService).GetEvent("SessionEnded");
            Assert(sessionEnded != null && sessionEnded.EventHandlerType == typeof(Action<WorldSessionEnd>),
                "IWorldGamemodeService exposes SessionEnded as Action<WorldSessionEnd>");
            var endSession = typeof(IWorldGamemodeService).GetMethod("EndSession");
            Assert(endSession != null && endSession.GetParameters().Length == 1
                && endSession.GetParameters()[0].ParameterType == typeof(WorldSessionEndReason),
                "IWorldGamemodeService exposes EndSession(WorldSessionEndReason)");

            // Pin the reason set: mods switch on these, so a silent rename/reorder is a breaking change.
            Assert((int)WorldSessionEndReason.MenuReached == 0 && (int)WorldSessionEndReason.EndedByGamemode == 1
                && (int)WorldSessionEndReason.Superseded == 2 && (int)WorldSessionEndReason.ProviderUnloading == 3
                && (int)WorldSessionEndReason.SceneReplaced == 4 && (int)WorldSessionEndReason.LoadFailed == 5,
                "WorldSessionEndReason order must append SceneReplaced and LoadFailed after the original reasons");

            var inFlight = typeof(IWorldTransitionState).GetProperty("IsTransitionInFlight");
            Assert(inFlight != null && inFlight.PropertyType == typeof(bool) && inFlight.CanRead && !inFlight.CanWrite,
                "IWorldTransitionState exposes read-only bool IsTransitionInFlight");
            Assert(typeof(IWorldGamemodeService).GetProperty("IsTransitionInFlight") == null,
                "scene-load state stays on its focused optional capability interface");

            var session = new WorldSession("world", "gamemode", "gameScene", "Scene", DateTime.UtcNow);
            var end = new WorldSessionEnd(session, WorldSessionEndReason.MenuReached);
            Assert(ReferenceEquals(end.Session, session) && end.Reason == WorldSessionEndReason.MenuReached,
                "WorldSessionEnd carries the ended session and the reason");

            var threw = false;
            try
            {
                _ = new WorldSessionEnd(null!, WorldSessionEndReason.MenuReached);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }

            Assert(threw, "WorldSessionEnd null-guards the session");
        }
        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
