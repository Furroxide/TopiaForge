using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Chronos;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScopedExtensionFacadeTests
    {
        internal static void Run()
        {
            using var lifetime = new OwnerModLifetime();
            var time = new TimeControlService();
            var facade = time.ForOwner(lifetime);
            lifetime.BeginStop();
            Assert(facade.Freeze("stale").ErrorCode == ModErrorCode.InvalidState && time.Calls == 0,
                "a stale time facade must not allocate or replace service leases before rejection");
            facade.Slow("stale", 0.5f); facade.ExemptPlayer("stale");
            Assert(time.Calls == 0, "all stopped time allocations must reject before backing-service work");
            using var rejectedLifetime = new RejectingLifetime();
            var rejected = new TimeControlService();
            rejected.ForOwner(rejectedLifetime).Freeze("rejected");
            Assert(rejected.LastLease!.IsActive, "Track rejection transfers lease cleanup to the lifetime, without direct second disposal");
            rejectedLifetime.Dispose();
            Assert(!rejected.LastLease.IsActive, "deferred rejected time leases remain owned until cleanup");

            using var turnLifetime = new OwnerModLifetime();
            var turnBackend = new TimeControlService();
            var turn = turnBackend.ForOwner(turnLifetime).BeginTurnBased("turns", new TurnSchedulerOptions()).Value!;
            turnLifetime.BeginStop();
            Assert(turn.BeginAction().ErrorCode == ModErrorCode.InvalidState && turn.EndAction().ErrorCode == ModErrorCode.InvalidState,
                "a retained turn scheduler cannot mutate after its owner stops");
            turn.Register(default, 1); turn.Unregister(default); turn.Tick(1);
            Assert(turnBackend.LastScheduler!.Calls == 0, "stopped turn scheduler callbacks never reach the backing scheduler");
            using var pauseLifetime = new OwnerModLifetime();
            var pause = new PauseMenuBridge();
            var owner = pause.ForOwner(pauseLifetime);
            var actions = 0; var exits = 0;
            owner.RegisterAction(new WorldPauseAction("action", "Action", () => actions++));
            owner.InterceptExit(_ => { exits++; return WorldPauseExitDecision.EndSessionAndExit; });
            pause.Action!.Callback(); pause.Interceptor!(null!);
            Assert(actions == 1 && exits == 1, "active pause callbacks should forward");
            pauseLifetime.BeginStop();
            pause.Action.Callback();
            Assert(pause.Interceptor!(null!) == WorldPauseExitDecision.Block && actions == 1 && exits == 1,
                "cancel-only must suppress pause callbacks and stale exit requests until cleanup");
            var calls = pause.Calls;
            Assert(owner.RegisterAction(new WorldPauseAction("stale", "Stale", () => { })).ErrorCode == ModErrorCode.Cancelled
                && owner.InterceptExit(_ => WorldPauseExitDecision.Block).ErrorCode == ModErrorCode.Cancelled && pause.Calls == calls,
                "stopped pause registrations must not enter or replace backing-service state");
            Console.WriteLine("ScopedExtensionFacadeTests passed.");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private sealed class RejectingLifetime : IModLifetime
        {
            private readonly List<IDisposable> rejected = new List<IDisposable>();
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            public bool IsStopping => cancellation.IsCancellationRequested;
            public CancellationToken StoppingToken => cancellation.Token;
            public IDisposable Track(IDisposable resource)
            { rejected.Add(resource); cancellation.Cancel(); throw new ObjectDisposedException("scope"); }
            public IDisposable Defer(Action action) => throw new NotSupportedException();
            public void Dispose()
            { foreach (var resource in rejected) resource.Dispose(); rejected.Clear(); }
        }
    }
}
