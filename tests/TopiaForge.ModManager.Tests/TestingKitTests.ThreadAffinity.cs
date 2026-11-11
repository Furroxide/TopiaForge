using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestEngineFacingThreadAffinity()
        {
            using var context = FakeModContext.CreateWithThreadChecks();
            IEntityService entities = context.Entities;
            IUiService ui = context.Ui;
            var entity = context.Entities.Create("Cube", Vec3.Zero);
            var moved = new TransformState(new Vec3(1, 2, 3), Quat.Identity, new Vec3(1, 1, 1));
            var query = new EntityQuery();
            var surface = new UiSurfaceRequest("thread-test", "Thread test", "");
            var modal = new UiModalRequest("Thread test", "Confirm the action.");
            var preferences = new UiAccessibilityPreferences(highContrast: true);
            var appliedOnOwner = false;
            var ownerThread = Thread.CurrentThread.ManagedThreadId;

            RunOnWorker(() =>
            {
                AssertThreadViolation(() => entities.TryGetTransform(entity, out _), nameof(IEntityService.TryGetTransform));
                AssertThreadViolation(() => entities.SetTransform(entity, moved), nameof(IEntityService.SetTransform));
                AssertThreadViolation(() => entities.Query(query), nameof(IEntityService.Query));
                AssertThreadViolation(() => entities.AcquireMotion(entity), nameof(IEntityService.AcquireMotion));
                AssertThreadViolation(() => entities.Destroy(entity), nameof(IEntityService.Destroy));
                AssertThreadViolation(() => ui.ApplyAccessibility(preferences), nameof(IUiService.ApplyAccessibility));
                AssertThreadViolation(() => ui.CreateSurface(surface), nameof(IUiService.CreateSurface));
                AssertThreadViolation(() => ui.ShowModal(modal, _ => { }), nameof(IUiService.ShowModal));
                AssertThreadViolation(() => ui.ShowToast("Worker"), nameof(IUiService.ShowToast));
                AssertThreadViolation(() => _ = ui.ClockTime, nameof(IUiService.ClockTime));
                AssertThreadViolation(() => _ = ui.Labels, nameof(IUiService.Labels));

                using var independent = FakeModContext.CreateWithThreadChecks();
                var ownEntity = independent.Entities.Create("Worker cube", Vec3.Zero);
                Assert(independent.Entities.TryGetTransform(ownEntity, out _) && independent.Ui.ShowToast("Worker owner").Succeeded,
                    "a separately constructed context can use its own thread even while another context is active");

                Assert(context.Scheduler.NextFrame(() =>
                {
                    appliedOnOwner = Thread.CurrentThread.ManagedThreadId == ownerThread
                        && entities.SetTransform(entity, moved).Succeeded
                        && ui.ShowToast("Queued").Succeeded;
                }).Succeeded, "a worker can queue next-frame work");
            });

            Assert(entities.TryGetTransform(entity, out var original) && original.Position == Vec3.Zero
                && context.Entities.ActiveMotionCount == 0 && context.Ui.Surfaces.Count == 0 && context.Ui.Modals.Count == 0
                && context.Ui.Toasts.Count == 0 && !ui.Accessibility.HighContrast && !appliedOnOwner,
                "worker calls leave entity and UI state unchanged before the owner advances a frame");
            context.AdvanceFrame(TimeSpan.Zero);
            Assert(appliedOnOwner && entities.TryGetTransform(entity, out var updated) && updated.Equals(moved)
                && context.Ui.Toasts.Count == 1 && context.Ui.Toasts[0].Message == "Queued",
                "next-frame work can use entity and UI services on the owner thread");

            context.Dispose();
            RunOnWorker(() => Assert(!entities.TryGetTransform(entity, out var stopped) && stopped.Equals(TransformState.Identity),
                "a stopped lifetime returns no transform before entering the thread guard"));
        }

        private static void TestPermissiveFakeThreadAccess()
        {
            using var context = new FakeModContext();
            var entity = context.Entities.Create("Default cube", Vec3.Zero);
            var moved = new TransformState(new Vec3(3, 2, 1), Quat.Identity, new Vec3(1, 1, 1));
            using var standaloneLifetime = new FakeModLifetime();
            var standaloneEntities = new FakeEntityService(standaloneLifetime);
            var standaloneEntity = standaloneEntities.Create("Standalone cube", Vec3.Zero);
            var standaloneUi = new FakeUiService(standaloneLifetime);
            var clockUi = new FakeUiService(standaloneLifetime, context.Time);
            RunOnWorker(() =>
            {
                Assert(context.Entities.TryGetTransform(entity, out _) && context.Entities.SetTransform(entity, moved).Succeeded
                    && context.Entities.Query(new EntityQuery()).Count == 1, "default context entity access remains permissive");
                var motion = context.Entities.AcquireMotion(entity);
                Assert(motion.Succeeded, "default motion acquisition remains permissive");
                motion.Value!.Dispose();
                var disposable = context.Entities.Create("Temporary cube", Vec3.Zero);
                Assert(context.Entities.Destroy(disposable).Succeeded, "default entity destruction remains permissive");
                Assert(context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true)).Succeeded
                    && context.Ui.CreateSurface(new UiSurfaceRequest("default-worker", "Default worker", "")).Succeeded
                    && context.Ui.ShowModal(new UiModalRequest("Default worker", "Continue?"), _ => { }).Succeeded
                    && context.Ui.ShowToast("Default worker").Succeeded,
                    "default context UI entry points remain permissive");
                _ = context.Ui.ClockTime;
                _ = context.Ui.Labels;
                Assert(standaloneEntities.TryGetTransform(standaloneEntity, out _)
                    && standaloneEntities.SetTransform(standaloneEntity, moved).Succeeded
                    && standaloneUi.ShowToast("Standalone worker").Succeeded && clockUi.ShowToast("Clock worker").Succeeded,
                    "existing standalone entity and both UI constructors remain permissive");
                _ = standaloneUi.ClockTime;
                _ = standaloneUi.Labels;
                _ = clockUi.ClockTime;
                _ = clockUi.Labels;
            });
            context.Dispose();
            RunOnWorker(() => Assert(context.Entities.TryGetTransform(entity, out var retained) && retained.Equals(moved),
                "default transform reads preserve their existing behavior after the fake lifetime stops"));
            context.AssertNoLeaks();
        }

        private static void RunOnWorker(Action action)
        {
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = error; }
            });
            worker.Start();
            worker.Join();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void AssertThreadViolation(Action action, string operation)
        {
            try { action(); }
            catch (InvalidOperationException error) when (
                error.Message.StartsWith("TFSDK100: " + operation + " ", StringComparison.Ordinal)
                && error.Message.Contains("Context.Scheduler.NextFrame", StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(operation + " should report TFSDK100 when called from a worker thread.");
        }
    }
}
