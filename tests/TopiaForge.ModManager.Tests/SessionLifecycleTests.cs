using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class SessionLifecycleTests
    {
        internal static void Run(string root)
        {
            using var fixture = new SessionFixture(root);
            var life = new SessionLifecycle();
            Assert(life.TryAcquire(true, null, out _) == SessionAdmission.Busy, "native ownership blocks new admission");
            Assert(life.TryAcquire(false, null, out var first) == SessionAdmission.Accepted, "idle admission");
            Assert(life.TryAcquire(false, null, out _) == SessionAdmission.Busy, "second operation cannot share a lease");
            var identity = new SessionIdentity("one", "request", fixture.Plan.Descriptor);
            life.Commit(first!, SessionPhase.Preparing, identity);
            Reject(() => life.Release(first!));
            Reject(() => life.Commit(first!, SessionPhase.Running));
            life.Commit(first!, SessionPhase.LoadingWorld);
            life.Commit(first!, SessionPhase.StartingMode);
            life.Commit(first!, SessionPhase.Running);
            life.Release(first!);
            Assert(life.TryAcquire(false, "old", out _) == SessionAdmission.StaleSession, "captured identity controls stop authority");
            Assert(life.TryAcquire(false, "one", out var stop) == SessionAdmission.Accepted, "running stop admission");
            Reject(() => life.Commit(first!, SessionPhase.Stopping));
            life.Commit(stop!, SessionPhase.Stopping);
            Assert(life.TryAcquire(false, null, out _) == SessionAdmission.Busy, "stopping is busy");
            life.Commit(stop!, SessionPhase.Idle);
            Assert(life.TryAcquire(false, null, out _) == SessionAdmission.Busy, "replacement lease spans temporary idle");
            life.Release(stop!);
            Assert(life.Current.Identity == null && life.Current.Sequence == 6, "terminal identity cleared after six committed changes");
            Console.WriteLine("SessionLifecycleTests passed.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Reject(Action callback)
        { try { callback(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("invalid state mutation accepted"); }
    }
}
