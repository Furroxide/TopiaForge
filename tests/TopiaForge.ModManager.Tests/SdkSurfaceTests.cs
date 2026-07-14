using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free additions to TopiaForge.Mods.Abstractions: the Vec3 struct and the IModContext
    // service-resolution extension methods. No GameCode/UnityEngine involved.
    internal static class SdkSurfaceTests
    {
        public static void Run()
        {
            TestVec3RoundTrip();
            TestVec3Equality();
            TestRequireServiceReturnsRegistered();
            TestRequireServiceThrowsWhenMissing();
            TestTryGetService();
            TestAssetContracts();
            TestPromptContracts();
            TestAssetAndPromptContextExtensions();
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
            TestSceneCoordinationContracts();
            TestPauseMenuContracts();
            TestCustomWorldContracts();
            TestShopContracts();
            TestRobotObjectiveProgramContracts();
            Console.WriteLine("All SDK surface tests passed.");
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

        // The scene-transition arbitration contract (the fix for mods racing single-mode scene loads).
        private static void TestSceneCoordinationContracts()
        {
            // Pin the priority order: Automatic yields, UserInitiated supersedes.
            Assert((int)SceneTransitionPriority.Automatic == 0 && (int)SceneTransitionPriority.UserInitiated == 1,
                "SceneTransitionPriority order must be Automatic, UserInitiated");

            var request = new SceneTransitionRequest("owner.mod", "UgcPlay", SceneTransitionPriority.Automatic, "why");
            Assert(request.OwnerModId == "owner.mod" && request.SceneName == "UgcPlay"
                && request.Priority == SceneTransitionPriority.Automatic && request.Reason == "why",
                "SceneTransitionRequest carries owner, scene, priority and reason");

            var defaultReason = new SceneTransitionRequest("owner.mod", "Scene", SceneTransitionPriority.UserInitiated);
            Assert(defaultReason.Reason == string.Empty, "SceneTransitionRequest reason defaults to empty");

            var threw = false;
            try
            {
                _ = new SceneTransitionRequest(" ", "Scene", SceneTransitionPriority.Automatic);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert(threw, "SceneTransitionRequest requires an owner mod id");

            var claim = new FakeClaim();
            var approved = SceneTransitionDecision.Approve(claim, "ok");
            Assert(approved.Approved && ReferenceEquals(approved.Claim, claim) && approved.Message == "ok",
                "SceneTransitionDecision.Approve carries the claim");
            var refused = SceneTransitionDecision.Refuse("busy");
            Assert(!refused.Approved && refused.Claim == null && refused.Message == "busy",
                "SceneTransitionDecision.Refuse carries no claim");

            var info = new SceneClaimInfo("owner.mod", "Scene", SceneTransitionPriority.UserInitiated, "why", DateTime.UtcNow);
            Assert(info.OwnerModId == "owner.mod" && info.SceneName == "Scene"
                && info.Priority == SceneTransitionPriority.UserInitiated && info.Reason == "why",
                "SceneClaimInfo carries the claim metadata");

            Assert(typeof(ISceneCoordinator).GetMethod("RequestTransition") != null
                && typeof(ISceneCoordinator).GetMethod("ReleaseOwner") != null
                && typeof(ISceneCoordinator).GetProperty("IsSceneBusy") != null
                && typeof(ISceneCoordinator).GetProperty("ActiveClaims") != null,
                "ISceneCoordinator exposes RequestTransition, ReleaseOwner, IsSceneBusy and ActiveClaims");

            // Direct requests default to user-initiated; automatic callers opt into the lower priority.
            Assert(new UgcLiveSyncRequest().Priority == SceneTransitionPriority.UserInitiated,
                "UgcLiveSyncRequest priority defaults to UserInitiated");

            var worldRequest = new WorldLoadRequest("world", "mode");
            Assert(worldRequest.Priority == SceneTransitionPriority.UserInitiated,
                "WorldLoadRequest priority defaults to UserInitiated");
            var automaticWorldRequest = new WorldLoadRequest(
                "world", "mode", SceneTransitionPriority.Automatic);
            Assert(automaticWorldRequest.Priority == SceneTransitionPriority.Automatic,
                "WorldLoadRequest carries explicit Automatic priority");
            AssertThrows<ArgumentOutOfRangeException>(() => new WorldLoadRequest(
                    "world", "mode", (SceneTransitionPriority)99),
                "WorldLoadRequest rejects an unknown transition priority");
            AssertThrows<ArgumentOutOfRangeException>(() => new UgcLiveSyncRequest(
                    (SceneTransitionPriority)99),
                "UgcLiveSyncRequest rejects an unknown transition priority");

            Assert(typeof(WorldLoadRequest).GetConstructors().Length == 1,
                "WorldLoadRequest exposes one canonical constructor");
            Assert(typeof(UgcLiveSyncRequest).GetConstructors().Length == 1,
                "UgcLiveSyncRequest exposes one canonical constructor");
        }

        private sealed class FakeClaim : IDisposable
        {
            public void Dispose()
            {
            }
        }

        // The wander/flee/reprogram objective additions (RobotKit 0.8.0): appended enum members (mods switch on
        // these, so a silent reorder is a breaking change), the new factories and their defaults, the courier
        // payload rules, and the ProgramDelivered event contract.
        private static void TestRobotObjectiveProgramContracts()
        {
            // Pin the enum orders: additions must append.
            Assert((int)RobotObjectiveKind.Idle == 0 && (int)RobotObjectiveKind.GoTo == 1
                && (int)RobotObjectiveKind.Follow == 2 && (int)RobotObjectiveKind.Patrol == 3
                && (int)RobotObjectiveKind.Wander == 4 && (int)RobotObjectiveKind.Flee == 5
                && (int)RobotObjectiveKind.Reprogram == 6,
                "RobotObjectiveKind order must be Idle, GoTo, Follow, Patrol, Wander, Flee, Reprogram");
            Assert((int)RobotObjectiveState.Idle == 0 && (int)RobotObjectiveState.Seeking == 1
                && (int)RobotObjectiveState.Arrived == 2 && (int)RobotObjectiveState.Dwelling == 3
                && (int)RobotObjectiveState.TargetMissing == 4 && (int)RobotObjectiveState.Cancelled == 5
                && (int)RobotObjectiveState.Delivered == 6,
                "RobotObjectiveState order must end with the appended Delivered");

            // Wander factories: home = nothing (agent position), a named target, or a fixed point.
            var wanderHere = RobotObjective.Wander();
            Assert(wanderHere.Kind == RobotObjectiveKind.Wander && wanderHere.TargetName == null
                && wanderHere.TargetPoint == null && wanderHere.Payload == null,
                "Wander() roams the set-time position and carries no payload");
            Assert(Math.Abs(wanderHere.WanderRadius - 8f) < 1e-6f, "WanderRadius defaults to 8 m");
            wanderHere.WanderRadius = 3f;
            Assert(Math.Abs(wanderHere.WanderRadius - 3f) < 1e-6f, "WanderRadius is a settable knob");
            Assert(RobotObjective.Wander("PAD").TargetName == "PAD", "Wander(name) anchors to the named target");
            Assert(RobotObjective.Wander(new Vec3(1f, 2f, 3f)).TargetPoint != null, "Wander(point) anchors to the point");

            // Flee factory and its distance knob.
            var flee = RobotObjective.Flee("PLAYER");
            Assert(flee.Kind == RobotObjectiveKind.Flee && flee.TargetName == "PLAYER" && flee.Payload == null,
                "Flee(name) targets the threat by name");
            Assert(Math.Abs(flee.FleeDistance - 8f) < 1e-6f, "FleeDistance defaults to 8 m");

            // Reprogram: courier + payload, by reference, with the no-chain-letters guard.
            var payload = RobotObjective.Follow("PLAYER");
            var courier = RobotObjective.Reprogram("ROBOT 2", payload);
            Assert(courier.Kind == RobotObjectiveKind.Reprogram && courier.TargetName == "ROBOT 2"
                && ReferenceEquals(courier.Payload, payload),
                "Reprogram keeps the recipient name and the payload by reference");

            var threwNull = false;
            try
            {
                _ = RobotObjective.Reprogram("ROBOT 2", null!);
            }
            catch (ArgumentNullException)
            {
                threwNull = true;
            }

            Assert(threwNull, "Reprogram null-guards the payload");

            var threwNested = false;
            try
            {
                _ = RobotObjective.Reprogram("ROBOT 3", courier);
            }
            catch (ArgumentException)
            {
                threwNested = true;
            }

            Assert(threwNested, "a Reprogram payload cannot itself be a Reprogram");

            // Describe() covers the new kinds (HUD badges and ground-truth facts read these).
            Assert(RobotObjective.Wander().Describe() == "WANDER", "Wander() describes as WANDER");
            Assert(RobotObjective.Wander("RED MARKER").Describe() == "WANDER NEAR RED MARKER",
                "a named wander describes its anchor");
            Assert(flee.Describe() == "FLEE FROM PLAYER", "a flee describes its threat");
            Assert(courier.Describe() == "REPROGRAM ROBOT 2: FOLLOW PLAYER", "a courier describes recipient and payload");

            // The delivery event contract.
            var delivered = typeof(IRobotObjectiveService).GetEvent("ProgramDelivered");
            Assert(delivered != null && delivered.EventHandlerType == typeof(Action<RobotProgramDelivery>),
                "IRobotObjectiveService exposes ProgramDelivered as Action<RobotProgramDelivery>");

            var threwDelivery = false;
            try
            {
                _ = new RobotProgramDelivery(null!, null!, payload);
            }
            catch (ArgumentNullException)
            {
                threwDelivery = true;
            }

            Assert(threwDelivery, "RobotProgramDelivery null-guards its parts");
        }

        // The pause-menu customization contract (session-scoped vanilla pause menu integration).
        private static void TestPauseMenuContracts()
        {
            Assert(typeof(IWorldPauseMenuService).GetProperty("IsAvailable") != null,
                "IWorldPauseMenuService exposes IsAvailable");
            var register = typeof(IWorldPauseMenuService).GetMethod("RegisterAction");
            Assert(register != null && register.ReturnType == typeof(IDisposable),
                "RegisterAction returns an IDisposable handle");
            Assert(typeof(IWorldPauseMenuService).GetMethod("SetExitInterceptor") != null,
                "IWorldPauseMenuService exposes SetExitInterceptor");
            Assert(typeof(WorldPauseAction).GetConstructors().Length == 1,
                "WorldPauseAction exposes one canonical constructor");

            var action = new WorldPauseAction("mod.action", "DO THING", () => { });
            Assert(action.Id == "mod.action" && action.Label == "DO THING", "WorldPauseAction keeps id and label");
            Assert(action.ClosePauseMenu && action.Order == 0 && !action.Destructive,
                "WorldPauseAction defaults: close menu, order 0, non-destructive");
            var destructive = new WorldPauseAction("mod.reset", "RESET", () => { }, true, 0, destructive: true);
            Assert(destructive.Destructive, "WorldPauseAction keeps its destructive-confirmation contract");

            var threw = false;
            try
            {
                _ = new WorldPauseAction("id", "label", null!);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }

            Assert(threw, "WorldPauseAction null-guards the callback");

            foreach (var invalid in new[] { "", " " })
            {
                try
                {
                    _ = new WorldPauseAction(invalid, "label", () => { });
                    Assert(false, "WorldPauseAction must reject a blank id");
                }
                catch (ArgumentException)
                {
                }
            }

            var session = new WorldSession("world", "gamemode", "gameScene", "Scene", DateTime.UtcNow);
            Assert(ReferenceEquals(new WorldPauseExitContext(session).Session, session),
                "WorldPauseExitContext carries the session");

            Assert((int)WorldPauseExitDecision.EndSessionAndExit == 0 && (int)WorldPauseExitDecision.ExitWithoutEnding == 1
                && (int)WorldPauseExitDecision.Block == 2,
                "WorldPauseExitDecision order must be EndSessionAndExit, ExitWithoutEnding, Block");
        }

        // The custom-world contract: mod-shipped content (e.g. a bundle prefab) registered as a playable world.
        private static void TestCustomWorldContracts()
        {
            Assert(WellKnownIds.SandboxGamemodeId == "io.github.furroxide.topiaforge.worlds.sandbox"
                && WellKnownIds.OpenSandboxWorldId == "io.github.furroxide.topiaforge.worlds.open_sandbox",
                "WellKnownIds must pin the published sandbox gamemode/world ids");

            var registerContent = typeof(IWorldGamemodeService).GetMethod(
                "RegisterWorld", new[] { typeof(WorldDefinition), typeof(ICustomWorldContent) });
            Assert(registerContent != null, "IWorldGamemodeService exposes RegisterWorld(WorldDefinition, ICustomWorldContent)");
            var unregister = typeof(IWorldGamemodeService).GetMethod("UnregisterWorld");
            Assert(unregister != null && unregister.ReturnType == typeof(bool)
                && unregister.GetParameters().Length == 1 && unregister.GetParameters()[0].ParameterType == typeof(string),
                "IWorldGamemodeService exposes bool UnregisterWorld(string)");

            var unregisterGamemode = typeof(IWorldRegistrationService).GetMethod("UnregisterGamemode");
            var unregisterMenuEntry = typeof(IWorldRegistrationService).GetMethod("UnregisterMenuEntry");
            Assert(unregisterGamemode != null && unregisterGamemode.ReturnType == typeof(bool),
                "optional world registration capability exposes bool UnregisterGamemode(string)");
            Assert(unregisterMenuEntry != null && unregisterMenuEntry.ReturnType == typeof(bool),
                "optional world registration capability exposes bool UnregisterMenuEntry(string)");

            var createRoot = typeof(ICustomWorldContent).GetMethod("CreateContentRoot");
            Assert(createRoot != null && createRoot.ReturnType == typeof(object), "ICustomWorldContent exposes CreateContentRoot(): object?");
            Assert(typeof(ICustomWorldContent).GetProperty("Options")?.PropertyType == typeof(CustomWorldOptions),
                "ICustomWorldContent exposes CustomWorldOptions Options");

            var options = CustomWorldOptions.Default;
            Assert(options.SpawnPointName == "SpawnPoint" && options.ApplyDefaultEnvironment
                && options.EnableKillPlane && options.KillPlaneDepth == 100f,
                "CustomWorldOptions defaults: SpawnPoint marker, default env, kill plane at 100m");
            Assert(!ReferenceEquals(CustomWorldOptions.Default, CustomWorldOptions.Default),
                "CustomWorldOptions.Default returns fresh instances");

            var bundleOptions = new BundleWorldOptions();
            Assert(bundleOptions.PrefabAssetName == "" && bundleOptions.RegisterSandboxMenuEntry
                && bundleOptions.MenuEntryId == "", "BundleWorldOptions defaults: single-prefab bundle, sandbox menu entry");

            // RegisterWorldFromBundle wires definition + content + sandbox menu entry through the service.
            var context = new FakeContext();
            var worlds = new FakeWorldService();
            var definition = context.RegisterWorldFromBundle(worlds, new BundleWorldOptions
            {
                Id = "test.worlds.island",
                Name = "Island",
                Description = "A test world.",
                BundleRelativePath = "AssetBundles/island.bundle",
            });
            Assert(definition.Id == "test.worlds.island" && ReferenceEquals(worlds.LastWorld, definition),
                "RegisterWorldFromBundle registers the definition");
            Assert(worlds.LastContent is BundleWorldContent, "RegisterWorldFromBundle attaches BundleWorldContent");
            Assert(worlds.LastMenuEntry != null && worlds.LastMenuEntry.Id == "test.worlds.island.menu"
                && worlds.LastMenuEntry.GamemodeId == WellKnownIds.SandboxGamemodeId
                && worlds.LastMenuEntry.WorldId == "test.worlds.island",
                "RegisterWorldFromBundle registers a sandbox-paired menu entry by default");

            worlds.LastMenuEntry = null;
            context.RegisterWorldFromBundle(worlds, new BundleWorldOptions
            {
                Id = "test.worlds.quiet",
                Name = "Quiet",
                BundleRelativePath = "AssetBundles/q.bundle",
                RegisterSandboxMenuEntry = false,
            });
            Assert(worlds.LastMenuEntry == null, "RegisterSandboxMenuEntry=false suppresses the menu entry");

            foreach (var invalid in new[]
            {
                new BundleWorldOptions { Name = "n", BundleRelativePath = "b" },
                new BundleWorldOptions { Id = "i", BundleRelativePath = "b" },
                new BundleWorldOptions { Id = "i", Name = "n" },
            })
            {
                try
                {
                    context.RegisterWorldFromBundle(worlds, invalid);
                    Assert(false, "RegisterWorldFromBundle must reject blank Id/Name/BundleRelativePath");
                }
                catch (ArgumentException)
                {
                }
            }

            // BundleWorldContent failure paths degrade to null (never throw): the fake asset service exposes
            // one asset named "prefab" (no .prefab suffix), so the single-prefab resolution finds nothing.
            var assetService = new FakeAssetBundleService();
            context.Services[typeof(IAssetBundleService)] = assetService;
            var noPrefab = new BundleWorldContent(context, "AssetBundles/x.bundle");
            Assert(noPrefab.CreateContentRoot() == null, "a bundle with no *.prefab resolves to null content");

            // With a pinned asset name the load succeeds, but this test process has no UnityEngine —
            // the GameObject type resolution must degrade to a logged null, not a throw.
            var pinned = new BundleWorldContent(context, "AssetBundles/x.bundle", "assets/world.prefab");
            Assert(pinned.CreateContentRoot() == null, "content creation degrades to null outside a Unity runtime");

            context.Services.Remove(typeof(IAssetBundleService));
            var noService = new BundleWorldContent(context, "AssetBundles/x.bundle");
            Assert(noService.CreateContentRoot() == null, "a missing io.github.furroxide.topiaforge.assets service degrades to null content");
        }

        // The multi-turn conversation primitive (IRobotConversationService): request defaults + the pollable handle
        // surface, so the Unity-free contract cannot regress silently.
        private static void TestConversationContracts()
        {
            var request = new RobotConversationRequest("frame", new[] { "CONVERT", "REFUSE" });
            Assert(request.SystemFrame == "frame", "RobotConversationRequest keeps the system frame");
            Assert(request.DecisionOptions.Count == 2 && request.DecisionOptions[0] == "CONVERT", "decision options are kept in order");
            Assert(request.MaxTurns == 3, "MaxTurns defaults to 3");
            Assert(Math.Abs(request.Temperature - 0.7f) < 1e-6, "Temperature defaults to 0.7");
            Assert(request.MaxReplyChars == 200, "MaxReplyChars defaults to 200");
            Assert(request.Usage == "robot-conversation", "Usage defaults");

            var nullRequest = new RobotConversationRequest(null!, null!);
            Assert(nullRequest.SystemFrame == string.Empty && nullRequest.DecisionOptions.Count == 0, "request null-guards frame/options");

            Assert(request.LiveFacts == null, "LiveFacts defaults to null (static facts only)");
            request.LiveFacts = () => new Dictionary<string, string> { ["k"] = "v" };
            Assert(request.LiveFacts()!["k"] == "v", "LiveFacts is a settable per-turn provider");

            var begin = typeof(IRobotConversationService).GetMethod("BeginConversation");
            Assert(begin != null && begin.ReturnType == typeof(IRobotConversation), "BeginConversation returns IRobotConversation");
            Assert(typeof(IRobotConversationService).GetProperty("IsAvailable") != null, "service exposes IsAvailable");
            foreach (var member in new[] { "IsThinking", "TurnReady", "Ended", "TurnCount", "LastReply", "LastDecision" })
            {
                Assert(typeof(IRobotConversation).GetProperty(member) != null, "IRobotConversation should expose " + member);
            }

            Assert(typeof(IRobotConversation).GetMethod("Submit") != null, "IRobotConversation should expose Submit");
            Assert(typeof(IRobotConversation).GetMethod("End") != null, "IRobotConversation should expose End");
        }

        // The player dialogue input (text + voice) contract surface.
        private static void TestDialogueInputContracts()
        {
            var begin = typeof(IPlayerDialogueInputService).GetMethod("BeginVoiceCapture");
            Assert(begin != null && begin.ReturnType == typeof(IVoiceCapture), "BeginVoiceCapture returns IVoiceCapture");
            Assert(typeof(IPlayerDialogueInputService).GetProperty("IsVoiceAvailable") != null, "service exposes IsVoiceAvailable");
            foreach (var member in new[] { "IsRecording", "IsComplete", "Found", "Text" })
            {
                Assert(typeof(IVoiceCapture).GetProperty(member) != null, "IVoiceCapture should expose " + member);
            }

            Assert(typeof(IVoiceCapture).GetMethod("Stop") != null && typeof(IVoiceCapture).GetMethod("Cancel") != null, "IVoiceCapture should expose Stop/Cancel");

            // TextInputBuffer is a concrete shared helper — exercise its core behaviour.
            var buffer = new TextInputBuffer(4);
            buffer.Append("ab");
            buffer.Append("cdef"); // clamps at 4
            Assert(buffer.Text == "abcd", "TextInputBuffer clamps to maxChars");
            buffer.Append("\b");
            Assert(buffer.Text == "abc", "TextInputBuffer honours backspace");
            buffer.Append("\n");
            Assert(buffer.ConsumeSubmit() && !buffer.ConsumeSubmit(), "TextInputBuffer submit is one-shot");
        }

        // The structured brain-query primitive (IRobotBrainQueryService): guard the enum order, the request/result
        // defaults, and the pollable-handle surface so the Unity-free contract cannot regress silently.
        private static void TestBrainQueryContracts()
        {
            Assert((int)RobotDecision.Comply == 0 && (int)RobotDecision.Freeze == 1 && (int)RobotDecision.Flee == 2
                && (int)RobotDecision.Resist == 3 && (int)RobotDecision.Unknown == 4, "RobotDecision order must be Comply,Freeze,Flee,Resist,Unknown");
            Assert((int)BrainFieldType.String == 0 && (int)BrainFieldType.Number == 1 && (int)BrainFieldType.Boolean == 2,
                "BrainFieldType order must be String,Number,Boolean");

            var field = new BrainOutputField("action", "the reaction", BrainFieldType.String, new[] { "comply", "resist" });
            Assert(field.Name == "action" && field.Type == BrainFieldType.String, "BrainOutputField should keep name/type");
            Assert(field.AllowedStrings != null && field.AllowedStrings.Count == 2, "BrainOutputField should keep its allowed strings");

            var request = new BrainQueryRequest("hello", new[] { field });
            Assert(request.Prompt == "hello" && request.Outputs.Count == 1, "BrainQueryRequest should keep prompt and outputs");
            Assert(request.Usage == "robot-brain-query", "BrainQueryRequest.Usage should default");
            Assert(Math.Abs(request.Temperature - 0.7f) < 1e-6 && !request.UseReasoning, "BrainQueryRequest defaults: temp 0.7, no reasoning");

            var nullRequest = new BrainQueryRequest(null!, null!);
            Assert(nullRequest.Prompt == string.Empty && nullRequest.Outputs.Count == 0, "BrainQueryRequest should null-guard prompt/outputs");

            var unavailable = BrainQueryResult.Unavailable;
            Assert(!unavailable.Available && !unavailable.Succeeded, "Unavailable result should be not-available, not-succeeded");
            Assert(unavailable.Values.Count == 0 && !unavailable.TryGet("x", out _), "Unavailable result should have empty values");

            var ok = new BrainQueryResult(true, true, new Dictionary<string, string> { ["action"] = "comply" }, null);
            Assert(ok.TryGet("action", out var action) && action == "comply", "BrainQueryResult.TryGet should return a present value");
            Assert(!ok.TryGet("missing", out _), "BrainQueryResult.TryGet should be false for a missing key");

            // Pollable-handle surface (Unity-free reflection, loads no UnityEngine).
            var serviceBegin = typeof(IRobotBrainQueryService).GetMethod("BeginQuery");
            Assert(serviceBegin != null && serviceBegin.ReturnType == typeof(IRobotBrainQuery), "IRobotBrainQueryService.BeginQuery should return IRobotBrainQuery");
            Assert(typeof(IRobotBrainQueryService).GetProperty("IsAvailable") != null, "IRobotBrainQueryService should expose IsAvailable");
            var complete = typeof(IRobotBrainQuery).GetProperty("IsComplete");
            Assert(complete != null && complete.PropertyType == typeof(bool), "IRobotBrainQuery should expose a bool IsComplete");
            var result = typeof(IRobotBrainQuery).GetProperty("Result");
            Assert(result != null && result.PropertyType == typeof(BrainQueryResult), "IRobotBrainQuery.Result should be a BrainQueryResult");
        }

        // The shop contract (catalog item + wallet + purchase arbiter) consumed by the TopiaForgeUi shop pane.
        // Behaviour lives in ShopTests; this pins the surface so it cannot regress silently.
        private static void TestShopContracts()
        {
            var item = new ShopItem("mod.item", "ITEM", "desc", 25);
            Assert(item.Category == string.Empty && item.MaxPurchases == 0,
                "ShopItem defaults: no category chip, unlimited purchases");

            Assert(typeof(IShopWallet).IsAssignableFrom(typeof(ShopWallet)), "ShopWallet implements IShopWallet");
            var balanceChanged = typeof(IShopWallet).GetEvent("BalanceChanged");
            Assert(balanceChanged != null && balanceChanged.EventHandlerType == typeof(Action<int>),
                "IShopWallet exposes BalanceChanged as Action<int>");
            var trySpend = typeof(IShopWallet).GetMethod("TrySpend");
            Assert(trySpend != null && trySpend.ReturnType == typeof(bool)
                && trySpend.GetParameters().Length == 1 && trySpend.GetParameters()[0].ParameterType == typeof(int),
                "IShopWallet exposes bool TrySpend(int)");
            Assert(typeof(IShopWallet).GetProperty("Balance")?.PropertyType == typeof(int),
                "IShopWallet exposes int Balance");

            // Pin the result set: shop UIs and mods switch on these, so a silent rename/reorder is breaking.
            Assert((int)ShopPurchaseResult.Purchased == 0 && (int)ShopPurchaseResult.InsufficientFunds == 1
                && (int)ShopPurchaseResult.SoldOut == 2 && (int)ShopPurchaseResult.Rejected == 3,
                "ShopPurchaseResult order must be Purchased, InsufficientFunds, SoldOut, Rejected");

            var tryPurchase = typeof(ShopTransactions).GetMethod("TryPurchase");
            Assert(tryPurchase != null && tryPurchase.ReturnType == typeof(ShopPurchaseResult)
                && tryPurchase.GetParameters().Length == 4,
                "ShopTransactions exposes TryPurchase(item, wallet, timesPurchased, canPurchase)");
        }

        private static void TestVec3RoundTrip()
        {
            var v = new Vec3(1.5f, -2f, 3.25f);
            Assert(v.X == 1.5f && v.Y == -2f && v.Z == 3.25f, "Vec3 components should round-trip");

            var array = v.ToArray();
            Assert(array.Length == 3 && array[0] == 1.5f && array[1] == -2f && array[2] == 3.25f, "ToArray should be [x,y,z]");

            var back = Vec3.FromArray(array);
            Assert(back.Equals(v), "FromArray(ToArray()) should round-trip");

            Assert(Vec3.FromArray(null).Equals(Vec3.Zero), "FromArray(null) should be Zero");
            Assert(Vec3.FromArray(new[] { 1f }).Equals(Vec3.Zero), "FromArray of a too-short array should be Zero");
            Assert(Vec3.Zero.Equals(new Vec3(0f, 0f, 0f)), "Zero should equal (0,0,0)");
        }

        private static void TestVec3Equality()
        {
            var a = new Vec3(1f, 2f, 3f);
            var b = new Vec3(1f, 2f, 3f);
            var c = new Vec3(1f, 2f, 4f);
            Assert(a.Equals(b) && a.GetHashCode() == b.GetHashCode(), "equal Vec3 values should be equal and hash equally");
            Assert(!a.Equals(c), "different Vec3 values should not be equal");
            Assert(a.Equals((object)b) && !a.Equals((object)"x"), "object Equals should match value and reject other types");
        }

        private static void TestRobotColor()
        {
            var c = new RobotColor(0.55f, 1f, 0.35f);
            Assert(c.R == 0.55f && c.G == 1f && c.B == 0.35f && c.A == 1f, "RobotColor should default alpha to opaque");

            var explicitAlpha = new RobotColor(0.1f, 0.2f, 0.3f, 0.4f);
            Assert(explicitAlpha.A == 0.4f, "RobotColor should keep an explicit alpha");

            var same = new RobotColor(0.55f, 1f, 0.35f, 1f);
            Assert(c.Equals(same) && c.GetHashCode() == same.GetHashCode(), "equal RobotColor values should be equal and hash equally");
            Assert(!c.Equals(new RobotColor(0f, 0f, 0f)), "different RobotColor values should not be equal");
            Assert(c.Equals((object)same) && !c.Equals((object)"x"), "object Equals should match value and reject other types");
            Assert(RobotColor.White.Equals(new RobotColor(1f, 1f, 1f, 1f)), "White should be opaque white");
        }

        private static void TestRobotAgentSpawnRequestDefaults()
        {
            var request = new RobotAgentSpawnRequest(new Vec3(1f, 2f, 3f));
            Assert(request.Position.Equals(new Vec3(1f, 2f, 3f)), "spawn request should keep its position");
            Assert(request.Facing == null, "facing should default to null");
            Assert(request.BrainMode == RobotBrainMode.Dormant, "a default robot's brain should be dormant");
            Assert(request.Gait == RobotGait.Run, "the default gait should be Run");
            Assert(request.MoveSpeed == 0f && request.TurnSpeed == 0f, "speed overrides should default to 0 (keep prefab default)");
            Assert(request.StopDistance == 0f, "stop distance should default to 0");
            Assert(request.Tint == null, "tint should default to null (native colours)");
            Assert(request.Name == null, "name should default to null");
            Assert(request.Scale == 1f, "scale should default to 1 (native size)");
            var interaction = request.Interaction ?? throw new InvalidOperationException("interaction should default to a policy object");
            Assert(interaction.NativeTalkMode == RobotNativeTalkMode.Enabled, "interaction should default to native talk");
            Assert(interaction.NativeTalkDistance == 0f, "native talk distance should default to prefab distance");
            Assert(interaction.CustomInteraction == null, "custom interaction should default to null");

            var facing = new RobotAgentSpawnRequest(Vec3.Zero, new Vec3(0f, 0f, 1f)) { BrainMode = RobotBrainMode.Autonomous };
            Assert(facing.Facing.HasValue && facing.Facing.Value.Equals(new Vec3(0f, 0f, 1f)), "facing should round-trip when provided");
            Assert(facing.BrainMode == RobotBrainMode.Autonomous, "brain mode should be settable to Autonomous");

            Assert(request.RobotTypeId == null, "robot type should default to null (default type)");
            request.RobotTypeId = "worker-robot";
            Assert(request.RobotTypeId == "worker-robot", "robot type id should round-trip");
        }

        // The robot type catalog and runtime brain-switch surface: a spawn UI's contract with RobotKit.
        private static void TestRobotTypeAndBrainSwitchContracts()
        {
            var descriptor = new RobotTypeDescriptor("worker-robot", "Worker Robot");
            Assert(descriptor.Id == "worker-robot" && descriptor.DisplayName == "Worker Robot",
                "RobotTypeDescriptor keeps id and display name");
            Assert(new RobotTypeDescriptor("slug", " ").DisplayName == "slug",
                "a blank display name falls back to the id");

            var types = typeof(IRobotAgentService).GetProperty("RobotTypes");
            Assert(types != null && typeof(IReadOnlyList<RobotTypeDescriptor>).IsAssignableFrom(types.PropertyType),
                "IRobotAgentService exposes the RobotTypes list");
            Assert(typeof(IRobotAgentService).GetMethod("IsRobotPrefab") != null,
                "IRobotAgentService exposes IsRobotPrefab");

            var agent = new FakeRobotAgent();
            Assert(agent.BrainMode == RobotBrainMode.Dormant, "the fake starts dormant");
            agent.SetBrainMode(RobotBrainMode.Autonomous);
            Assert(agent.BrainMode == RobotBrainMode.Autonomous, "SetBrainMode switches the reported mode");

            var kinds = (RobotTargetKind[])Enum.GetValues(typeof(RobotTargetKind));
            Assert(kinds[0] == RobotTargetKind.Custom, "RobotTargetKind.Custom is the default (0)");
            var info = new RobotTargetInfo("  robot 2 ", RobotTargetKind.Robot, "a red one");
            Assert(info.Name == "ROBOT 2" && info.Kind == RobotTargetKind.Robot && info.Description == "a red one",
                "RobotTargetInfo normalises the name and keeps kind/description");
            Assert(typeof(IRobotObjectiveService).GetProperty("Targets") != null
                && typeof(IRobotObjectiveService).GetMethod("TryGetTargetInfo") != null,
                "IRobotObjectiveService exposes the target metadata view");
        }

        private static void TestRobotInteractionContracts()
        {
            Assert((int)RobotNativeTalkMode.Enabled == 0 && (int)RobotNativeTalkMode.Disabled == 1,
                "native talk mode order should be Enabled, Disabled");

            var native = RobotInteractionOptions.NativeTalk();
            Assert(native.NativeTalkMode == RobotNativeTalkMode.Enabled && native.NativeTalkDistance == 0f && native.CustomInteraction == null,
                "NativeTalk should keep the game's talk interaction");

            var distant = RobotInteractionOptions.NativeTalkAtDistance(12f);
            Assert(distant.NativeTalkMode == RobotNativeTalkMode.Enabled && distant.NativeTalkDistance == 12f,
                "NativeTalkAtDistance should keep native talk and store the distance");

            var disabled = RobotInteractionOptions.DisableNativeTalk();
            Assert(disabled.NativeTalkMode == RobotNativeTalkMode.Disabled && disabled.CustomInteraction == null,
                "DisableNativeTalk should disable native talk without installing a callback");

            var invoked = false;
            var custom = new RobotCustomInteraction("Hack robot", _ => invoked = true)
            {
                Distance = 9f,
                ScreenRectExpansion = 0.2f,
                CanInteract = ctx => ctx.Distance < 9f
            };
            var customOptions = RobotInteractionOptions.Custom(custom);
            Assert(customOptions.NativeTalkMode == RobotNativeTalkMode.Disabled && ReferenceEquals(customOptions.CustomInteraction, custom),
                "Custom should disable native talk and keep the custom interaction");
            Assert(custom.Prompt == "Hack robot" && custom.Distance == 9f && Math.Abs(custom.ScreenRectExpansion - 0.2f) < 1e-6,
                "custom interaction should keep prompt, distance, and screen expansion");

            var context = new RobotInteractionContext(
                new FakeRobotAgent(),
                new object(),
                new Vec3(1f, 2f, 3f),
                new Vec3(1f, 2f, 7f),
                4f);
            Assert(context.Agent != null && context.Hand != null, "interaction context should keep agent and hand");
            Assert(context.AgentPosition.Equals(new Vec3(1f, 2f, 3f)) && context.HandPosition.Equals(new Vec3(1f, 2f, 7f)),
                "interaction context should keep positions");
            Assert(context.Distance == 4f && custom.CanInteract!(context), "interaction context should keep distance");
            custom.Interact!(context);
            Assert(invoked, "custom interaction callback should be invokable");

            var setInteraction = typeof(IRobotAgent).GetMethod("SetInteraction");
            Assert(setInteraction != null && setInteraction.GetParameters().Length == 1 &&
                setInteraction.GetParameters()[0].ParameterType == typeof(RobotInteractionOptions),
                "IRobotAgent should expose SetInteraction(RobotInteractionOptions)");
        }

        private static void TestReachableSpawnRequestDefaults()
        {
            var request = new ReachableSpawnRequest(new Vec3(4f, 5f, 6f));
            Assert(request.Origin.Equals(new Vec3(4f, 5f, 6f)), "reachable-spawn request should keep its origin");
            Assert(request.ReachableFrom == null, "ReachableFrom should default to null (uses Origin)");
            Assert(request.MinRadius == 8f, "MinRadius should default to 8");
            Assert(request.MaxRadius == 24f, "MaxRadius should default to 24");
            Assert(request.MaxCandidates == 16, "MaxCandidates should default to 16");
            Assert(request.VerticalScan == 3f, "VerticalScan should default to 3");
            Assert(request.GroundProbeDepth == 12f, "GroundProbeDepth should default to 12");
            Assert(request.HeightOffset == 0.25f, "HeightOffset should default to 0.25");

            var anchored = new ReachableSpawnRequest(Vec3.Zero)
            {
                ReachableFrom = new Vec3(1f, 0f, 2f),
                MinRadius = 5f,
                MaxRadius = 30f,
                MaxCandidates = 24
            };
            Assert(anchored.ReachableFrom.HasValue && anchored.ReachableFrom.Value.Equals(new Vec3(1f, 0f, 2f)), "ReachableFrom should round-trip");
            Assert(anchored.MinRadius == 5f && anchored.MaxRadius == 30f && anchored.MaxCandidates == 24, "request radii/attempts should be settable");
        }

        // HeadPosition is the head/aim anchor the SDK exposes for hit-zone tests (headshots) and world-anchored
        // combat HUD; guard its presence and read-only Vec3 shape so the contract cannot regress silently. The
        // interface is Unity-free, so reflecting its own members loads no UnityEngine types.
        private static void TestRobotAgentSurface()
        {
            var headPosition = typeof(IRobotAgent).GetProperty("HeadPosition");
            Assert(headPosition != null, "IRobotAgent should expose a HeadPosition property");
            Assert(headPosition!.PropertyType == typeof(Vec3), "HeadPosition should be a Vec3");
            Assert(headPosition.CanRead && !headPosition.CanWrite, "HeadPosition should be a read-only property");
        }

        private static void TestRobotAgentEnums()
        {
            // RobotDamageType must mirror the game's native DamageType ordering (Normal, Fire, Electricity, Poison, Water).
            Assert((int)RobotDamageType.Normal == 0, "Normal must be 0");
            Assert((int)RobotDamageType.Fire == 1, "Fire must be 1");
            Assert((int)RobotDamageType.Electricity == 2, "Electricity must be 2");
            Assert((int)RobotDamageType.Poison == 3, "Poison must be 3");
            Assert((int)RobotDamageType.Water == 4, "Water must be 4");

            Assert((int)RobotBrainMode.Dormant == 0, "Dormant must be the default (0) brain mode");
            Assert((int)RobotGait.Walk == 0 && (int)RobotGait.Run == 1 && (int)RobotGait.Sprint == 2, "gait order should be Walk, Run, Sprint");
        }

        private static void TestRequireServiceReturnsRegistered()
        {
            var svc = new FakeService();
            var context = new FakeContext();
            context.Services[typeof(IFakeService)] = svc;
            Assert(ReferenceEquals(context.RequireService<IFakeService>(), svc), "RequireService should return the registered service");
        }

        private static void TestRequireServiceThrowsWhenMissing()
        {
            var context = new FakeContext();
            var threw = false;
            try
            {
                context.RequireService<IFakeService>();
            }
            catch (InvalidOperationException ex)
            {
                threw = ex.Message.Contains("IFakeService");
            }

            Assert(threw, "RequireService should throw an InvalidOperationException naming the missing service type");
        }

        private static void TestTryGetService()
        {
            var svc = new FakeService();
            var context = new FakeContext();

            Assert(!context.TryGetService<IFakeService>(out _), "TryGetService should be false when unregistered");

            context.Services[typeof(IFakeService)] = svc;
            Assert(context.TryGetService<IFakeService>(out var resolved) && ReferenceEquals(resolved, svc),
                "TryGetService should return true and the service when registered");
        }

        private static void TestAssetContracts()
        {
            var options = AssetBundleLoadOptions.Default;
            Assert(options.Cache && !options.Reload, "asset bundle options should cache by default without reload");

            var request = new AssetBundleLoadRequest("owner.mod", "pkg", "AssetBundles/main", options);
            Assert(request.OwnerModId == "owner.mod" && request.PackagePath == "pkg" && request.RelativePath == "AssetBundles/main",
                "asset bundle request should keep owner/package/relative path");

            var handle = new FakeAssetBundleHandle();
            var loadSuccess = AssetBundleLoadResult.Success(handle);
            Assert(loadSuccess.Ok && ReferenceEquals(loadSuccess.Bundle, handle) && loadSuccess.Error == string.Empty,
                "asset bundle load success should expose the handle");
            var loadFail = AssetBundleLoadResult.Fail("missing");
            Assert(!loadFail.Ok && loadFail.Bundle == null && loadFail.Error == "missing", "asset bundle load failure should expose the error");

            var asset = new object();
            var assetSuccess = AssetLoadResult.Success(asset);
            Assert(assetSuccess.Ok && ReferenceEquals(assetSuccess.Asset, asset), "asset load success should expose the asset");
            var typedAssetSuccess = AssetLoadResult<object>.Success(asset);
            Assert(typedAssetSuccess.Ok && ReferenceEquals(typedAssetSuccess.Asset, asset), "typed asset load success should expose the asset");

            var spawnSuccess = SpawnAssetResult.Success(asset);
            Assert(spawnSuccess.Ok && ReferenceEquals(spawnSuccess.Instance, asset), "spawn success should expose the instance");
            var typedSpawnSuccess = SpawnAssetResult<object>.Success(asset);
            Assert(typedSpawnSuccess.Ok && ReferenceEquals(typedSpawnSuccess.Instance, asset), "typed spawn success should expose the instance");
        }

        private static void TestPromptContracts()
        {
            var request = new PromptOverrideRequest("owner.mod", "robot.greeting", "replacement", 7, "why");
            Assert(request.OwnerModId == "owner.mod" && request.PromptId == "robot.greeting", "prompt request should keep owner and prompt id");
            Assert(request.ReplacementText == "replacement" && request.Priority == 7 && request.Description == "why",
                "prompt request should keep replacement metadata");

            var promptOverride = new PromptOverride("owner.mod", "robot.greeting", "replacement", 7, "why");
            var conflict = new PromptConflict("robot.greeting", new[] { promptOverride }, promptOverride);
            Assert(conflict.PromptId == "robot.greeting" && ReferenceEquals(conflict.EffectiveOverride, promptOverride),
                "prompt conflict should expose prompt id and effective override");
            Assert(conflict.Overrides.Count == 1 && ReferenceEquals(conflict.Overrides[0], promptOverride),
                "prompt conflict should keep overrides");
        }

        private static void TestAssetAndPromptContextExtensions()
        {
            var context = new FakeContext();
            var assetService = new FakeAssetBundleService();
            var promptRegistry = new FakePromptOverrideRegistry();
            context.Services[typeof(IAssetBundleService)] = assetService;
            context.Services[typeof(IPromptOverrideRegistry)] = promptRegistry;

            var load = context.LoadAssetBundle("AssetBundles/main");
            Assert(load.Ok && assetService.LastRequest != null, "context.LoadAssetBundle should call the asset service");
            Assert(assetService.LastRequest!.OwnerModId == context.ModId && assetService.LastRequest.PackagePath == context.Paths.PackagePath,
                "context.LoadAssetBundle should inject owner and package path");

            var asset = context.LoadAsset<object>(assetService.Handle, "prefab");
            Assert(asset.Ok && assetService.LastAssetName == "prefab", "context.LoadAsset should call the typed asset helper");

            var prefab = new object();
            var spawn = context.SpawnAsset(prefab);
            Assert(spawn.Ok && ReferenceEquals(assetService.LastPrefab, prefab), "context.SpawnAsset should call the typed spawn helper");

            var prompt = context.RegisterPromptOverride("robot.greeting", "hello", 3, "test");
            Assert(promptRegistry.LastRequest != null && promptRegistry.LastRequest.OwnerModId == context.ModId,
                "context.RegisterPromptOverride should inject the owner mod id");
            Assert(prompt.Override.Priority == 3 && prompt.Override.Description == "test", "prompt helper should keep priority and description");
        }

        private interface IFakeService
        {
        }

        private sealed class FakeService : IFakeService
        {
        }

        private sealed class FakeWorldService : IWorldGamemodeService
        {
            public WorldDefinition? LastWorld { get; private set; }
            public ICustomWorldContent? LastContent { get; private set; }
            public GamemodeMenuEntry? LastMenuEntry { get; set; }

            public IReadOnlyList<WorldDefinition> Worlds => Array.Empty<WorldDefinition>();
            public IReadOnlyList<GamemodeDefinition> Gamemodes => Array.Empty<GamemodeDefinition>();
            public IReadOnlyList<GamemodeMenuEntry> MenuEntries => Array.Empty<GamemodeMenuEntry>();
            public WorldSession? CurrentSession => null;
            public event Action<WorldSession>? SessionChanged;
            public event Action<WorldSessionEnd>? SessionEnded;

            public void RegisterWorld(WorldDefinition world)
            {
                LastWorld = world;
            }

            public void RegisterWorld(WorldDefinition world, ICustomWorldContent content)
            {
                LastWorld = world;
                LastContent = content;
            }

            public bool UnregisterWorld(string worldId)
            {
                return false;
            }

            public void RegisterGamemode(GamemodeDefinition gamemode)
            {
            }

            public void RegisterMenuEntry(GamemodeMenuEntry entry)
            {
                LastMenuEntry = entry;
            }

            public WorldLoadResult Load(WorldLoadRequest request)
            {
                return WorldLoadResult.Fail("not implemented");
            }

            public WorldLoadResult LaunchMenuEntry(string entryId)
            {
                return WorldLoadResult.Fail("not implemented");
            }

            public void EndSession(WorldSessionEndReason reason)
            {
            }

            // Keep the compiler from warning the events are unused without changing the public surface.
            public void RaiseForCoverage(WorldSession session)
            {
                SessionChanged?.Invoke(session);
                SessionEnded?.Invoke(new WorldSessionEnd(session, WorldSessionEndReason.MenuReached));
            }
        }

        private sealed class FakeAssetBundleService : IAssetBundleService
        {
            public FakeAssetBundleHandle Handle { get; } = new FakeAssetBundleHandle();
            public AssetBundleLoadRequest? LastRequest { get; private set; }
            public string LastAssetName { get; private set; } = string.Empty;
            public object? LastPrefab { get; private set; }

            public AssetBundleLoadResult LoadBundle(AssetBundleLoadRequest request)
            {
                LastRequest = request;
                return AssetBundleLoadResult.Success(Handle);
            }

            public AssetLoadResult LoadAsset(IAssetBundleHandle bundle, string assetName, Type assetType)
            {
                LastAssetName = assetName;
                return AssetLoadResult.Success(new object());
            }

            public AssetLoadResult<T> LoadAsset<T>(IAssetBundleHandle bundle, string assetName) where T : class
            {
                LastAssetName = assetName;
                return AssetLoadResult<T>.Success(new object() as T ?? throw new InvalidOperationException("Unexpected test type."));
            }

            public SpawnAssetResult SpawnAsset(object prefab)
            {
                LastPrefab = prefab;
                return SpawnAssetResult.Success(prefab);
            }

            public SpawnAssetResult<T> SpawnAsset<T>(T prefab) where T : class
            {
                LastPrefab = prefab;
                return SpawnAssetResult<T>.Success(prefab);
            }

            public IReadOnlyList<string> GetAllAssetNames(IAssetBundleHandle bundle)
            {
                return new[] { "prefab" };
            }

            public void UnloadOwner(string ownerModId, bool unloadAllLoadedObjects = false)
            {
            }
        }

        private sealed class FakeAssetBundleHandle : IAssetBundleHandle
        {
            public string FullPath => "pkg/AssetBundles/main";
            public object Bundle { get; } = new object();
            public IReadOnlyList<string> OwnerModIds => new[] { "test.mod" };
            public bool IsLoaded => true;
        }

        private sealed class FakePromptOverrideRegistry : IPromptOverrideRegistry
        {
            private readonly List<PromptOverride> overrides = new List<PromptOverride>();

            public PromptOverrideRequest? LastRequest { get; private set; }
            public IReadOnlyList<PromptOverride> Overrides => overrides;

            public IPromptOverrideHandle Register(PromptOverrideRequest request)
            {
                LastRequest = request;
                var promptOverride = new PromptOverride(
                    request.OwnerModId,
                    request.PromptId,
                    request.ReplacementText,
                    request.Priority,
                    request.Description);
                overrides.Add(promptOverride);
                return new FakePromptOverrideHandle(promptOverride);
            }

            public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride)
            {
                promptOverride = overrides.FirstOrDefault(o => o.PromptId == promptId);
                return promptOverride != null;
            }

            public IReadOnlyList<PromptConflict> GetConflicts()
            {
                return Array.Empty<PromptConflict>();
            }

            public void UnregisterOwner(string ownerModId)
            {
                overrides.RemoveAll(o => o.ModId == ownerModId);
            }
        }

        private sealed class FakePromptOverrideHandle : IPromptOverrideHandle
        {
            public FakePromptOverrideHandle(PromptOverride promptOverride)
            {
                Override = promptOverride;
            }

            public PromptOverride Override { get; }
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }

        private sealed class FakeRobotAgent : IRobotAgent
        {
            public string Id => "fake";
            public object GameObject { get; } = new object();
            public bool IsAlive => true;
            public Vec3 Position => Vec3.Zero;
            public Vec3 HeadPosition => Vec3.Zero;
            public RobotBrainMode BrainMode { get; private set; } = RobotBrainMode.Dormant;
            public bool IsMoving => false;
            public bool HasReachedTarget => false;
            public float MoveSpeed { get; set; }
            public float TurnSpeed { get; set; }
            public float StopDistance { get; set; }
            public RobotGait Gait { get; set; }
            public void MoveTo(Vec3 position) { }
            public void Chase(object targetGameObject) { }
            public void Stop() { }
            public void SetBrainMode(RobotBrainMode mode) => BrainMode = mode;
            public void SetTint(RobotColor color) { }
            public void SetEmote(string emojiShortcode) { }
            public void SetName(string name) { }
            public void SetScale(float scale) { }
            public void SetInteraction(RobotInteractionOptions options) { }
            public bool ApplyDamage(float amount, RobotDamageType type, string source) => false;
            public void Kill(RobotDamageType type, string source) { }
            public void Ragdoll() { }
            public void Knockback(Vec3 impulse) { }
            public void Despawn() { }
        }

        // Minimal IModContext for testing the service-resolution extensions; only GetService is exercised.
        private sealed class FakeContext : IModContext
        {
            public Dictionary<Type, object> Services { get; } = new Dictionary<Type, object>();

            public string ModId => "test.mod";
            public string ModName => "Test";
            public Version Version => new Version(1, 0, 0);
            public ModPaths Paths => new ModPaths("pkg", "cfg", "data");
            public IModLogger Logger => new NullLogger();

            public event Action<float>? Update;
            public event Action<string>? SceneLoaded;

            public T LoadConfig<T>(T defaultValue) where T : class => defaultValue;

            public void SaveConfig<T>(T config) where T : class
            {
            }

            public T? GetService<T>() where T : class
            {
                return Services.TryGetValue(typeof(T), out var service) ? (T)service : null;
            }

            // Keep the compiler from warning the events are unused without changing the public surface.
            public void RaiseForCoverage()
            {
                Update?.Invoke(0f);
                SceneLoaded?.Invoke(string.Empty);
            }
        }

        private sealed class NullLogger : IModLogger
        {
            public void Debug(string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }
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
