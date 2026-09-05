using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class GamemodeSessionOrchestratorTests
    {
        internal static void Run(string root)
        {
            TestMenuCancellationFailure(root + "-menu-cancel-failure");
            TestDelayedSceneOwnership(root + "-delayed-scene");
            TestCapturedReadiness(root + "-captured-readiness");
            TestShutdownCancelsMainMenu(root + "-shutdown-menu");
            TestShutdownAdmission(root + "-shutdown-admission");
            TestRunningAndStaleHandles(root + "-running");
            TestOversizedStartupError(root + "-oversized");
            TestCancellationAndLateResults(root + "-cancel");
            TestFailures(root + "-failures");
            TestSelfStop(root + "-self-stop");
            TestScenePolicy(root + "-scene-policy");
            TestNativeDrain(root + "-native");
            TestNotificationStop(root + "-notification");
            TestReentrantAdmission(root + "-reentrant");
            TestPreparingAndOwnerCancellation(root + "-owners");
            Console.WriteLine("GamemodeSessionOrchestratorTests passed.");
        }

        private static void TestRunningAndStaleHandles(string root)
        {
            using var test = new SessionFixture(root);
            var phases = new List<SessionPhase>();
            test.Hosted.StateChanged += state => phases.Add(state.Phase);
            test.Hosted.StateChanged += _ => throw new InvalidOperationException("observer failure");
            test.Hosted.Outcome += _ => throw new InvalidOperationException("outcome observer failure");
            var diagnostics = 0; test.Hosted.DiagnosticFailure += _ => diagnostics++;
            test.Launch();
            var old = SessionFixture.ModeSession!;
            Assert(ReferenceEquals(old.Lifetime, old.Context.Lifetime), "Session and Context must share their owner scope lifetime");
            Assert(test.Parent.ActiveChildScopeCount == 1 && old.Context.Identity.Id == test.Parent.Identity.Id,
                "same-package participants share one scope with exact package identity");
            Assert(phases.SequenceEqual(new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Running }),
                "launch commits all four startup states before succeeding");
            Assert(old.World.Scene.InstanceId == -12, "negative native scene identities are opaque and valid");
            var restarted = old.RestartAsync();
            test.Wait(restarted);
            Assert(restarted.Result.Succeeded && SessionFixture.ModeSession!.SessionId != old.SessionId, "restart resolves and owns a fresh session");
            var stale = old.StopAsync(); test.Wait(stale);
            Assert(!stale.Result.Succeeded && test.Hosted.Current.Phase == SessionPhase.Running, "stale stop cannot affect successor");
            // The environment snapshots immutable package state; a stale request must still revalidate a changed fresh snapshot.
            test.InvalidBindings = true;
            var blocked = SessionFixture.ModeSession!.RestartAsync(); test.Wait(blocked);
            Assert(!blocked.Result.Succeeded && test.Hosted.Current.Phase == SessionPhase.Running,
                "revalidation failure must preserve the running session");
            test.InvalidBindings = false;
            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync(); test.Wait(menu);
            Assert(menu.Result.Succeeded && test.Hosted.Current.Phase == SessionPhase.Idle && test.MenuLoads == 1,
                "main-menu succeeds only after cleanup and its native operation");
            Assert(test.Outcomes.Count(value => value.Kind == "session") == 2, "each replaced/stopped session has exactly one terminal outcome");
            Assert(diagnostics > 0, "notification failures are isolated and reported");
            Assert(test.Parent.ActiveChildScopeCount == 0, "all scopes released after menu");
        }

        private static void TestCancellationAndLateResults(string root)
        {
            foreach (var inWorld in new[] { true, false })
            {
                using var test = new SessionFixture(root + inWorld);
                using var cancellation = new CancellationTokenSource();
                var world = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var mode = new TaskCompletionSource<OperationResult<IGamemodeController>>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (inWorld) SessionFixture.Load = (_, _) => world.Task;
                else SessionFixture.Start = (_, _) => mode.Task;
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "cancel-request", cancellation.Token);
                test.Until(() => test.Hosted.Current.Phase == (inWorld ? SessionPhase.LoadingWorld : SessionPhase.StartingMode));
                cancellation.Cancel(); test.Wait(launch);
                Assert(launch.Result.ErrorCode == ModErrorCode.Cancelled && test.Hosted.Current.Phase == SessionPhase.Stopping,
                    "caller cancellation settles launch but keeps session stopping while callback drains");
                var competing = test.Hosted.StartAsync(test.Plan.Descriptor, "competing"); test.Wait(competing);
                Assert(competing.Result.ErrorCode == ModErrorCode.Conflict, "competing startup stays Busy during callback drain");
                Assert(test.Outcomes.All(value => value.Kind != "session"), "terminal outcome cannot precede late resource cleanup");
                var disposed = 0;
                if (inWorld) world.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed++)));
                else mode.SetResult(OperationResult<IGamemodeController>.Success(new SessionTestController(() => disposed++)));
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                Assert(disposed == 1 && test.Parent.ActiveChildScopeCount == 0, "late returned owned results are disposed exactly once before Idle");
                Assert(test.Outcomes.Count(value => value.Kind == "session") == 1, "cancelled session has one terminal outcome");
                Assert(test.Outcomes.Single(value => value.Kind == "session").Status == "cancelled",
                    "startup cancelled before Running must report cancelled terminal status after cleanup");
                test.Launch("successor");
                test.Host.Drain();
                Assert(test.Hosted.Current.Phase == SessionPhase.Running, "late callbacks cannot stop a newer session");
            }
        }

        private static void TestFailures(string root)
        {
            foreach (var phase in new[] { "provider-constructor", "provider-load", "factory-constructor", "factory-start", "cleanup" })
            {
                using var test = new SessionFixture(root + phase);
                var released = 0;
                Action fail = () =>
                {
                    SessionFixture.ChildLifetime!.Defer(() => released++);
                    throw new InvalidOperationException("allocation then " + phase);
                };
                if (phase == "provider-constructor") SessionFixture.ProviderConstructor = fail;
                if (phase == "factory-constructor") SessionFixture.FactoryConstructor = fail;
                if (phase == "provider-load") SessionFixture.Load = (_, _) => { fail(); throw new Exception(); };
                if (phase == "factory-start") SessionFixture.Start = (_, _) => { fail(); throw new Exception(); };
                if (phase == "cleanup")
                {
                    SessionFixture.Start = (session, _) =>
                    {
                        session.Lifetime.Defer(() => { released++; throw new InvalidOperationException("scope cleanup"); });
                        return Task.FromResult(OperationResult<IGamemodeController>.Success(
                            new SessionTestController(() => { released++; throw new InvalidOperationException("controller cleanup"); })));
                    };
                    test.Launch();
                    var stop = test.Hosted.ShutdownAsync(); test.Wait(stop);
                    Assert(!stop.Result.Succeeded && released == 2, "throwing disposal aggregates failures and attempts all disposers");
                }
                else
                {
                    var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "failure"); test.Wait(launch);
                    test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                    Assert(!launch.Result.Succeeded && released == 1, "constructor/start allocation is owned before failure");
                }
                Assert(test.Parent.ActiveChildScopeCount == 0 && !test.Native.IsSceneBusy, "failed startup releases scopes and native ownership");
                Assert(test.Outcomes.Count(value => value.Kind == "session") == 1, "failed session publishes one terminal outcome");
            }
        }

        private static void TestSelfStop(string root)
        {
            using var test = new SessionFixture(root);
            var disposed = 0;
            SessionFixture.Start = async (session, _) =>
            {
                var accepted = await session.StopAsync();
                Assert(accepted.Succeeded, "self-stop must acknowledge acceptance without awaiting its own callback");
                return OperationResult<IGamemodeController>.Success(new SessionTestController(() => disposed++));
            };
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "self-stop"); test.Wait(launch);
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(!launch.Result.Succeeded && disposed == 1, "controller returned after self-stop must be cleaned before terminal");
        }

        private static void TestScenePolicy(string root)
        {
            foreach (var ends in new[] { false, true })
            {
                using var test = new SessionFixture(root + ends, ends);
                test.Launch();
                var session = SessionFixture.ModeSession!;
                test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-12, "World", SceneLifecyclePhase.Activated, SceneLoadMode.Single, true));
                test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(100, "Background", SceneLifecyclePhase.Loaded, SceneLoadMode.Additive, false));
                test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(101, "Snapshot", SceneLifecyclePhase.Activated, SceneLoadMode.Single, true, true));
                test.Host.Drain();
                Assert(test.Hosted.Current.Phase == SessionPhase.Running, "duplicate, additive background and initial events do not end gameplay");
                test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-20, "Replacement", SceneLifecyclePhase.Activated, SceneLoadMode.Single, true));
                if (ends) test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                else { test.Host.Drain(); Assert(ReferenceEquals(session, SessionFixture.ModeSession), "keep-controller never reconstructs a controller"); }
            }
        }

        private static void TestNativeDrain(string root)
        {
            using var test = new SessionFixture(root);
            var native = new ControlledDispatch();
            SessionFixture.Load = (context, token) =>
            {
                var admitted = SessionFixture.Slot!.Borrowed!.TryDispatch(new NativeSceneRequest("World", false, "foundation-test"),
                    native, token);
                Assert(admitted.Succeeded, "provider borrows startup reservation");
                return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            };
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "native");
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.StartingMode);
            Assert(!launch.IsCompleted && test.Native.IsSceneBusy, "launch must await native drain before Running");
            var shutdown = test.Hosted.ShutdownAsync(); test.Host.Drain();
            Assert(!shutdown.IsCompleted && test.Hosted.Current.Phase == SessionPhase.Stopping, "shutdown retains native ownership");
            native.Sink!.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot("World", true, true)));
            test.Wait(shutdown);
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && !test.Native.IsSceneBusy, "late native completion releases terminal ownership");
        }

        private static void TestMenuCancellationFailure(string root)
        {
            using var test = new SessionFixture(root);
            test.Launch();
            var pending = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            test.Menu = token =>
            {
                token.Register(() => throw new InvalidOperationException("menu cancellation callback failure"));
                return pending.Task;
            };
            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            test.Until(() => test.MenuLoads == 1);
            var shutdown = test.Hosted.ShutdownAsync(); test.Host.Drain();
            Assert(!shutdown.IsCompleted && test.Native.IsSceneBusy, "throwing cancellation cannot skip the menu drain barrier");
            pending.SetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "cancelled"));
            test.Wait(menu); test.Wait(shutdown);
            Assert(!shutdown.Result.Succeeded && shutdown.Result.ErrorMessage.Contains("menu cancellation callback failure"),
                "shutdown must aggregate a throwing menu cancellation callback after completing its drain");
            Assert(!test.Native.IsSceneBusy, "cancellation callback failure cannot abandon native admission");
        }

        private static void TestDelayedSceneOwnership(string root)
        {
            using var test = new SessionFixture(root, endOnScene: true);
            test.Launch();
            test.Dispatch.HoldNextPost = true;
            test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-20, "OldReplacement", SceneLifecyclePhase.Activated, SceneLoadMode.Single, true));
            var stopped = SessionFixture.ModeSession!.StopAsync(); test.Wait(stopped);
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            test.Launch("new-scene-session");
            var newer = SessionFixture.ModeSession!.SessionId;
            test.Dispatch.Release();
            test.Until(() => !test.Host.HasPendingWork);
            Assert(test.Hosted.Current.Identity?.SessionId == newer && test.Hosted.Current.Phase == SessionPhase.Running,
                "a delayed scene event from a prior session cannot stop its successor");
        }

        private static void TestCapturedReadiness(string root)
        {
            using var test = new SessionFixture(root, endOnScene: true);
            var world = new ChangingReadinessWorld();
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(world));
            test.Launch();
            test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(-20, "Replacement", SceneLifecyclePhase.Activated, SceneLoadMode.Single, true));
            test.Until(() => !test.Host.HasPendingWork);
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && world.Reads == 1,
                "scene policy must use captured world readiness without re-reading an arbitrary provider getter");
        }

        private sealed class ChangingReadinessWorld : IWorldInstance
        {
            internal int Reads;
            public WorldReadiness Readiness => ++Reads == 1
                ? new WorldReadiness(new WorldSceneIdentity(-12, "World"), TransformState.Identity)
                : throw new InvalidOperationException("readiness changed after startup");
            public void Dispose() { }
        }

        private static void TestShutdownCancelsMainMenu(string root)
        {
            var test = new SessionFixture(root);
            test.Launch();
            var pending = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = CancellationToken.None;
            test.Menu = value => { token = value; return pending.Task; };
            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            test.Until(() => test.MenuLoads == 1);
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && test.Native.IsSceneBusy,
                "main-menu callback owns native admission after gameplay cleanup");
            var shutdown = test.Hosted.ShutdownAsync(); test.Host.Drain();
            Assert(token.IsCancellationRequested, "global shutdown must signal in-flight main-menu work after session identity clears");
            Assert(!shutdown.IsCompleted && test.Native.IsSceneBusy, "ignored menu cancellation must retain shutdown/native barrier");
            pending.SetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "menu cancellation"));
            test.Wait(menu); test.Wait(shutdown);
            Assert(!test.Native.IsSceneBusy && shutdown.Result.Succeeded, "shutdown completes only after cancelled menu callback and native drain");
            test.Dispose();
        }

        private static void TestShutdownAdmission(string root)
        {
            using (var test = new SessionFixture(root + "-idle"))
            {
                var constructed = false; SessionFixture.ProviderConstructor = () => constructed = true;
                var shutdown = test.Hosted.ShutdownAsync();
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "after-shutdown");
                test.Wait(shutdown); test.Wait(launch);
                Assert(!constructed && !launch.Result.Succeeded, "global shutdown must close admission at the host request before a following launch");
            }
            foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.Running })
            {
                using var test = new SessionFixture(root + phase);
                Task<OperationResult<bool>>? shutdown = null;
                var notifications = new List<SessionPhase>();
                test.Hosted.StateChanged += state =>
                {
                    notifications.Add(state.Phase);
                    if (state.Phase == phase) shutdown = test.Hosted.ShutdownAsync();
                };
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "shutdown-observer");
                test.Until(() => shutdown != null);
                test.Wait(shutdown!); test.Wait(launch);
                Assert(test.Hosted.Current.Phase == SessionPhase.Idle && test.Parent.ActiveChildScopeCount == 0,
                    "observer shutdown drains the captured session without reentrant cleanup");
                Assert(launch.Result.Succeeded == (phase == SessionPhase.Running), "Running is the committed launch success boundary");
                Assert(notifications[notifications.Count - 2] == SessionPhase.Stopping
                    && notifications[notifications.Count - 1] == SessionPhase.Idle, "shutdown notifications finish in committed order");
            }
        }

        private static void TestOversizedStartupError(string root)
        {
            var test = new SessionFixture(root);
            SessionFixture.Start = (_, _) => throw new InvalidOperationException(new string('x', 9000));
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "oversized");
            test.Wait(launch);
            test.Until(() => !test.Host.HasPendingWork);
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && test.Parent.ActiveChildScopeCount == 0,
                "oversized author exception must not make wire outcome construction strand cleanup");
            Assert(test.Outcomes.Count(value => value.Kind == "launch") == 1 && test.Outcomes.Count(value => value.Kind == "session") == 1,
                "oversized error still publishes both exactly-once outcomes");
            test.Dispose();
        }

        private static void TestNotificationStop(string root)
        {
            using var test = new SessionFixture(root);
            test.Hosted.StateChanged += state =>
            {
                if (state.Phase == SessionPhase.Running) SessionFixture.ModeSession!.Context.Lifetime.Dispose();
            };
            test.Launch();
            test.Host.Drain();
            Assert(SessionFixture.ChildLifetime!.IsStopping, "a stop requested by a Running observer must not be silently lost behind the launch lease");
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
        }

        private static void TestReentrantAdmission(string root)
        {
            using var test = new SessionFixture(root);
            test.Launch();
            Task<OperationResult<bool>>? competing = null;
            test.Hosted.StateChanged += state =>
            {
                if (state.Phase == SessionPhase.Stopping)
                    competing = test.Hosted.StartAsync(test.Plan.Descriptor, "reentrant");
            };
            var stop = SessionFixture.ModeSession!.StopAsync(); test.Wait(stop);
            test.Until(() => competing != null && competing.IsCompleted);
            Assert(competing!.Result.ErrorCode == ModErrorCode.Conflict,
                "a launch requested reentrantly while Stopping must reject Busy, never queue until Idle");
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle, "reentrant requests must not replace a session after stop");
        }

        private static void TestPreparingAndOwnerCancellation(string root)
        {
            using (var test = new SessionFixture(root + "-prepare"))
            using (var token = new CancellationTokenSource())
            {
                test.Hosted.StateChanged += state => { if (state.Phase == SessionPhase.Preparing) token.Cancel(); };
                var invoked = false; SessionFixture.ProviderConstructor = () => invoked = true;
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "preparing", token.Token); test.Wait(launch);
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                Assert(!invoked && launch.Result.ErrorCode == ModErrorCode.Cancelled && test.Parent.ActiveChildScopeCount == 0,
                    "Preparing cancellation must clean scopes before constructors run");
            }
            using (var test = new SessionFixture(root + "-owner"))
            {
                var pending = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
                SessionFixture.Load = (_, _) => pending.Task;
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "owner");
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.LoadingWorld);
                test.Parent.BeginStopping();
                var stopped = test.Hosted.StopOwnerAsync(test.Parent.Identity.Id);
                test.Host.Drain();
                Assert(!stopped.IsCompleted && test.Parent.ActiveChildScopeCount == 1,
                    "owner unload retains scoped services while ignored cancellation drains");
                pending.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
                test.Wait(stopped);
                Assert(test.Parent.ActiveChildScopeCount == 0 && test.Hosted.Current.Phase == SessionPhase.Idle,
                    "owner stop completes only after all callback and scope cleanup");
            }
        }

        internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private sealed class ControlledDispatch : IInternalNativeSceneDispatch
        {
            internal IInternalNativeSceneCompletion? Sink;
            public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion sink) { Sink = sink; return NativeSceneDispatchStatus.Dispatched; }
        }
    }
}
