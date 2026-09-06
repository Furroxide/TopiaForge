using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScopedConstructionDrainTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var throws in new[] { false, true })
                try { Check(root + "-" + throws, throws); } catch (Exception error) { failures.Add(error); }
            try { CancellationDuringConstructionRetainsScope(root + "-cancel"); }
            catch (Exception error) { failures.Add(error); }
            try { CompletedConstructionReturnsOwnedScopeAfterCancellation(root + "-completed-cancel"); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        private static void Check(string root, bool cleanupThrows)
        {
            var fixture = new SessionFixture(root);
            var cleanups = 0;
            var diagnostics = new List<Exception>();
            fixture.Hosted.DiagnosticFailure += diagnostics.Add;
            fixture.ScopeFactory = lifetime =>
            {
                lifetime.Defer(() =>
                {
                    Assert(fixture.Host.IsCurrent, "constructor allocation cleanup belongs to the host");
                    cleanups++;
                    if (cleanupThrows) throw new InvalidOperationException("remaining cleanup failure");
                });
                var lease = lifetime.Defer(() =>
                {
                    Assert(fixture.Host.IsCurrent, "queued lease cleanup belongs to the host");
                    cleanups++;
                    if (cleanupThrows) throw new InvalidOperationException("queued cleanup failure");
                });
                if (cleanupThrows) lifetime.StoppingToken.Register(() => throw new InvalidOperationException("cancel cleanup failure"));
                fixture.Dispatch.HoldNextPost = true;
                Task.Run(lease.Dispose).GetAwaiter().GetResult();
                throw new InvalidOperationException("scope constructor failure");
            };
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "constructor-failure");
            fixture.Host.Drain();
            var terminalBeforeDrain = fixture.Outcomes.Any(outcome => outcome.Kind == "session");
            var retainedBeforeDrain = fixture.Parent.ActiveChildScopeCount;
            fixture.Dispatch.Release();
            fixture.Wait(launch);
            fixture.Until(() => fixture.Outcomes.Any(outcome => outcome.Kind == "session"));
            var terminal = fixture.Outcomes.Single(outcome => outcome.Kind == "session");
            var evidence = string.Join(";", diagnostics.Select(error => error.ToString()));
            var failures = new List<string>();
            if (terminalBeforeDrain) failures.Add("constructor failure published terminal outcome before queued scope cleanup drained");
            if (retainedBeforeDrain != 1 || fixture.Parent.ActiveChildScopeCount != 0)
                failures.Add("failed scope must retain parent ownership until drain then release it");
            if (cleanups != 2 || terminal.Status != "failed" || launch.Result.Succeeded)
                failures.Add("constructor failure must attempt every cleanup once and fail the session");
            foreach (var message in cleanupThrows
                ? new[] { "scope constructor failure", "remaining cleanup failure", "queued cleanup failure", "cancel cleanup failure" }
                : new[] { "scope constructor failure" })
                if (!evidence.Contains(message, StringComparison.Ordinal)
                    || terminal.Error == null || !terminal.Error.Message.Contains(message, StringComparison.Ordinal))
                    failures.Add("missing diagnostic or terminal failure evidence: " + message);
            try { fixture.Dispose(); } catch (Exception error) { failures.Add("fixture cleanup: " + error.Message); }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        }
        private static void CancellationDuringConstructionRetainsScope(string root)
        {
            using var fixture = new SessionFixture(root);
            var cleanups = 0;
            var providerConstructors = 0;
            var rejectedInitialization = false;
            fixture.Hosted.DiagnosticFailure += error => rejectedInitialization |= error.GetBaseException() is ObjectDisposedException;
            fixture.ScopeFactory = lifetime =>
            {
                lifetime.Defer(() => cleanups++);
                lifetime.Dispose();
            };
            SessionFixture.ProviderConstructor = () => providerConstructors++;
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "cancel-construction");
            fixture.Wait(launch);
            fixture.Until(() => fixture.Outcomes.Any(outcome => outcome.Kind == "session"));
            Assert(launch.Result.ErrorCode == ModErrorCode.Cancelled && providerConstructors == 0,
                "cancellation during context construction prevents provider activation");
            Assert(cleanups == 1 && fixture.Parent.ActiveChildScopeCount == 0
                && fixture.Outcomes.Single(outcome => outcome.Kind == "session").Status == "failed" && rejectedInitialization,
                "cancellation that rejects later constructor allocations must still clean and release the failed scope");
        }
        private static void CompletedConstructionReturnsOwnedScopeAfterCancellation(string root)
        {
            using var fixture = new SessionFixture(root);
            using var cancellation = new CancellationTokenSource();
            var cleanups = 0;
            fixture.ScopeFactory = lifetime => lifetime.Defer(() => cleanups++);
            var creation = fixture.Parent.CreateChildScopeAsync("completed", cancellation.Token, () => { },
                new NativeTransitionAccessSlot("completed:package", "completed", () => true), fixture.Host);
            Assert(creation.IsCompletedSuccessfully, "successful scope initialization returns its completed ownership task");
            cancellation.Cancel();
            fixture.Wait(creation);
            var scope = creation.Result;
            Assert(scope.Lifetime.IsStopping && fixture.Parent.ActiveChildScopeCount == 1 && cleanups == 0,
                "cancellation after successful creation cannot hide or dispose the returned scope before its caller owns it");
            scope.Dispose();
            fixture.Wait(scope.DrainRejectedResourcesAsync());
            Assert(cleanups == 1 && fixture.Parent.ActiveChildScopeCount == 0,
                "the caller can release a successfully created scope even after cancellation precedes its await");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
