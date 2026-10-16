using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class ContentAdmissionTests
    {
        internal static void Run(string root, string? selected = null)
        {
            var cases = new Dictionary<string, Action<string>>
            {
                ["reserve-throw"] = ReservationFailureReleasesAdmission,
                ["expected-failure"] = ExpectedFailureKeepsItsCode,
                ["grant-cleanup"] = GrantFailureKeepsReturnedResource,
                ["worker-cancel"] = WorkerCancellationDrainsThrowingCallbacks,
                ["stale-content"] = StaleContentCannotStopSuccessor,
                ["child-restart"] = ChildFacadeRestartSurvivesItsOwnTeardown,
                ["child-menu"] = ChildFacadeMenuSurvivesItsOwnTeardown,
                ["child-explicit-cancel"] = ChildFacadePreservesCallerCancellation,
                ["child-queued-stop"] = ChildFacadeRechecksLifetimeAtAdmission,
                ["menu-reserve-throw"] = MenuReservationFailureCompletesOnce,
                ["menu-close-throw"] = MenuCloseFailureCompletesAfterDrain,
            };
            foreach (var item in cases)
                if (selected == null || item.Key == selected) item.Value(root + "/" + item.Key);
            if (selected != null && !cases.ContainsKey(selected)) throw new ArgumentException("Unknown content case: " + selected);
            Console.WriteLine("Content admission tests passed.");
        }

        private static void ReservationFailureReleasesAdmission(string root)
        {
            FaultExecutor faults = null!;
            // A broken implementation must fail this assertion promptly, without blocking test cleanup
            // forever on the leaked operation whose absence is the behavior being tested.
            var fixture = new SessionFixture(root, decorateNative: value => faults = new FaultExecutor(value));
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            fixture.Launch();
            var scopes = fixture.Parent.ActiveChildScopeCount;
            faults.ThrowReserve = true;
            var called = false;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(
                fixture.Parent.Sessions.Current.Session!.SessionId, (_, _) =>
                { called = true; return Task.FromResult(OperationResult<IDisposable>.Success(new Resource(() => { }))); });
            fixture.Host.Drain();
            Assert(imported.IsCompleted, "native reservation exceptions must complete the content task and release admission");
            Assert(!imported.Result.Succeeded && imported.Result.ErrorMessage.Contains("reserve-fault")
                && !called && fixture.Parent.ActiveChildScopeCount == scopes && !fixture.Native.IsSceneBusy,
                "reservation failure must allocate no child or invoke package callbacks");
            var restarted = fixture.Parent.Sessions.Current.Session!.RestartAsync(); fixture.Wait(restarted);
            Assert(restarted.Result.Succeeded, "a failed reservation cannot leave future launches Busy");
            fixture.Dispose();
        }

        private static void ExpectedFailureKeepsItsCode(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var scopes = fixture.Parent.ActiveChildScopeCount;
            var disposed = 0;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(fixture.Parent.Sessions.Current.Session!.SessionId,
                (context, _) =>
                {
                    context.Context.Lifetime.Defer(() => disposed++);
                    return Task.FromResult(OperationResult<IDisposable>.Failure(ModErrorCode.NotFound, "selected-content-missing"));
                });
            fixture.Wait(imported);
            Assert(imported.Result.ErrorCode == ModErrorCode.NotFound && imported.Result.ErrorMessage == "selected-content-missing",
                "internal cleanup cancellation must preserve a delivered provider failure code and message");
            Assert(disposed == 1 && fixture.Parent.ActiveChildScopeCount == scopes && fixture.Hosted.Current.Phase == SessionPhase.Running,
                "ordinary content failure cleans only its child and leaves the session running");
            fixture.Wait(fixture.Parent.Sessions.Current.Session!.StopAsync());
            fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(fixture.Outcomes.Single(value => value.Kind == "session").Status == "succeeded",
                "a handled missing-content result must not poison terminal cleanup evidence");
        }

        private static void GrantFailureKeepsReturnedResource(string root)
        {
            FaultExecutor faults = null!;
            using var fixture = new SessionFixture(root, decorateNative: value => faults = new FaultExecutor(value));
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var disposed = 0;
            faults.ThrowGrantCleanup = true;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(fixture.Parent.Sessions.Current.Session!.SessionId,
                (context, _) =>
                {
                    context.Context.Lifetime.Defer(() => { disposed++; throw new InvalidOperationException("scope-cleanup"); });
                    return Task.FromResult(OperationResult<IDisposable>.Success(new Resource(() =>
                    { disposed++; throw new InvalidOperationException("content-cleanup"); })));
                });
            fixture.Wait(imported);
            Assert(disposed == 2, "a returned content resource must be owned before borrowed-grant cleanup can throw");
            Assert(imported.Result.ErrorCode == ModErrorCode.External
                && new[] { "grant-cleanup", "scope-cleanup", "content-cleanup" }.All(imported.Result.ErrorMessage.Contains),
                "grant, returned-content and child cleanup faults all remain actionable");
            Assert(!fixture.Native.IsSceneBusy, "cleanup errors cannot retain a drained native reservation");
        }

        private static void WorkerCancellationDrainsThrowingCallbacks(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<OperationResult<IDisposable>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false; var callbacks = 0; var disposed = 0;
            var hostThread = Thread.CurrentThread.ManagedThreadId;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(fixture.Parent.Sessions.Current.Session!.SessionId,
                (context, token) =>
                {
                    token.Register(() => { Assert(Thread.CurrentThread.ManagedThreadId == hostThread, "operation cancellation belongs to host"); callbacks++; throw new InvalidOperationException("callback-cancel"); });
                    context.Context.Lifetime.StoppingToken.Register(() => { Assert(Thread.CurrentThread.ManagedThreadId == hostThread, "scope cancellation belongs to host"); callbacks++; throw new InvalidOperationException("scope-cancel"); });
                    context.Context.Lifetime.Defer(() => disposed++);
                    entered = true; return pending.Task;
                }, cancellation.Token);
            fixture.Until(() => entered);
            Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
            fixture.Until(() => callbacks == 2);
            var competing = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "content-busy"); fixture.Wait(competing);
            Assert(competing.Result.ErrorCode == ModErrorCode.Conflict && !imported.IsCompleted && disposed == 0,
                "caller cancellation retains ownership until its callback returns");
            pending.SetResult(OperationResult<IDisposable>.Success(new Resource(() => disposed++)));
            fixture.Wait(imported);
            Assert(disposed == 2 && imported.Result.ErrorCode == ModErrorCode.External
                && imported.Result.ErrorMessage.Contains("callback-cancel") && imported.Result.ErrorMessage.Contains("scope-cancel"),
                "throwing cancellation callbacks cannot escape or lose late content cleanup");
            Assert(fixture.Hosted.Current.Phase == SessionPhase.Running, "caller cancellation alone does not stop the session");
        }

        private static void StaleContentCannotStopSuccessor(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            IModLifetime stale = null!;
            var first = fixture.Parent.Sessions.Current.Session!;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(first.SessionId, (context, _) =>
            { stale = context.Context.Lifetime; return Task.FromResult(OperationResult<IDisposable>.Success(new Resource(() => { }))); });
            fixture.Wait(imported);
            fixture.Wait(first.RestartAsync());
            var nextId = fixture.Parent.Sessions.Current.Session!.SessionId;
            var pending = new TaskCompletionSource<OperationResult<IDisposable>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false;
            var next = fixture.Parent.ContentOperations.RunContentOperationAsync(nextId, (_, _) => { entered = true; return pending.Task; });
            fixture.Until(() => entered);
            var postedBefore = fixture.Dispatch.PostedCount;
            stale.Dispose(); fixture.Host.Drain(20);
            var stalePosts = fixture.Dispatch.PostedCount - postedBefore;
            Assert(fixture.Parent.Sessions.Current.Session!.SessionId == nextId && fixture.Hosted.Current.Phase == SessionPhase.Running,
                "old content lifetime callbacks cannot cancel a new session's content operation");
            pending.SetResult(OperationResult<IDisposable>.Success(new Resource(() => { })));
            fixture.Wait(next);
            Assert(next.Result.Succeeded, "a stale scope cannot cancel the newer operation token");
            Assert(stalePosts <= 1, "a stale stop callback must dispatch at most once, without a Busy retry storm");
        }

        private static void ChildFacadeRestartSurvivesItsOwnTeardown(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var original = SessionFixture.ModeSession!;
            var facade = ((IInternalWorldSessionContext)original.Context).Sessions.Current.Session!;
            var restarted = facade.RestartAsync(); fixture.Wait(restarted);
            Assert(restarted.Result.Succeeded && original.Lifetime.IsStopping
                && fixture.Hosted.Current.Phase == SessionPhase.Running
                && fixture.Parent.Sessions.Current.Session!.SessionId != original.SessionId,
                "an admitted child-facade restart must survive teardown of its consuming predecessor scope");
            var stale = facade.RestartAsync(); fixture.Wait(stale);
            Assert(stale.Result.ErrorCode == ModErrorCode.Cancelled, "the stopped consuming facade cannot restart its successor");
        }

        private static void ChildFacadeMenuSurvivesItsOwnTeardown(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var original = SessionFixture.ModeSession!;
            var facade = ((IInternalWorldSessionContext)original.Context).Sessions.Current.Session!;
            var menu = facade.ReturnToMainMenuAsync(); fixture.Wait(menu);
            Assert(menu.Result.Succeeded && fixture.MenuLoads == 1 && original.Lifetime.IsStopping
                && fixture.Hosted.Current.Phase == SessionPhase.Idle,
                "an admitted child-facade main-menu operation must survive its consuming scope teardown");
        }

        private static void ChildFacadePreservesCallerCancellation(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            using var cancellation = new CancellationTokenSource();
            var facade = ((IInternalWorldSessionContext)SessionFixture.ModeSession!.Context).Sessions.Current.Session!;
            var pending = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false; var tokenSeen = CancellationToken.None; var disposed = 0;
            SessionFixture.Load = (_, token) => { entered = true; tokenSeen = token; return pending.Task; };
            var restarted = facade.RestartAsync(cancellation.Token);
            fixture.Until(() => entered);
            Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
            fixture.Until(() => tokenSeen.IsCancellationRequested);
            pending.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed++)));
            fixture.Wait(restarted); fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(restarted.Result.ErrorCode == ModErrorCode.Cancelled && disposed == 1,
                "independent caller cancellation remains effective after child-facade admission and drains late worlds");
        }

        private static void ChildFacadeRechecksLifetimeAtAdmission(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var context = (ModContext)SessionFixture.ModeSession!.Context;
            var id = fixture.Parent.Sessions.Current.Session!.SessionId;
            var facade = ((IInternalWorldSessionContext)context).Sessions.Current.Session!;
            Task<OperationResult<bool>> queued = null!;
            Task.Run(() => { queued = facade.RestartAsync(); }).GetAwaiter().GetResult();
            context.BeginStopping();
            fixture.Wait(queued);
            Assert(queued.Result.ErrorCode == ModErrorCode.Cancelled && fixture.Hosted.Current.Phase == SessionPhase.Running
                && fixture.Parent.Sessions.Current.Session!.SessionId == id,
                "a scope stopped after queuing but before host admission cannot restart the session");
        }
        private static void MenuReservationFailureCompletesOnce(string root)
        {
            FaultExecutor faults = null!;
            var fixture = new SessionFixture(root, decorateNative: value => faults = new FaultExecutor(value));
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var id = fixture.Parent.Sessions.Current.Session!.SessionId;
            faults.ThrowReserve = true;
            var menu = fixture.Parent.Sessions.Current.Session!.ReturnToMainMenuAsync();
            fixture.Host.Drain();
            Assert(menu.IsCompleted, "a main-menu reservation exception must complete its command and release the lease");
            Assert(menu.Result.ErrorCode == ModErrorCode.External && menu.Result.ErrorMessage.Contains("reserve-fault")
                && fixture.MenuLoads == 0 && fixture.Parent.Sessions.Current.Session!.SessionId == id,
                "failed menu admission must preserve the running session and avoid scene callbacks");
            Assert(fixture.Outcomes.Count(value => value.Command == "main-menu") == 1
                && fixture.Outcomes.Count(value => value.Kind == "session") == 0,
                "failed menu admission publishes exactly one command outcome and no session terminal");
            var restarted = fixture.Parent.Sessions.Current.Session!.RestartAsync(); fixture.Wait(restarted);
            Assert(restarted.Result.Succeeded, "menu reservation failure cannot leave later requests Busy");
            fixture.Dispose();
        }

        private static void MenuCloseFailureCompletesAfterDrain(string root)
        {
            FaultExecutor faults = null!;
            var fixture = new SessionFixture(root, decorateNative: value => faults = new FaultExecutor(value));
            fixture.Parent.ConfigureSessions(fixture.Hosted); fixture.Launch();
            var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            faults.CloseBarrier = late.Task;
            faults.ThrowClose = true;
            fixture.Menu = _ => throw new InvalidOperationException("menu-load-fault");
            var diagnostics = new List<Exception>();
            fixture.Hosted.DiagnosticFailure += diagnostics.Add;
            var menu = fixture.Parent.Sessions.Current.Session!.ReturnToMainMenuAsync();
            fixture.Until(() => faults.CloseEntered);
            var competing = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "during-menu-drain"); fixture.Wait(competing);
            Assert(!menu.IsCompleted && competing.Result.ErrorCode == ModErrorCode.Conflict
                && fixture.Outcomes.Count(value => value.Command == "main-menu") == 0,
                "menu cleanup must retain Busy and withhold command terminal until its native close completes");
            late.SetResult(true);
            fixture.Until(() => menu.IsCompleted || diagnostics.Count != 0);
            Assert(menu.IsCompleted, "a fault after native menu drain must complete its command and release the lease");
            Assert(menu.Result.ErrorCode == ModErrorCode.External && menu.Result.ErrorMessage.Contains("menu-load-fault")
                && menu.Result.ErrorMessage.Contains("menu-close-fault") && !fixture.Native.IsSceneBusy,
                "menu callback and late native-close faults must both remain in the operation result");
            Assert(fixture.Outcomes.Count(value => value.Command == "main-menu") == 1
                && fixture.Outcomes.Count(value => value.Kind == "session") == 1,
                "a failed menu close has one command terminal and one predecessor session terminal");
            fixture.Launch("after-menu-drain");
            fixture.Dispose();
        }
        private sealed class Resource : IDisposable
        {
            private Action? dispose;
            internal Resource(Action dispose) { this.dispose = dispose; }
            public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
        }

        private sealed class FaultExecutor : INativeTransitionExecutor
        {
            private readonly INativeTransitionExecutor inner;
            internal bool ThrowReserve;
            internal bool ThrowGrantCleanup;
            internal bool ThrowClose;
            internal Task? CloseBarrier;
            internal bool CloseEntered;
            internal FaultExecutor(INativeTransitionExecutor inner) { this.inner = inner; }
            public bool IsSceneBusy => inner.IsSceneBusy;
            public void SetSessionAdmissionGate(Func<bool> busy) => inner.SetSessionAdmissionGate(busy);
            public Task<NativeDrainResult> WaitForIdleAsync() => inner.WaitForIdleAsync();
            public OperationResult<INativeTransitionReservation> TryReserve(NativeTransitionOwner owner, string id)
            {
                if (ThrowReserve) { ThrowReserve = false; throw new InvalidOperationException("reserve-fault"); }
                var result = inner.TryReserve(owner, id);
                if ((!ThrowGrantCleanup && !ThrowClose && CloseBarrier == null) || !result.TryGetValue(out var value)) return result;
                var wrapped = new FaultReservation(value, ThrowGrantCleanup, ThrowClose, CloseBarrier, () => CloseEntered = true);
                ThrowGrantCleanup = false; ThrowClose = false; CloseBarrier = null;
                return OperationResult<INativeTransitionReservation>.Success(wrapped);
            }
        }
        private sealed class FaultReservation : INativeTransitionReservation
        {
            private readonly INativeTransitionReservation inner;
            private readonly bool throwGrant;
            private readonly bool throwClose;
            private readonly Task? closeBarrier;
            private readonly Action closeEntered;
            internal FaultReservation(INativeTransitionReservation inner, bool throwGrant, bool throwClose, Task? closeBarrier, Action closeEntered)
            { this.inner = inner; this.throwGrant = throwGrant; this.throwClose = throwClose; this.closeBarrier = closeBarrier; this.closeEntered = closeEntered; }
            public INativeTransitionGrant BorrowFor(string packageId, string sessionId)
            {
                var grant = inner.BorrowFor(packageId, sessionId);
                return throwGrant ? new FaultGrant(grant) : grant;
            }
            public async Task<NativeDrainResult> CloseAsync()
            {
                var result = await inner.CloseAsync();
                closeEntered();
                if (closeBarrier != null) await closeBarrier;
                if (throwClose) throw new InvalidOperationException("menu-close-fault");
                return result;
            }
            public void Dispose() => inner.Dispose();
        }
        private sealed class FaultGrant : INativeTransitionGrant
        {
            private readonly INativeTransitionGrant inner;
            internal FaultGrant(INativeTransitionGrant inner) { this.inner = inner; }
            public string SessionId => inner.SessionId;
            public IInternalSceneTransitionService SceneTransitions => inner.SceneTransitions;
            public void Dispose() { inner.Dispose(); throw new InvalidOperationException("grant-cleanup"); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
