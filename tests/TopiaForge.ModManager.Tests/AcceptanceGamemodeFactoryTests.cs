using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.SdkAcceptance;

namespace TopiaForge.ModManager.Tests
{
    internal static class AcceptanceGamemodeFactoryTests
    {
        internal static async Task RunAsync()
        {
            var failures = new List<Exception>();
            foreach (var test in new Func<Task>[]
            {
                NullSessionIsRejected,
                SessionCancellationPrecedesProbeLookup,
                CallerCancellationPrecedesProbeLookup,
                StoppedOwnerPrecedesProbeLookup,
                MissingProbeRemainsUnavailable
            })
            {
                try { await test(); }
                catch (Exception error) { failures.Add(new Exception(test.Method.Name + ": " + error.Message, error)); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
            Console.WriteLine("Acceptance gamemode factory tests passed (5 cases).");
        }

        private static async Task NullSessionIsRejected()
        {
            try { await new AcceptanceGamemodeFactory().StartAsync(null!, CancellationToken.None); }
            catch (ArgumentNullException error) when (error.ParamName == "session") { return; }
            throw new Exception("The actual acceptance factory must reject a null session with its parameter name.");
        }

        private static async Task SessionCancellationPrecedesProbeLookup()
        {
            using var context = NewContext();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert(!context.Lifetime.IsStopping, "Session cancellation must be independent of owner disposal.");
            var resourcesBefore = context.Lifetime.TrackedResourceCount;
            var result = await new AcceptanceGamemodeFactory().StartAsync(
                NewSession(context, cancellation.Token), CancellationToken.None);
            Assert(result.ErrorCode == ModErrorCode.Cancelled,
                "A cancelled session must return Cancelled even when the owner is live and the caller token is not cancelled.");
            Assert(context.Lifetime.TrackedResourceCount == resourcesBefore, "Cancelled startup must not allocate acceptance resources.");
        }

        private static async Task CallerCancellationPrecedesProbeLookup()
        {
            using var context = NewContext();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = await new AcceptanceGamemodeFactory().StartAsync(NewSession(context), cancellation.Token);
            Assert(result.ErrorCode == ModErrorCode.Cancelled, "Caller cancellation must remain Cancelled.");
        }

        private static async Task StoppedOwnerPrecedesProbeLookup()
        {
            using var context = NewContext();
            var session = NewSession(context);
            context.Lifetime.Dispose();
            var result = await new AcceptanceGamemodeFactory().StartAsync(session, CancellationToken.None);
            Assert(result.ErrorCode == ModErrorCode.Cancelled, "An owner that has stopped cannot begin an acceptance probe.");
        }

        private static async Task MissingProbeRemainsUnavailable()
        {
            using var context = NewContext();
            var result = await new AcceptanceGamemodeFactory().StartAsync(NewSession(context), CancellationToken.None);
            Assert(result.ErrorCode == ModErrorCode.Unavailable, "A live session without its owning probe must remain Unavailable.");
        }

        private static FakeModContext NewContext() => new FakeModContext(
            new ModIdentity("dev.topiaforge.sdk-acceptance", "SDK Acceptance", SemanticVersion.Parse("0.1.0-rc.1")));

        private static FakeGamemodeSession NewSession(FakeModContext context, CancellationToken token = default) =>
            new FakeGamemodeSession(context, new WorldReadiness(new WorldSceneIdentity(1, "acceptance-world"), TransformState.Identity),
                cancellationToken: token);

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
