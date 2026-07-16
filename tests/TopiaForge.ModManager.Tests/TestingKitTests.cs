using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        public static void Run()
        {
            TestInMemoryAuthoringServices();
            TestDeterministicGameplayServices();
            TestImmediateLifetimeReleaseForZombieResources();
            TestSchedulerAndControlledCompletion();
            TestExpectedCancellationResults();
            TestBundleWorldContentOwnership();
            TestSpecialistModuleFakes();
            TestCompleteCoreServiceContext();
            TestDeclarativeUiComposition();
            TestLifecycleAndLeaks();
            TestPartialLoadFailureCleanup();
            TestEveryOwnedResourceAcrossTenReloads();
            TestEveryOwnedResourceAfterPartialLoadFailure();
            Console.WriteLine("TestingKitTests passed.");
        }

        private static void TestInMemoryAuthoringServices()
        {
            using var context = new FakeModContext();
            var definition = new ConfigDefinition<ProbeConfig>(
                2,
                () => new ProbeConfig { Value = 4 },
                value => value.Value >= 0
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "Value cannot be negative."),
                (_, value) => OperationResult<ProbeConfig>.Success(
                    new ProbeConfig { Value = value.Value + 1 }));
            var defaults = context.Config.Load(definition);
            Assert(defaults.Succeeded && defaults.Value!.Value == 4,
                "config creates and validates defaults before a value is saved");
            var saved = new ProbeConfig { Value = 9 };
            Assert(context.Config.Save(definition, saved).Succeeded &&
                   ReferenceEquals(context.Config.Load(definition).Value, saved),
                "config validates and returns the stored typed value");
            context.Config.Seed(1, new ProbeConfig { Value = 2 });
            Assert(context.Config.Load(definition).Value!.Value == 3,
                "config migration is controlled with an explicit stored schema");

            Assert(context.Storage.Save("progress", saved).Succeeded &&
                   context.Storage.Load<ProbeConfig>("progress").Value!.Value == 9,
                "typed storage round-trips values without touching disk");
            Assert(context.Storage.SetStoryFlag("chapter-one/terminal-opened", false).Succeeded &&
                   context.Storage.TryGetStoryFlag("chapter-one/terminal-opened", out var storyFlag) &&
                   !storyFlag,
                "mod-owned story flags preserve an explicitly stored false value");
            Assert(context.Storage.DeleteStoryFlag("chapter-one/terminal-opened").Succeeded &&
                   !context.Storage.TryGetStoryFlag("chapter-one/terminal-opened", out _),
                "mod-owned story flags can be removed without exposing save paths");
            Assert(context.Files.WriteDataTextAsync("nested/save.txt", "ready").Result.Succeeded &&
                   context.Files.ReadDataTextAsync("nested/save.txt").Result.Value == "ready",
                "content-based data files round-trip without revealing paths");
            context.FileSystem.SetPackageText("content/story.txt", "chapter one");
            Assert(context.Files.ReadPackageTextAsync("content/story.txt").Result.Value == "chapter one",
                "tests can seed package content directly");
            AssertThrows<ArgumentException>(() => context.Files.PackageFileExists("../escape.dll"),
                "fake content operations reject traversal");

            context.Logger.Info("started");
            context.Logger.Error(new InvalidOperationException("boom"), "failed");
            Assert(context.Logger.Entries.Count == 2 &&
                   context.Logger.Count(CapturedLogLevel.Error) == 1 &&
                   context.Logger.Entries[1].Exception is InvalidOperationException,
                "captured logger retains severity, order, and exceptions");
        }

        private static void TestDeterministicGameplayServices()
        {
            using var context = new FakeModContext();
            var inputResult = context.Input.RegisterAction(new InputActionDefinition(
                "activate",
                "Activate",
                new[] { InputBinding.Key("F") }));
            Assert(inputResult.TryGetValue(out var inputHandle) && inputHandle is FakeInputAction,
                "input registration returns a typed successful operation result");
            var input = (FakeInputAction)inputHandle!;
            var pressedDuringFrame = false;
            context.Events.SubscribeUpdate(_ => pressedDuringFrame = input.WasPressed);
            context.Input.SetValue("activate", 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            Assert(pressedDuringFrame && input.IsHeld && !input.WasPressed,
                "input edges remain visible during a frame and clear afterwards");

            context.Input.IsUiFocused = true;
            Assert(!input.IsHeld && input.Value == 0f,
                "UI focus suppresses actions that opt into suppression");

            var alternateResult = context.Input.RegisterAction(new InputActionDefinition(
                "alternate",
                "Alternate",
                new[] { InputBinding.Key("F"), InputBinding.GamepadButton(InputGamepadButton.West) }));
            Assert(alternateResult.TryGetValue(out var alternate),
                "a second uniquely named input action registers successfully");
            Assert(context.Input.GetConflicts().Count == 2,
                "input conflicts report the shared physical control for both affected actions");
            Assert(alternate!.Rebind(new[] { InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger) }).Succeeded &&
                   context.Input.GetConflicts().Count == 0,
                "runtime rebinding resolves conflicts without changing the action identity");
            Assert(alternate.ResetBindings().Succeeded && alternate.Bindings.Count == 2,
                "actions restore keyboard, mouse, or gamepad defaults deterministically");
            var duplicate = context.Input.RegisterAction(new InputActionDefinition(
                "alternate",
                "Duplicate alternate",
                new[] { InputBinding.Key("G") }));
            Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "duplicate input names return a stable conflict instead of throwing");

            var snapshot = new PlayerSnapshot(
                new Vec3(1f, 2f, 3f),
                new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            context.Player.Snapshot = snapshot;
            Assert(context.Player.TryGetSnapshot(out var observed) && ReferenceEquals(observed, snapshot),
                "player snapshots are set explicitly");
            context.Player.Health = new PlayerHealthSnapshot(75f, 100f);
            Assert(context.Player.Damage(new PlayerDamageRequest(30f, "test.hazard")).Value!.Current == 45f &&
                   context.Player.Heal(10f, "test.reward").Value!.Current == 55f,
                "player health damage and healing are deterministic and bounded");
            var trackedBeforeControl = context.Lifetime.TrackedResourceCount;
            var control = context.Player.AcquireControl("testing");
            Assert(control.Succeeded && context.Player.ActiveControlLeaseCount == 1,
                "player-control leases are observable");
            control.Value!.Dispose();
            Assert(context.Player.ActiveControlLeaseCount == 0
                && context.Lifetime.TrackedResourceCount == trackedBeforeControl,
                "disposing player control also unregisters its lifetime ownership entry immediately");

            var entity = context.Entities.Create("Cube", new Vec3(0f, 0f, 2f));
            context.Physics.RaycastHit = new PhysicsHit(
                entity,
                entity.Position,
                new Vec3(0f, 1f, 0f),
                2f);
            Assert(context.Physics.TryRaycast(snapshot.AimRay, 5f, out var hit) && ReferenceEquals(hit!.Entity, entity),
                "physics returns the configured live hit");
            context.Physics.SphereCastHit = context.Physics.RaycastHit;
            context.Physics.OverlapEntities.Add(entity);
            Assert(context.Physics.TrySphereCast(snapshot.AimRay, 0.5f, 5f, out var sphereHit) &&
                   ReferenceEquals(sphereHit!.Entity, entity) &&
                   context.Physics.Overlap(new Bounds(entity.Position, new Vec3(2f, 2f, 2f))).Count == 1,
                "shape casts and bounded overlap queries use opaque live entities");
            var transformed = new TransformState(
                new Vec3(1f, 2f, 3f),
                new Quat(0f, 0f, 0f, 1f),
                new Vec3(2f, 2f, 2f));
            Assert(context.Entities.SetTransform(entity, transformed).Succeeded &&
                   context.Entities.TryGetTransform(entity, out var readTransform) &&
                   readTransform.Equals(transformed) &&
                   context.Entities.Query(new EntityQuery(
                       center: transformed.Position,
                       radius: 1f,
                       nameContains: "cub",
                       maximumResults: 1)).Count == 1,
                "entity transform and bounded query APIs require no engine handles");
            var acquired = context.Entities.AcquireMotion(entity);
            Assert(acquired.Succeeded, "entity motion can be acquired deterministically");
            var motion = acquired.Value!;
            var moved = motion.MoveToward(new Vec3(0f, 0f, 4f), 10f, 10f, 20f, 0.1f);
            Assert(moved.Succeeded && context.Entities.ActiveMotionCount == 1,
                "motion operations update fake entity state");
            motion.Throw(new Vec3(0f, 0f, 1f), 8f);
            Assert(context.Entities.ActiveMotionCount == 0,
                "throwing releases exclusive motion ownership");
            Assert(context.Entities.Destroy(entity).Succeeded && !entity.IsAlive,
                "owned entities can be destroyed through the safe entity service");
        }

        private static void TestSchedulerAndControlledCompletion()
        {
            using var context = new FakeModContext();
            var order = new List<string>();
            Assert(context.Scheduler.NextFrame(() => order.Add("frame")).Succeeded,
                "next-frame work returns a successful operation result");
            Assert(context.Scheduler.After(TimeSpan.FromSeconds(2), () => order.Add("after")).Succeeded,
                "delayed work returns a successful operation result");
            var delay = context.Scheduler.DelayAsync(TimeSpan.FromSeconds(2));
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert(string.Join(",", order) == "frame" && !delay.IsCompleted,
                "virtual time does not complete future work early");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert(string.Join(",", order) == "frame,after" && delay.IsCompletedSuccessfully &&
                   delay.Result.Succeeded,
                "virtual time completes same-deadline work in registration order");

            using var controlled = new ControlledOperation<string>(context.Lifetime.StoppingToken);
            Assert(!controlled.Task.IsCompleted, "controlled operations begin pending");
            Assert(controlled.Fail(ModErrorCode.Unavailable, "offline") &&
                   controlled.Task.Result.ErrorCode == ModErrorCode.Unavailable,
                "controlled operations expose expected failure completion");

            var cancelled = context.Scheduler.DelayAsync(TimeSpan.FromDays(1), CancellationToken.None);
            context.Dispose();
            Assert(cancelled.IsCompletedSuccessfully &&
                   cancelled.Result.ErrorCode == ModErrorCode.Cancelled &&
                   context.Scheduler.PendingCount == 0,
                "lifetime shutdown completes pending delays as stable cancellation failures without leaks");
            var rejectedSchedule = context.Scheduler.NextFrame(() => { });
            var rejectedInput = context.Input.RegisterAction(new InputActionDefinition(
                "after-stop",
                "After stop",
                new[] { InputBinding.Key("F11") }));
            var rejectedLocalization = context.Localization.Register(new LocalizationCatalog(
                "en",
                new Dictionary<string, string> { ["after-stop"] = "After stop" }));
            Assert(!rejectedSchedule.Succeeded && rejectedSchedule.ErrorCode == ModErrorCode.Cancelled &&
                   !rejectedInput.Succeeded && rejectedInput.ErrorCode == ModErrorCode.Cancelled &&
                   !rejectedLocalization.Succeeded && rejectedLocalization.ErrorCode == ModErrorCode.Cancelled,
                "post-stop registrations return stable cancellation failures instead of throwing");
        }

    }
}
