using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScopedCleanupReentrancyTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var throws in new[] { false, true })
                try { Check(root, throws); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private static void Check(string root, bool throws)
        {
            using var fixture = new SessionFixture(root + "-" + throws);
            var cleanup = 0;
            var terminalCleanupCount = -1;
            SessionFixture.Start = (session, _) =>
            {
                SessionFixture.ModeSession = session;
                session.Lifetime.Defer(() => cleanup++);
                session.Lifetime.Defer(() =>
                {
                    try
                    {
                        session.Lifetime.Track(new Resource(() =>
                        {
                            cleanup++;
                            Assert(fixture.Host.IsCurrent, "reentrant resource cleanup must use the host thread");
                            if (throws) throw new InvalidOperationException("late cleanup failed");
                        }));
                    }
                    catch (ObjectDisposedException) { }
                });
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController()));
            };
            fixture.Hosted.Outcome += outcome =>
            {
                if (outcome.Kind == "session") terminalCleanupCount = cleanup;
            };
            fixture.Launch();
            var stop = SessionFixture.ModeSession!.StopAsync();
            fixture.Wait(stop);
            fixture.Until(() => fixture.Outcomes.Any(outcome => outcome.Kind == "session"));
            var terminal = fixture.Outcomes.Single(outcome => outcome.Kind == "session");
            Assert(terminalCleanupCount == 2 && cleanup == 2,
                "every resource submitted during scope disposal must drain before terminal publication");
            Assert(terminal.Status == (throws ? "failed" : "succeeded"),
                "reentrant disposal failure must be aggregated into the terminal outcome");
            Assert(fixture.Parent.ActiveChildScopeCount == 0, "final cleanup releases parent scope ownership");
        }
        private sealed class Resource : IDisposable
        {
            private Action? cleanup;
            internal Resource(Action cleanup) { this.cleanup = cleanup; }
            public void Dispose() { var action = cleanup; cleanup = null; action?.Invoke(); }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
