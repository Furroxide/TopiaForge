using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeWorkLifecycleTests
    {
        internal static void Run(string root)
        {
            PrematureLifetimeDisposalRetainsResources();
            FailedScopeConstructionRetainsNativeOwnership(root + "-construction");
            foreach (var throws in new[] { false, true }) SessionStopRetainsNativeOwnership(root + "-session-" + throws, throws);
            Console.WriteLine("Native work lifecycle tests passed.");
        }

        private static void PrematureLifetimeDisposalRetainsResources()
        {
            using var lifetime = new OwnerModLifetime();
            var work = ((IInternalNativeWorkLifetime)(IModLifetime)lifetime).RegisterNativeWork("pending bundle");
            var disposed = 0;
            lifetime.Defer(() => disposed++);
            try
            {
                lifetime.BeginStop();
                var refused = false;
                try { ((IInternalNativeWorkLifetime)(IModLifetime)lifetime).RegisterNativeWork("too late"); }
                catch (ObjectDisposedException) { refused = true; }
                Assert(refused, "stopping must revoke native admission before any new request allocation");
                try { lifetime.Dispose(); } catch (InvalidOperationException) { }
                Assert(disposed == 0, "synchronous disposal cannot destroy resources while a native request is pending");
            }
            finally { work.Complete(); }
            ((IInternalNativeWorkLifetime)(IModLifetime)lifetime).DrainNativeWorkAsync().GetAwaiter().GetResult();
            lifetime.Dispose();
            Assert(disposed == 1, "native completion permits resource disposal exactly once");
        }

        private static void SessionStopRetainsNativeOwnership(string root, bool throwingCleanup)
        {
            using var fixture = new SessionFixture(root);
            AssetNativeWorkTicket? work = null;
            var controllerDisposed = 0;
            var resourceDisposed = 0;
            SessionFixture.Start = (session, _) =>
            {
                SessionFixture.ModeSession = session;
                work = ((IInternalNativeWorkLifetime)session.Lifetime).RegisterNativeWork("session prefab");
                session.Lifetime.Defer(() =>
                {
                    resourceDisposed++;
                    if (throwingCleanup) throw new InvalidOperationException("scope resource cleanup failure");
                });
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController(() => controllerDisposed++)));
            };
            fixture.Launch();
            try
            {
                // The session-bound StopAsync acknowledges acceptance; owner stop exposes the full drain task.
                var stop = fixture.Hosted.StopOwnerAsync(fixture.Parent.Identity.Id);
                fixture.Host.Drain();
                Assert(!stop.IsCompleted && fixture.Hosted.Current.Phase == SessionPhase.Stopping
                    && fixture.Parent.ActiveChildScopeCount == 1 && controllerDisposed == 0 && resourceDisposed == 0,
                    "session cleanup must remain Busy and retain the controller, assets and parent scope until native completion");
                var competing = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "native-competing");
                fixture.Wait(competing);
                Assert(competing.Result.ErrorCode == ModErrorCode.Conflict && fixture.Outcomes.All(outcome => outcome.Kind != "session"),
                    "a competing launch and terminal publication cannot bypass native asset drain");
                work!.Complete(throwingCleanup ? new InvalidOperationException("late native asset cleanup failure") : null);
                fixture.Wait(stop);
                fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
                var terminal = fixture.Outcomes.Single(outcome => outcome.Kind == "session");
                Assert(controllerDisposed == 1 && resourceDisposed == 1 && fixture.Parent.ActiveChildScopeCount == 0,
                    "native drain must release every resource once before parent ownership ends");
                Assert(terminal.Status == (throwingCleanup ? "failed" : "succeeded"), "late cleanup changes the single terminal outcome");
                if (throwingCleanup) Assert(terminal.Error != null
                    && terminal.Error.Message.Contains("late native asset cleanup failure", StringComparison.Ordinal)
                    && terminal.Error.Message.Contains("scope resource cleanup failure", StringComparison.Ordinal),
                    "native and ordinary cleanup failures must both remain in terminal evidence");
            }
            finally { work?.Complete(); }
        }

        private static void FailedScopeConstructionRetainsNativeOwnership(string root)
        {
            using var fixture = new SessionFixture(root);
            AssetNativeWorkTicket? work = null;
            var resourceDisposed = 0;
            var diagnostics = new List<Exception>();
            fixture.Hosted.DiagnosticFailure += diagnostics.Add;
            fixture.ScopeFactory = lifetime =>
            {
                work = ((IInternalNativeWorkLifetime)(IModLifetime)lifetime).RegisterNativeWork("constructor bundle");
                lifetime.Defer(() => resourceDisposed++);
                throw new InvalidOperationException("native scope initialization failure");
            };
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "native-constructor");
            fixture.Host.Drain();
            try
            {
                Assert(work != null && fixture.Parent.ActiveChildScopeCount == 1 && resourceDisposed == 0
                    && fixture.Outcomes.All(outcome => outcome.Kind != "session"),
                    "failed initialization must retain its otherwise unreturned scope until actual native completion");
                work!.Complete(new InvalidOperationException("constructor late native cleanup failure"));
                fixture.Wait(launch);
                fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
                var evidence = string.Join(";", diagnostics.Select(error => error.ToString()));
                Assert(resourceDisposed == 1 && fixture.Parent.ActiveChildScopeCount == 0 && !launch.Result.Succeeded
                    && evidence.Contains("native scope initialization failure", StringComparison.Ordinal)
                    && evidence.Contains("constructor late native cleanup failure", StringComparison.Ordinal),
                    "failed initialization must retain primary and native cleanup failures while draining its scope");
            }
            finally { work?.Complete(); }
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
