using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestCompleteCoreServiceContext()
        {
            var context = new FakeModContext();
            context.Scenes.CompleteLoadsImmediately = false;
            var sceneLoad = context.Scenes.LoadAsync(new SceneLoadRequest("PuzzleRoom"));
            Assert(!sceneLoad.IsCompleted && context.Scenes.PendingLoadCount == 1,
                "scene completion can be held for in-flight assertions");
            Assert(context.Scenes.CompleteNextLoad() && sceneLoad.Result.Succeeded &&
                   context.Scenes.ActiveScene == "PuzzleRoom",
                "manual scene completion updates typed state and completes the task");

            var policyChangeLoad = context.Scenes.LoadAsync(new SceneLoadRequest("PolicyChangeRoom"));
            context.Scenes.CompleteLoadsImmediately = true;
            var policyChangeOverlap = context.Scenes.LoadAsync(new SceneLoadRequest("PolicyChangeOverlap"));
            Assert(!policyChangeLoad.IsCompleted
                && policyChangeOverlap.IsCompletedSuccessfully
                && policyChangeOverlap.Result.ErrorCode == ModErrorCode.Conflict
                && context.Scenes.CompleteNextLoad()
                && policyChangeLoad.Result.Succeeded
                && context.Scenes.ActiveScene == "PolicyChangeRoom",
                "changing fake completion policy cannot bypass the production-equivalent native-load slot");
            var immediateAfterPolicyChange = context.Scenes.LoadAsync(
                new SceneLoadRequest("ImmediateAfterPolicyChange"));
            Assert(immediateAfterPolicyChange.IsCompletedSuccessfully
                && immediateAfterPolicyChange.Result.Succeeded
                && context.Scenes.ActiveScene == "ImmediateAfterPolicyChange",
                "manual completion releases the slot before the updated immediate policy is applied");
            context.Scenes.CompleteLoadsImmediately = false;

            using (var committedCancellation = new System.Threading.CancellationTokenSource())
            {
                var committedLoad = context.Scenes.LoadAsync(
                    new SceneLoadRequest("CommittedRoom"),
                    committedCancellation.Token);
                committedCancellation.Cancel();
                using var cancelledOverlapToken = new System.Threading.CancellationTokenSource();
                cancelledOverlapToken.Cancel();
                var cancelledOverlappingLoad = context.Scenes.LoadAsync(
                    new SceneLoadRequest("CancelledOverlappingRoom"),
                    cancelledOverlapToken.Token);
                var overlappingLoad = context.Scenes.LoadAsync(
                    new SceneLoadRequest("OverlappingRoom"));
                Assert(committedLoad.IsCompleted
                    && committedLoad.Result.ErrorCode == ModErrorCode.Cancelled
                    && cancelledOverlappingLoad.IsCompletedSuccessfully
                    && cancelledOverlappingLoad.Result.ErrorCode == ModErrorCode.Conflict
                    && overlappingLoad.IsCompletedSuccessfully
                    && overlappingLoad.Result.ErrorCode == ModErrorCode.Conflict
                    && context.Scenes.CompleteNextLoad()
                    && context.Scenes.ActiveScene == "CommittedRoom",
                    "cancelling before or after an overlapping dispatch cannot bypass the occupied native-load slot");
            }

            var admittedAfterCompletion = context.Scenes.LoadAsync(
                new SceneLoadRequest("AdmittedAfterCompletion"));
            Assert(!admittedAfterCompletion.IsCompleted
                && context.Scenes.CompleteNextLoad()
                && admittedAfterCompletion.Result.Succeeded
                && context.Scenes.ActiveScene == "AdmittedAfterCompletion",
                "manual native completion releases the fake scene-load slot for the next request");

            context.Scenes.Load("PuzzleRoom");

            var sceneTransitions = new List<SceneLoadEvent>();
            var legacySceneTransitions = 0;
            context.Events.SubscribeSceneLoaded((string _) => legacySceneTransitions++);
            context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) => sceneTransitions.Add(scene));
            context.Scenes.Load("Lighting", SceneLoadMode.Additive);
            Assert(context.Scenes.ActiveScene == "PuzzleRoom"
                && sceneTransitions.Count == 1
                && !sceneTransitions[0].IsAuthoritativeReplacement,
                "the fake mirrors production background-additive load semantics");
            Assert(context.Scenes.Activate("Lighting")
                && context.Scenes.ActiveScene == "Lighting"
                && sceneTransitions.Count == 2
                && sceneTransitions[1].Mode == SceneLoadMode.Additive
                && sceneTransitions[1].IsAuthoritativeReplacement,
                "explicit additive activation emits a detail-only authoritative transition");
            Assert(context.Scenes.Activate("PuzzleRoom")
                && sceneTransitions.Count == 3
                && sceneTransitions[2].Mode == SceneLoadMode.Single
                && legacySceneTransitions == 1
                && sceneTransitions[2].IsAuthoritativeReplacement,
                "activation preserves the scene's original load mode without replaying legacy load events");
            Assert(!new SceneLoadEvent("StreamingLoader", SceneLoadMode.Additive, isActive: true)
                    .IsAuthoritativeReplacement
                && new SceneLoadEvent("StreamingLoader", SceneLoadMode.Single, isActive: true)
                    .IsAuthoritativeReplacement,
                "temporary active additive loader scenes do not reset gameplay providers, while single loads do");

            var checkpointCalls = 0;
            context.Scenes.SubscribeCheckpointChanged(_ => throw new InvalidOperationException("expected"));
            var checkpointLease = context.Scenes.SubscribeCheckpointChanged(_ => checkpointCalls++);
            context.Scenes.SetCheckpoint(new CheckpointSnapshot(
                "puzzle-room.entry",
                "PuzzleRoom",
                new Vec3(1f, 0f, 2f)));
            Assert(context.Scenes.TryGetCheckpoint(out var checkpoint) &&
                   checkpoint!.Id == "puzzle-room.entry" &&
                   checkpointCalls == 1 &&
                   context.Logger.Count(CapturedLogLevel.Error) == 1,
                "checkpoint observation isolates failing subscribers and preserves typed current state");
            checkpointLease.Dispose();
            Assert(context.Scenes.ActiveCheckpointSubscriptionCount == 1,
                "checkpoint registrations remain disposable before lifetime cleanup");

            var entity = context.Entities.Create("Terminal", Vec3.Zero);
            var interaction = context.Interactions.Register(
                entity,
                new InteractableDefinition("USE"),
                _ => { });
            Assert(interaction.Succeeded, "interactions are lifetime-owned in the fake context");

            var bundle = context.Assets.LoadBundleAsync("assets/content.bundle").Result.Value!;
            var prefab = context.Assets.LoadPrefabAsync(bundle, "PuzzleCube").Result.Value!;
            Assert(context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity)).Succeeded,
                "asset handles and spawned entities use only SDK-native values");
            Assert(context.Audio.Play(new AudioPlayRequest("ui.confirm")).Succeeded,
                "audio playback is captured");

            var trackedBeforeUi = context.Lifetime.TrackedResourceCount;
            var surface = context.Ui.CreateSurface(new UiSurfaceRequest("status", "Status", "Ready"));
            var accessibility = context.Ui.ApplyAccessibility(
                new UiAccessibilityPreferences(true, 1.25f, true, 0f));
            var modalResult = false;
            var modal = context.Ui.ShowModal(
                new UiModalRequest("Confirm", "Continue?"),
                confirmed => modalResult = confirmed);
            ((FakeUiModal)modal.Value!).Confirm();
            Assert(surface.Succeeded && accessibility.Succeeded
                && context.Ui.Accessibility.HighContrast && context.Ui.Accessibility.ReducedMotion
                && modalResult && context.Ui.ShowToast("Done", UiTone.Success).Succeeded,
                "UI accessibility, surfaces, explicit modal completion, and toasts are captured");
            surface.Value!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == trackedBeforeUi,
                "disposed surfaces and completed modals unregister their lifetime entries immediately");

            var localization = context.Localization.Register(new LocalizationCatalog(
                "en",
                new Dictionary<string, string> { ["ready"] = "Ready" }));
            Assert(localization.Succeeded && context.Localization.Get("ready", "fallback") == "Ready",
                "localization falls back from region to language");
            context.Commands.Register(
                new CommandDefinition("ping", "Checks the mod"),
                _ => OperationResult<string>.Success("pong"));
            Assert(context.Commands.TryExecute("ping", Array.Empty<string>(), out var command) &&
                   command!.Value == "pong",
                "commands execute deterministically");
            context.Diagnostics.Report(new DiagnosticEntry("TEST001", "captured"));
            Assert(context.Diagnostics.GetSnapshot().Count == 1,
                "structured diagnostics are captured");
            Assert(context.Extensions.Register<IProbeExtension>(new ProbeExtension()).Succeeded &&
                   context.Extensions.TryGet<IProbeExtension>(out _),
                "typed extension providers can be registered and resolved");

            context.Dispose();
            context.AssertNoLeaks();
        }

    }
}
