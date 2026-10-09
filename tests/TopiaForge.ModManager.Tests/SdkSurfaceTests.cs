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
            TestRobotEditingContracts();
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

        private static void TestWorldSessionEndContracts()
        {
            var assembly = typeof(IWorldSessionService).Assembly;
            Assert(assembly.GetType("TopiaForge.Mods.GamemodeHost") == null
                && assembly.GetType("TopiaForge.Mods.IWorldGamemodeService") == null,
                "the old startup and imperative registration APIs are retired");
            Assert(typeof(IWorldSessionService).GetEvent("StateChanged")!.EventHandlerType == typeof(Action<WorldSessionSnapshot>),
                "observers receive one committed immutable state");
            Assert(typeof(IWorldSession).GetProperty("Context") == null && typeof(IWorldSession).GetProperty("Lifetime") == null,
                "an observed session cannot expose another package's resource scope");
            Assert(typeof(IGamemodeSession).GetInterfaces().Contains(typeof(IWorldSession)), "gamemode session shares the bound identity contract");
            foreach (var property in typeof(WorldSessionSnapshot).GetProperties())
                Assert(!property.CanWrite, "committed session state is immutable");
            Assert(typeof(IGamemodeFactory).GetMethod("StartAsync") != null && typeof(IGamemodeFactory).GetProperty("GamemodeId") == null,
                "manifest identity is authoritative and factory startup is asynchronous");
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
