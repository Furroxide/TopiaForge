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
    internal static class SessionActivationTests
    {
        internal static void Run(string root)
        {
            ObserversAreCommittedAndBound(root + "/observers");
            ContentStaysOwnedUntilStop(root + "/content");
            CancelledContentDrainsBeforeTerminal(root + "/cancel");
            ContentFailureAttemptsEveryCleanup(root + "/failure");
            Console.WriteLine("Session activation tests passed.");
        }
        private static void ObserversAreCommittedAndBound(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            var states = new List<WorldSessionSnapshot>();
            var constructed = 0;
            SessionFixture.FactoryConstructor = () => constructed++;
            fixture.Parent.Sessions.StateChanged += state =>
            {
                Assert(fixture.Parent.Sessions.Current.Phase == state.Phase, "observers must see committed state");
                states.Add(state);
            };
            fixture.Launch();
            Assert(states.Select(state => state.Phase).SequenceEqual(new[] { WorldSessionPhase.Preparing, WorldSessionPhase.LoadingWorld,
                WorldSessionPhase.StartingMode, WorldSessionPhase.Running }), "observers require ordered complete startup phases");
            Assert(constructed == 1 && states.All(state => !(state.Session is IGamemodeSession)),
                "observation must neither construct a controller nor expose another package's mod context");
            var stale = fixture.Parent.Sessions.Current.Session!;
            var scoped = ((IInternalWorldSessionContext)SessionFixture.ModeSession!.Context).Sessions;
            var scopedHandle = scoped.Current.Session!;
            fixture.Wait(stale.RestartAsync());
            Assert(constructed == 2 && fixture.Parent.Sessions.Current.Session!.SessionId != stale.SessionId, "restart must create one new identity");
            var staleStop = stale.StopAsync(); fixture.Wait(staleStop);
            var scopedStop = scopedHandle.StopAsync(); fixture.Wait(scopedStop);
            Assert(staleStop.Result.ErrorCode == ModErrorCode.InvalidState && scopedStop.Result.ErrorCode == ModErrorCode.Cancelled,
                "stale session and stopped consumer handles cannot stop their successor");
        }
        private static void ContentStaysOwnedUntilStop(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            fixture.Launch();
            var session = fixture.Parent.Sessions.Current.Session!;
            var disposed = 0;
            IModContext? child = null;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(session.SessionId, (context, _) =>
            {
                child = context.Context;
                Assert(!ReferenceEquals(child, fixture.Parent) && child.Identity.Id == fixture.Parent.Identity.Id
                    && context.SessionId == session.SessionId, "content callbacks need a fresh exact-owner scope with stable public session identity");
                child.Lifetime.Defer(() => disposed++);
                return Task.FromResult(OperationResult<IDisposable>.Success(new Resource(() => disposed++)));
            });
            fixture.Wait(imported);
            Assert(imported.Result.Succeeded && disposed == 0 && !child!.Lifetime.IsStopping, "successful imported content remains session-owned");
            fixture.Wait(session.StopAsync());
            fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(disposed == 2 && child!.Lifetime.IsStopping, "session stop must release imported result and child resources");
        }
        private static void CancelledContentDrainsBeforeTerminal(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            fixture.Launch();
            var session = fixture.Parent.Sessions.Current.Session!;
            var pending = new TaskCompletionSource<OperationResult<IDisposable>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false; var disposed = 0;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(session.SessionId, (_, _) => { entered = true; return pending.Task; });
            fixture.Until(() => entered);
            var competing = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "competing"); fixture.Wait(competing);
            Assert(competing.Result.ErrorCode == ModErrorCode.Conflict, "content work holds shared launch admission Busy");
            fixture.Wait(session.StopAsync());
            Assert(!imported.IsCompleted && fixture.Hosted.Current.Phase == SessionPhase.Stopping
                && fixture.Outcomes.Count(outcome => outcome.Kind == "session") == 0, "stop cannot publish terminal before a callback drains");
            pending.SetResult(OperationResult<IDisposable>.Success(new Resource(() => disposed++)));
            fixture.Wait(imported);
            Assert(imported.Result.ErrorCode == ModErrorCode.Cancelled && disposed == 1 && fixture.Hosted.Current.Phase == SessionPhase.Idle,
                "late imported content must be disposed before terminal release");
            Assert(fixture.Outcomes.Count(outcome => outcome.Kind == "session") == 1, "content cancellation has one terminal session outcome");
        }
        private static void ContentFailureAttemptsEveryCleanup(string root)
        {
            using var fixture = new SessionFixture(root);
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            fixture.Launch();
            var disposed = 0;
            var imported = fixture.Parent.ContentOperations.RunContentOperationAsync(fixture.Parent.Sessions.Current.Session!.SessionId, (context, _) =>
            {
                context.Context.Lifetime.Defer(() => disposed++);
                context.Context.Lifetime.Defer(() => throw new InvalidOperationException("content-scope-cleanup"));
                throw new AggregateException("import failed", new InvalidOperationException("native-root-cleanup"));
            });
            fixture.Wait(imported);
            Assert(!imported.Result.Succeeded && disposed == 1 && imported.Result.ErrorMessage.Contains("content-scope-cleanup")
                && imported.Result.ErrorMessage.Contains("native-root-cleanup"), "every independent content cleanup must run and retain its cause");
            fixture.Wait(fixture.Parent.Sessions.Current.Session!.StopAsync());
            fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(fixture.Outcomes.Single(outcome => outcome.Kind == "session").Status == "failed", "content cleanup failures must remain in terminal evidence");
        }
        private sealed class Resource : IDisposable
        {
            private Action? dispose;
            internal Resource(Action dispose) { this.dispose = dispose; }
            public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
