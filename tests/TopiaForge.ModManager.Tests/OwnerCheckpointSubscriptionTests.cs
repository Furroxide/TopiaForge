using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class OwnerCheckpointSubscriptionTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { StoppingSuppressesCallbacks, StoppedRegistrationDoesNotPublish,
                CancellationDuringRegistrationRetainsOwnership, DiagnosticFailureDoesNotInterruptOtherOwners })
                try { test(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        private static void StoppingSuppressesCallbacks()
        {
            using var lifetime = new OwnerModLifetime();
            var backend = new Backend();
            var called = 0;
            OwnerCheckpointSubscription.Subscribe(lifetime, _ => called++, backend.Register, error => throw error);
            backend.Callback!(null!);
            Assert(called == 1, "active checkpoint subscribers receive events");
            lifetime.BeginStop();
            backend.Callback!(null!);
            Assert(called == 1 && backend.Disposed == 0,
                "checkpoint delivery stops on cancellation while native subscription cleanup remains deferred");
            lifetime.Dispose();
            Assert(backend.Disposed == 1, "the native subscription is released once");
        }

        private static void StoppedRegistrationDoesNotPublish()
        {
            using var lifetime = new OwnerModLifetime();
            lifetime.BeginStop();
            var backend = new Backend();
            ThrowsDisposed(() => OwnerCheckpointSubscription.Subscribe(lifetime, _ => { }, backend.Register, _ => { }));
            Assert(backend.Registered == 0, "a stopped checkpoint registration must not reach the backend");
        }

        private static void CancellationDuringRegistrationRetainsOwnership()
        {
            using var lifetime = new OwnerModLifetime();
            var backend = new Backend { DuringRegistration = lifetime.BeginStop };
            ThrowsDisposed(() => OwnerCheckpointSubscription.Subscribe(lifetime, _ => { }, backend.Register, _ => { }));
            Assert(backend.Registered == 1 && backend.Disposed == 1,
                "Track rejection retains ownership when cancellation happens while the backend is registering");
        }

        private static void DiagnosticFailureDoesNotInterruptOtherOwners()
        {
            using var lifetime = new OwnerModLifetime();
            var first = new Backend();
            var second = new Backend();
            var delivered = 0;
            var reported = 0;
            OwnerCheckpointSubscription.Subscribe(lifetime, _ => throw new InvalidOperationException("handler failure"),
                first.Register, _ => { reported++; throw new InvalidOperationException("diagnostic failure"); });
            OwnerCheckpointSubscription.Subscribe(lifetime, _ => delivered++, second.Register, _ => { });
            foreach (var callback in new[] { first.Callback!, second.Callback! }) callback(null!);
            Assert(reported == 1 && delivered == 1,
                "a failing handler and diagnostic sink cannot interrupt another owner's checkpoint delivery");
        }

        private sealed class Backend
        {
            internal int Registered;
            internal int Disposed;
            internal Action? DuringRegistration;
            internal Action<CheckpointSnapshot>? Callback;
            internal IDisposable Register(Action<CheckpointSnapshot> callback)
            {
                Registered++; Callback = callback; DuringRegistration?.Invoke();
                return new Subscription(this);
            }
            private sealed class Subscription : IDisposable
            {
                private Backend? owner;
                internal Subscription(Backend owner) { this.owner = owner; }
                public void Dispose() { if (owner == null) return; owner.Disposed++; owner.Callback = null; owner = null; }
            }
        }
        private static void ThrowsDisposed(Action action)
        {
            try { action(); } catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("A cancelled registration must reject new ownership.");
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
