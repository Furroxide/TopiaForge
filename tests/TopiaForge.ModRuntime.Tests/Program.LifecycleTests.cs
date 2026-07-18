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

        private static void TestInitialBackgroundSceneReplay(string root)
        {
            var fixture = NewFixture(
                root,
                "initial-background-scenes",
                "TopiaForge.ValidTestMod.RuntimeInitialSceneReplayMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchInitialScenes(
                    new RuntimeUnderTest.InitialSceneReplay(
                        45,
                        "Lighting",
                        isValid: true,
                        mode: SceneLoadMode.Additive,
                        isActive: false),
                    new RuntimeUnderTest.InitialSceneReplay(
                        46,
                        "Menu",
                        isValid: true,
                        mode: SceneLoadMode.Single,
                        isActive: true)),
                "startup replay should include background additive scenes before the active scene");
            Assert(!runtime.DispatchSceneLoaded(
                    45,
                    "Lighting",
                    isValid: true,
                    mode: SceneLoadMode.Additive,
                    isActive: false)
                && !runtime.DispatchSceneLoaded(46, "Menu", isValid: true),
                "one native echo per replayed initial scene instance should be suppressed");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "initial-lifecycle:Lighting:45:Loaded:Additive:background:initial",
                "initial-legacy:Menu",
                "initial-detail:Menu:Single:active",
                "initial-lifecycle:Menu:46:Loaded:Single:active:initial",
                "initial-lifecycle:Menu:46:Activated:Single:active:initial",
                "unload");
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

        private static void TestCompleteSceneLifecycleDelivery(string root)
        {
            var fixture = NewFixture(root, "scene-lifecycle", "TopiaForge.ValidTestMod.RuntimeSceneLifecycleMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchInitialScene(81, "Menu", isValid: true),
                "the initial scene should publish one normalized loaded/activated pair");
            Assert(!runtime.DispatchSceneLoaded(81, "Menu", isValid: true),
                "the native echo after initial replay must not duplicate lifecycle events");
            Assert(runtime.DispatchSceneLoaded(
                    82,
                    "Shared",
                    isValid: true,
                    SceneLoadMode.Additive,
                    isActive: false),
                "a background additive scene should publish its loaded phase");
            Assert(runtime.DispatchSceneLoaded(
                    83,
                    "Shared",
                    isValid: true,
                    SceneLoadMode.Additive,
                    isActive: false),
                "equal scene names must remain distinguishable by instance id");
            Assert(runtime.DispatchSceneActivated(82, "Shared", isValid: true, SceneLoadMode.Additive),
                "later activation should publish a distinct lifecycle phase");
            Assert(runtime.DispatchSceneUnloaded(82, "Shared", isValid: true, SceneLoadMode.Additive),
                "scene unload should reach lifecycle subscribers");
            Assert(!runtime.DispatchSceneUnloaded(0, string.Empty, isValid: false, SceneLoadMode.Single),
                "invalid unload callbacks must not reach mods");
            Assert(runtime.DispatchSceneLoaded(
                    84,
                    "Replacement",
                    isValid: true,
                    SceneLoadMode.Single,
                    isActive: false),
                "single replacements should publish load before Unity's activation callback");
            Assert(runtime.DispatchSceneLifecycleActivated(
                    84,
                    "Replacement",
                    isValid: true,
                    SceneLoadMode.Single),
                "the later activeSceneChanged callback should publish exact activation without replaying legacy detail");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "scene-lifecycle:Menu:81:Loaded:Single:active:initial",
                "scene-lifecycle:Menu:81:Activated:Single:active:initial",
                "scene-lifecycle:Shared:82:Loaded:Additive:background:native",
                "scene-lifecycle:Shared:83:Loaded:Additive:background:native",
                "scene-lifecycle:Shared:82:Activated:Additive:active:native",
                "scene-lifecycle:Shared:82:Unloaded:Additive:background:native",
                "scene-lifecycle:Replacement:84:Loaded:Single:background:native",
                "scene-lifecycle:Replacement:84:Activated:Single:active:native",
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
