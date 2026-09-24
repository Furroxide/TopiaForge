using System;
using System.Linq;
using System.Reflection;
using TopiaForge.CreatorTools;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SandboxWorkbenchRollbackTests
    {
        public static void Run()
        {
            ReentrantIsolationAcquireCannotAuthorizeChangedSession();
            SameSessionStaleConfirmationCannotReplaceCurrent();
            StaleProjectDeletionCannotDeleteOrClearModal();
            StaleNativeBindingConfirmationCannotClearNewerModal();
            ManualRemovalReportsThrowingTargetRegistration();
            ManualCleanupAggregatesThrowingOwnedDisposers();
            DeadEntriesReportFailuresAndContinue();
            UndoSpawnReportsThrowingTargetRegistration();
            BorrowedStateRestoresAfterEnd();
            ConflictingOutsideChangesSurviveAndAreReported();
            DisappearingTargetReleasesWithoutResurrection();
            RejectedPreviewPreservesPriorEditsForRollback();
            ThrowingRestorationStillReleasesOtherOwners();
            UnavailableIsolationRefusesAllMutations();
            StaleIsolationConfirmationCannotAuthorizeNewSession();
            StaleEndConfirmationCannotEndNewSession();
            ReentrantCleanupCannotRestartOrDuplicateSession();
            TenCyclesReturnEveryOwnershipSnapshotToBaseline();
            FakeRobotEditorEnforcesExclusiveAndEndedLeases();
            FakeObjectiveHandlesReleaseLifetimeEntries();
            TenAcknowledgementRevocationsRestoreLifetimeBaseline();
            Console.WriteLine("SandboxWorkbenchRollbackTests passed (21 offline checks; 10 Sandbox cycles and 10 synthetic isolation revocations).");
        }

        private static void BorrowedStateRestoresAfterEnd()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            Assert(fixture.Target.Transform.Position != fixture.Original.Position
                && fixture.Target.BrainMode == RobotBrainMode.Dormant
                && !ReferenceEquals(fixture.Target.Personality, fixture.Personality)
                && fixture.Editor.ActiveTemporaryPersonalityCount == 1, "previews must change independently observed fixture state");
            Assert(fixture.Workbench.EndSession().Succeeded, "ordinary restoration succeeds");
            fixture.AssertRestored();
        }

        private static void ConflictingOutsideChangesSurviveAndAreReported()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            var externalPosition = new Vec3(90f, 80f, 70f);
            var externalPersona = new RobotPersonalityDraft("Outside writer", "Retain outside state.");
            fixture.Target.Transform = new TransformState(externalPosition, fixture.Target.Transform.Rotation, fixture.Target.Transform.Scale);
            fixture.Target.Personality = externalPersona;
            fixture.Target.BrainMode = RobotBrainMode.Autonomous;
            var result = fixture.Workbench.EndSession();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Conflict, "End Session propagates restore Conflict");
            Assert(fixture.Target.Transform.Position == externalPosition
                && fixture.Target.Transform.Rotation == fixture.Original.Rotation
                && fixture.Target.Transform.Scale == fixture.Original.Scale
                && ReferenceEquals(fixture.Target.Personality, externalPersona)
                && fixture.Target.BrainMode == RobotBrainMode.Autonomous,
                "outside properties survive while uncontested transform components restore");
            Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Warning
                && toast.Message.Contains("conflicting", StringComparison.OrdinalIgnoreCase)), "conflict is visibly reported");
            Assert(fixture.Editor.ActiveLeaseCount == 0 && fixture.Editor.ActiveTemporaryPersonalityCount == 0,
                "conflict still releases temporary resources");
            Assert(fixture.Workbench.EndSession().Value == false, "repeated cleanup is idempotent");
        }

        private static void DisappearingTargetReleasesWithoutResurrection()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            fixture.Target.IsAlive = false;
            var vanishedTransform = fixture.Target.Transform;
            fixture.Context.AdvanceFrame(TimeSpan.Zero);
            Assert(!fixture.Target.IsAlive && fixture.Target.Transform.Position == vanishedTransform.Position
                && fixture.Editor.ActiveLeaseCount == 0 && fixture.Editor.ActiveTemporaryPersonalityCount == 0,
                "retired targets release edits without writing or resurrecting scene objects");
            Assert(fixture.Workbench.IsSessionActive, "a missing borrowed target does not retire unrelated session ownership");
        }

        private static void RejectedPreviewPreservesPriorEditsForRollback()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            var previewPosition = fixture.Target.Transform.Position;
            var previewPersonality = fixture.Target.Personality;
            fixture.Target.PreviewErrorCode = ModErrorCode.External;
            fixture.Click("nudge-up");
            fixture.Click("apply-personality");
            Assert(fixture.Target.Transform.Position == previewPosition && ReferenceEquals(fixture.Target.Personality, previewPersonality),
                "rejected previews do not partially replace prior lease state");
            Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                && toast.Message.Contains("preview failure", StringComparison.Ordinal)), "failed preview is reported");
            Assert(fixture.Workbench.EndSession().Succeeded, "earlier successful edits remain restorable after a failed preview");
            fixture.AssertRestored();
        }

        private static void ThrowingRestorationStillReleasesOtherOwners()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            var second = new FakeRobotEditTarget("second-borrowed", TransformState.Identity) { ThrowOnDispose = true };
            fixture.Editor.AddTarget(second);
            fixture.Workbench.RefreshNativeRoster();
            fixture.Select(second);
            fixture.Click("nudge-up");
            fixture.Target.ThrowOnRestore = true;
            Assert(fixture.Workbench.SpawnRobot().Succeeded, "throwing fixture also owns an unrelated spawned robot");
            var result = fixture.Workbench.EndSession();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.External
                && result.ErrorMessage.Contains("dispose exception", StringComparison.Ordinal)
                && result.ErrorMessage.Contains("restore exception", StringComparison.Ordinal), "cleanup aggregates throwing providers");
            Assert(fixture.Editor.ActiveLeaseCount == 0 && fixture.Editor.ActiveTemporaryPersonalityCount == 0
                && fixture.Content.ActiveSessionCount == 0 && fixture.Robots.Agents.ActiveAgents.Count == 0
                && fixture.Robots.Objectives.ActiveHandleCount == 0 && fixture.Context.LocalPlayer.ActiveControlLeaseCount == 0,
                "all unrelated resources release after earlier cleanup throws");
            Assert(second.Transform.Position == Vec3.Zero, "throwing disposal does not skip its explicit restoration");
            fixture.AssertRestored();
        }

        private static void UnavailableIsolationRefusesAllMutations()
        {
            using var fixture = new Fixture(CreatorProjectScope.Global);
            Assert(fixture.Workbench.Open().Succeeded, "global browsing remains available");
            fixture.Select(fixture.Target);
            fixture.Click("enable-mutations");
            Assert(fixture.Context.Ui.Modals.Count == 0 && fixture.Safety.ActiveLeaseCount == 0,
                "unavailable bridge never offers a success acknowledgement");
            var spawn = fixture.Workbench.SpawnRobot();
            Assert(!spawn.Succeeded && spawn.ErrorCode == ModErrorCode.Unavailable, "global robot spawn refuses without isolation");
            foreach (var id in new[] { "nudge-up", "brain-dormant", "apply-personality" })
            {
                Assert(fixture.Window.TryFindNode(id, out var node) && node is UiButton { Enabled: false },
                    "unavailable mutation controls are disabled");
                // Fault-inject a queued callback past disabled presentation; the operation must still refuse.
                ((UiButton)node!).Activated();
            }
            Assert(fixture.Editor.ActiveLeaseCount == 0 && fixture.Robots.Agents.ActiveAgents.Count == 0,
                "all editor actions remain mutation-free without isolation");
            fixture.AssertRestored();
        }

        private static void StaleIsolationConfirmationCannotAuthorizeNewSession()
        {
            using var fixture = new Fixture(CreatorProjectScope.Global);
            fixture.Safety.SetState(CreatorMutationSafetyState.Ready, "Synthetic bridge only.");
            Assert(fixture.Workbench.Open().Succeeded, "open isolated fake session");
            fixture.Click("enable-mutations");
            var late = CaptureQueuedCallback(fixture.Context.Ui.Modals.Single());
            fixture.Workbench.EndSession();
            fixture.Workbench.Open();
            fixture.Click("enable-mutations");
            var current = fixture.Context.Ui.Modals.Single();
            late(true);
            Assert(fixture.Safety.ActiveLeaseCount == 0 && current.IsOpen,
                "a queued old acknowledgement cannot grant isolation or clear the current confirmation");
            current.Confirm();
            Assert(fixture.Safety.ActiveLeaseCount == 1, "current explicit acknowledgement still works");
        }

        private static void StaleEndConfirmationCannotEndNewSession()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            fixture.Click("end-session");
            var late = CaptureQueuedCallback(fixture.Context.Ui.Modals.Single());
            fixture.Workbench.EndSession();
            fixture.Workbench.Open();
            fixture.Workbench.SpawnRobot();
            late(true);
            Assert(fixture.Workbench.IsSessionActive && fixture.Robots.Agents.ActiveAgents.Count == 1,
                "an old End Session confirmation cannot remove replacement-session content");
            fixture.Click("end-session");
            fixture.Context.Ui.Modals.Single().Confirm();
            Assert(!fixture.Workbench.IsSessionActive, "the current End Session confirmation remains usable");
        }

        private static void ReentrantCleanupCannotRestartOrDuplicateSession()
        {
            using var fixture = new Fixture();
            fixture.OpenAndEdit();
            fixture.OnHide = () =>
            {
                Assert(fixture.Workbench.EndSession().Value == false, "nested cleanup is bounded");
                Assert(!fixture.Workbench.Open().Succeeded && !fixture.Workbench.SpawnRobot().Succeeded,
                    "cleanup callbacks cannot acquire replacement session ownership");
            };
            Assert(fixture.Workbench.EndSession().Succeeded && fixture.Content.ActiveSessionCount == 0,
                "reentrant hide callback still completes outer restoration once");
            fixture.AssertRestored();
        }

        private static void TenCyclesReturnEveryOwnershipSnapshotToBaseline()
        {
            using var fixture = new Fixture();
            // Establish retained host surfaces once. End Session intentionally keeps the reusable window/HUD.
            fixture.Workbench.Open();
            fixture.Workbench.EndSession();
            var baseline = fixture.Snapshot();
            for (var cycle = 0; cycle < 10; cycle++)
            {
                fixture.OpenAndEdit();
                Assert(fixture.Workbench.SpawnRobot().Succeeded, "cycle " + cycle + " owns a manual robot");
                fixture.Workbench.Hide();
                Assert(fixture.Editor.ActiveLeaseCount == 1 && fixture.Content.ActiveSessionCount == 1
                    && fixture.Context.LocalPlayer.ActiveControlLeaseCount == 0, "hide retains edits/session but releases controls");
                fixture.Workbench.Open();
                Assert(fixture.Content.ActiveSessionCount == 1 && fixture.Context.LocalPlayer.ActiveControlLeaseCount == 1,
                    "reopen reuses the session and reacquires exactly one control lease");
                Assert(fixture.Workbench.EndSession().Succeeded, "cycle " + cycle + " ends cleanly");
                fixture.AssertRestored();
                Assert(fixture.Snapshot() == baseline, "cycle " + cycle + " must restore every ownership counter immediately");
                fixture.Context.AdvanceFrame(TimeSpan.Zero);
                Assert(fixture.Snapshot() == baseline, "cycle " + cycle + " stays restored after the next deterministic update barrier");
            }
        }

        private static void FakeRobotEditorEnforcesExclusiveAndEndedLeases()
        {
            using var fixture = new Fixture();
            var lease = fixture.Editor.BeginTemporaryEdit(fixture.Target).Value!;
            Assert(fixture.Editor.BeginTemporaryEdit(fixture.Target).ErrorCode == ModErrorCode.Conflict,
                "the fake refuses overlapping ownership of one target");
            lease.PreviewBrainMode(RobotBrainMode.Dormant);
            Assert(lease.Restore().Succeeded && !lease.IsActive && lease.Restore().Value == false,
                "restoration terminates ownership exactly once");
            Assert(lease.PreviewBrainMode(RobotBrainMode.Dormant).ErrorCode == ModErrorCode.InvalidState,
                "late lease calls cannot change restored state");
            fixture.AssertRestored();
        }

        private static void FakeObjectiveHandlesReleaseLifetimeEntries()
        {
            using var fixture = new Fixture();
            var baseline = fixture.Context.Lifetime.TrackedResourceCount;
            var target = fixture.Robots.Objectives.RegisterTarget("fixture-target", RobotTargetKind.Player,
                () => new RobotTargetSnapshot(Vec3.Zero)).Value!;
            var robot = fixture.Robots.Agents.Spawn(new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            var objective = fixture.Robots.Objectives.SetObjective(robot, RobotObjective.Idle()).Value!;
            objective.Dispose();
            target.Dispose();
            robot.Despawn();
            Assert(fixture.Robots.Objectives.ActiveHandleCount == 0 && fixture.Context.Lifetime.TrackedResourceCount == baseline,
                "explicit target/objective disposal removes lifetime registrations before context shutdown");
        }

        private static void TenAcknowledgementRevocationsRestoreLifetimeBaseline()
        {
            using var fixture = new Fixture(CreatorProjectScope.Global);
            fixture.Workbench.Open();
            fixture.Workbench.EndSession();
            var baseline = fixture.Snapshot();
            for (var cycle = 0; cycle < 10; cycle++)
            {
                fixture.Safety.SetState(CreatorMutationSafetyState.Ready, "Synthetic isolation fixture only.");
                fixture.Workbench.Open();
                fixture.Click("enable-mutations");
                fixture.Context.Ui.Modals.Single().Confirm();
                Assert(fixture.Safety.ActiveLeaseCount == 1, "acknowledgement acquires one synthetic lease");
                fixture.OpenAndEdit();
                Assert(fixture.Workbench.SpawnRobot().Succeeded, "admitted fake isolation permits test mutation");
                fixture.Safety.SetState(CreatorMutationSafetyState.Unavailable, "Synthetic lease revoked.");
                fixture.Context.AdvanceFrame(TimeSpan.Zero);
                Assert(!fixture.Workbench.IsSessionActive && fixture.Snapshot() == baseline,
                    "revocation cycle " + cycle + " releases all resources before context shutdown");
                fixture.AssertRestored();
            }
        }

        private static Action<bool> CaptureQueuedCallback(FakeUiModal modal) =>
            (Action<bool>)(typeof(FakeUiModal).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(modal)
                ?? throw new InvalidOperationException("Modal callback was not captured."));

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Sandbox offline rollback: " + message);
        }

        private sealed class Fixture : IDisposable
        {
            public FakeModContext Context { get; } = new FakeModContext();
            public FakeCreatorContentService Content { get; }
            public FakeRobotKit Robots { get; }
            public FakeRobotSceneEditorService Editor { get; }
            public FakeCreatorMutationSafetyService Safety { get; }
            public FakeRobotEditTarget Target { get; }
            public CreatorWorkbench Workbench { get; }
            public TransformState Original { get; } = new TransformState(new Vec3(2f, 3f, 4f), Quat.Identity, new Vec3(2f, 2f, 2f));
            public RobotPersonalityDraft Personality { get; } = new RobotPersonalityDraft("Original", "Original fixture personality.", 0.25f);
            public Action? OnHide { get; set; }
            private readonly IDisposable[] registrations;

            public Fixture(CreatorProjectScope scope = CreatorProjectScope.Sandbox,
                ICreatorMutationSafetyService? mutationSafety = null, ICreatorProjectLibrary? library = null)
            {
                Context.Scenes.Load("RobotopiaCity");
                Context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
                Content = new FakeCreatorContentService(Context.Lifetime);
                Robots = new FakeRobotKit(Context.Lifetime);
                Editor = new FakeRobotSceneEditorService(Context.Lifetime);
                Safety = new FakeCreatorMutationSafetyService(Context.Lifetime);
                Target = new FakeRobotEditTarget("borrowed-fixture", Original) { Personality = Personality };
                Editor.AddTarget(Target);
                registrations = new[] {
                    Context.Extensions.Register<IRobotSceneEditorService>(Editor).Value!,
                    Context.Extensions.Register<IRobotObjectiveService>(Robots.Objectives).Value!,
                    Context.Extensions.Register<ICreatorMutationSafetyService>(mutationSafety ?? Safety).Value! };
                if (library != null) registrations = registrations.Concat(new[] { Context.Extensions.Register<ICreatorProjectLibrary>(library).Value! }).ToArray();
                Workbench = new CreatorWorkbench(Context,
                    new CreatorWorkbenchOptions("sandbox-rollback", "OFFLINE SANDBOX", scope, 16, true, false, 4, 0f),
                    Content, Robots.Agents, () => OnHide?.Invoke(), () => Workbench!.EndSession());
            }
            public FakeUiSurface Window => Context.Ui.Surfaces.Single(surface => surface.Id == "sandbox-rollback-window");
            public void Click(string id) => Assert(Window.ActivateButton(id).Succeeded && Window.CallbackErrors.Count == 0,
                "real workbench declarative callback " + id + " completes");
            public void Select(FakeRobotEditTarget target) => Assert(Window.SelectListItem("roster-list", "robot-native:" + target.Id).Succeeded,
                "borrowed target is independently present in the roster");
            public void OpenAndEdit()
            {
                Assert(Workbench.Open().Succeeded, "workbench opens");
                Select(Target);
                Assert(Window.ChangeText("rotation-y", "1").Succeeded && Window.ChangeText("rotation-w", "0").Succeeded
                    && Window.ChangeText("scale-x", "3").Succeeded && Window.ChangeText("scale-y", "4").Succeeded
                    && Window.ChangeText("scale-z", "5").Succeeded, "editable rotation/scale fields accept deterministic fixture values");
                Click("apply-transform");
                Click("nudge-up");
                Click("brain-dormant");
                Click("apply-personality");
            }
            public void AssertRestored() => Assert(Target.Transform.Position == Original.Position
                && Target.Transform.Rotation == Original.Rotation && Target.Transform.Scale == Original.Scale
                && Target.BrainMode == RobotBrainMode.Autonomous && ReferenceEquals(Target.Personality, Personality)
                && Editor.ActiveLeaseCount == 0 && Editor.ActiveTemporaryPersonalityCount == 0,
                "original transform, brain and personality identity restore without retained edit resources");
            public string Snapshot() => string.Join(",", Context.Lifetime.TrackedResourceCount, Context.Events.ActiveSubscriptionCount,
                Context.Input.ActiveActionCount, Context.LocalPlayer.ActiveControlLeaseCount, Context.Entities.ActiveMotionCount,
                Context.Scheduler.PendingCount, Context.Scenes.PendingLoadCount, Context.Scenes.ActiveCheckpointSubscriptionCount,
                Context.Interactions.ActiveRegistrationCount, Context.Assets.ActiveBundleCount, Context.Assets.ActivePrefabCount,
                Context.Assets.ActiveSpawnCount, Context.Audio.ActivePlaybacks.Count, Context.Ui.Surfaces.Count, Context.Ui.Modals.Count,
                Context.Extensions.ActiveProviderCount, Context.Localization.ActiveCatalogCount, Context.Commands.ActiveCommandCount,
                Content.ActiveSessionCount, Content.ActiveSpawnCount, Content.ActiveEditCount,
                Robots.Agents.ActiveAgents.Count, Robots.Objectives.ActiveHandleCount, Robots.Conversations.ActiveConversationCount,
                Editor.ActiveLeaseCount, Editor.ActiveTemporaryPersonalityCount, Safety.ActiveLeaseCount);
            public void Dispose()
            {
                OnHide = null;
                Workbench.Dispose();
                Content.Dispose();
                foreach (var registration in registrations) registration.Dispose();
                Context.Dispose();
                Context.AssertNoLeaks();
            }
        }
    }
}
