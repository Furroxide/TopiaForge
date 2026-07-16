using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestNormalLifecycleAndSubscriberIsolation(string root)
        {
            var fixture = NewFixture(root, "normal", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            Assert(runtime.IsLoaded(fixture.Manifest.Id), "a valid synthetic assembly should load");
            runtime.DispatchUpdate(1f / 60f);
            runtime.UnloadAll();

            AssertTrace(fixture.TracePath, "load", "update-after-failure", "unload", "cleanup-second", "cleanup-first");
            Assert(fixture.Logger.Errors.Count == 1
                && fixture.Logger.Errors[0].Contains("subscriber", StringComparison.OrdinalIgnoreCase),
                "one throwing event subscriber must be attributed without blocking later subscribers");
            Assert(fixture.Observer.Events.SequenceEqual(new[] { "loading:" + fixture.Manifest.Id, "loaded:" + fixture.Manifest.Id }),
                "the startup observer should bracket the successful load callback");
            Assert(fixture.GameplayHost.Disposed, "runtime shutdown should release the manager-owned gameplay host");
        }

        private static void TestPartialLoadFailureCleanup(string root)
        {
            var fixture = NewFixture(root, "load-failure", "TopiaForge.ValidTestMod.RuntimeFailingLoadMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("synthetic load failure", StringComparison.Ordinal) == true,
                "a throwing OnLoad must remain failed and observable");
            AssertTrace(fixture.TracePath, "load", "unload", "cleanup");
            Assert(fixture.Observer.Events.SequenceEqual(new[] { "loading:" + fixture.Manifest.Id, "failed:" + fixture.Manifest.Id }),
                "the startup observer should close a failed callback boundary");
            runtime.UnloadAll();
        }

        private static void TestInitialSceneReplayAndDeduplication(string root)
        {
            var fixture = NewFixture(root, "initial-scene", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchInitialScene(41, "Startup", isValid: true),
                "a valid active scene should be replayed immediately after mod loading");
            Assert(!runtime.DispatchSceneLoaded(41, "Startup", isValid: true),
                "the native callback for the replayed scene handle should be suppressed once");
            Assert(runtime.DispatchSceneLoaded(41, "Startup", isValid: true),
                "a later real callback must not be suppressed merely because Unity reused a scene handle");
            Assert(!runtime.DispatchSceneLoaded(42, "   ", isValid: true)
                   && !runtime.DispatchSceneLoaded(42, "Gameplay", isValid: false),
                "invalid or unnamed Unity scenes must not reach mods");
            Assert(runtime.DispatchSceneLoaded(42, "Gameplay", isValid: true),
                "the next valid loaded scene should be delivered");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "scene:Startup",
                "scene:Gameplay",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestNativeInitialSceneRaceIsDeduplicated(string root)
        {
            var fixture = NewFixture(root, "initial-scene-race", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchSceneLoaded(51, "Startup", isValid: true),
                "a native scene callback that wins the startup race should be delivered");
            Assert(!runtime.DispatchInitialScene(51, "Startup", isValid: true),
                "the explicit active-scene replay should recognize an already-delivered native callback");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestDetailedSceneLoadDelivery(string root)
        {
            var fixture = NewFixture(root, "detailed-scene", "TopiaForge.ValidTestMod.RuntimeDetailedSceneMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchInitialScene(71, "Menu", isValid: true),
                "initial active-scene replay should be authoritative");
            Assert(runtime.DispatchSceneLoaded(
                    72,
                    "Lighting",
                    isValid: true,
                    SceneLoadMode.Additive,
                    isActive: false),
                "an ordinary additive scene should still be delivered with non-authoritative metadata");
            Assert(runtime.DispatchSceneActivated(72, "Lighting", isValid: true, SceneLoadMode.Additive),
                "activating a previously background additive scene should publish an authoritative detail event");
            Assert(runtime.DispatchSceneLoaded(
                    73,
                    "ActivatedArena",
                    isValid: true,
                    SceneLoadMode.Additive,
                    isActive: true),
                "an activated additive scene should be delivered as authoritative");
            Assert(runtime.DispatchSceneLoaded(
                    74,
                    "Replacement",
                    isValid: true,
                    SceneLoadMode.Single,
                    isActive: false),
                "a single load should be authoritative even before active-scene state catches up");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "scene-legacy:Menu",
                "scene-detail:Menu:Single:active:authoritative",
                "scene-legacy:Lighting",
                "scene-detail:Lighting:Additive:background:additive",
                "scene-detail:Lighting:Additive:active:authoritative",
                "scene-legacy:ActivatedArena",
                "scene-detail:ActivatedArena:Additive:active:authoritative",
                "scene-legacy:Replacement",
                "scene-detail:Replacement:Single:background:authoritative",
                "unload");
        }

        private static void TestInvalidInitialSceneWaitsForNativeDelivery(string root)
        {
            var fixture = NewFixture(root, "invalid-initial-scene", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.DispatchInitialScene(0, string.Empty, isValid: false),
                "an invalid active scene must not be synthesized for mods");
            Assert(runtime.DispatchSceneLoaded(61, "Startup", isValid: true),
                "a valid native scene callback should still deliver after an invalid initial snapshot");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestUnloadFailureStillCleans(string root)
        {
            var fixture = NewFixture(root, "unload-failure", "TopiaForge.ValidTestMod.RuntimeFailingUnloadMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            runtime.UnloadAll();

            AssertTrace(fixture.TracePath, "load", "unload", "cleanup");
            Assert(fixture.Logger.Errors.Any(error => error.Contains("OnUnload", StringComparison.Ordinal)),
                "unload exceptions should be attributed after lifetime cleanup continues");
        }

    }
}
