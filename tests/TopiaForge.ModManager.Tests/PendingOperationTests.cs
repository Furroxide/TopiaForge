using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Custom-world content creation used to be waited on from Unity's sceneLoaded callback. Because the SDK's
    /// asset tasks only complete from a main-thread AssetBundleCreateRequest callback, that blocked the engine's
    /// update pump and hung Robotopia permanently. These tests pin the drain-based replacement: never wait,
    /// always release late content on the caller's thread, and stay armed for a relaunch.
    /// </summary>
    internal static class PendingOperationTests
    {
        private const float Timeout = 30f;

        public static void Run()
        {
            WaitsWithoutBlocking();
            CompletesWithTheCreatedContent();
            ConvertsAFaultIntoAFailure();
            CancelReleasesLateContentToTheCaller();
            CancelFreesTheSlotForARelaunch();
            TimesOutOnceThenKeepsDraining();
            ForgetDropsEverything();
            Console.WriteLine("PendingOperationTests passed.");
        }

        private static void WaitsWithoutBlocking()
        {
            var load = new PendingOperation<IWorldContent>();
            var completion = new TaskCompletionSource<OperationResult<IWorldContent>>();
            load.Begin(_ => completion.Task, CancellationToken.None, now: 0f);

            Assert(load.Poll(1f, Timeout, out _) == PendingOperationState.Waiting,
                "an unfinished creation must report Waiting rather than block");
            Assert(load.IsInFlight, "an unfinished creation stays armed");
        }

        private static void CompletesWithTheCreatedContent()
        {
            var load = new PendingOperation<IWorldContent>();
            var content = new FakeWorldContent();
            load.Begin(
                _ => Task.FromResult(OperationResult<IWorldContent>.Success(content)),
                CancellationToken.None,
                now: 0f);

            Assert(load.Poll(0f, Timeout, out var result) == PendingOperationState.Completed,
                "a finished creation must report Completed");
            Assert(result.TryGetValue(out var created) && ReferenceEquals(created, content),
                "the caller must receive the created content");
            Assert(!content.Disposed, "wanted content must not be released by the drain");
            Assert(load.Poll(0f, Timeout, out _) == PendingOperationState.Idle,
                "a released creation must leave the slot idle");
        }

        private static void ConvertsAFaultIntoAFailure()
        {
            var load = new PendingOperation<IWorldContent>();
            load.Begin(
                _ => Task.FromException<OperationResult<IWorldContent>>(new InvalidOperationException("bundle is corrupt")),
                CancellationToken.None,
                now: 0f);

            Assert(load.Poll(0f, Timeout, out var result) == PendingOperationState.Completed,
                "a faulted creation must still resolve so the caller can fall back");
            Assert(!result.Succeeded && result.ErrorMessage.Contains("bundle is corrupt", StringComparison.Ordinal),
                "a fault must surface as a failure result, not an escaping exception");
        }

        private static void CancelReleasesLateContentToTheCaller()
        {
            var load = new PendingOperation<IWorldContent>();
            var completion = new TaskCompletionSource<OperationResult<IWorldContent>>();
            var observed = CancellationToken.None;
            load.Begin(
                token =>
                {
                    observed = token;
                    return completion.Task;
                },
                CancellationToken.None,
                now: 0f);

            load.Cancel();
            Assert(observed.IsCancellationRequested, "cancelling must signal the token handed to the SDK");

            // The SDK can still hand back live content after cancellation. It owns Unity objects, so it must be
            // surfaced to the caller for release on the main thread rather than silently dropped.
            var content = new FakeWorldContent();
            completion.SetResult(OperationResult<IWorldContent>.Success(content));
            Assert(load.Poll(1f, Timeout, out var result) == PendingOperationState.Abandoned,
                "late content from a cancelled creation must be reported as Abandoned");
            Assert(result.TryGetValue(out var orphan) && ReferenceEquals(orphan, content),
                "the caller must receive the orphaned content so it can release it");
            Assert(load.Poll(1f, Timeout, out _) == PendingOperationState.Idle,
                "a drained abandonment must leave the slot idle");
        }

        private static void CancelFreesTheSlotForARelaunch()
        {
            var load = new PendingOperation<IWorldContent>();
            var first = new TaskCompletionSource<OperationResult<IWorldContent>>();
            load.Begin(_ => first.Task, CancellationToken.None, now: 0f);
            load.Cancel();

            // Relaunching a world must not have to wait on the creation the previous session discarded.
            var second = new FakeWorldContent();
            load.Begin(
                _ => Task.FromResult(OperationResult<IWorldContent>.Success(second)),
                CancellationToken.None,
                now: 1f);
            Assert(load.IsInFlight, "a relaunch must arm immediately while the discarded creation drains");

            // The abandoned creation is reported first so its content is released promptly.
            first.SetResult(OperationResult<IWorldContent>.Success(new FakeWorldContent()));
            Assert(load.Poll(1f, Timeout, out _) == PendingOperationState.Abandoned,
                "the discarded creation drains ahead of the armed one");
            Assert(load.Poll(1f, Timeout, out var result) == PendingOperationState.Completed
                   && result.TryGetValue(out var created) && ReferenceEquals(created, second),
                "the relaunched creation must still resolve normally");
        }

        private static void TimesOutOnceThenKeepsDraining()
        {
            var load = new PendingOperation<IWorldContent>();
            var completion = new TaskCompletionSource<OperationResult<IWorldContent>>();
            load.Begin(_ => completion.Task, CancellationToken.None, now: 0f);

            Assert(load.Poll(Timeout - 0.1f, Timeout, out _) == PendingOperationState.Waiting,
                "a creation inside its budget keeps waiting");
            Assert(load.Poll(Timeout, Timeout, out _) == PendingOperationState.TimedOut,
                "a creation past its budget must report TimedOut so the caller can fall back");
            Assert(load.Poll(Timeout + 1f, Timeout, out _) == PendingOperationState.Waiting,
                "a timed-out creation must be reported once, then keep draining");

            var content = new FakeWorldContent();
            completion.SetResult(OperationResult<IWorldContent>.Success(content));
            Assert(load.Poll(Timeout + 2f, Timeout, out var result) == PendingOperationState.Abandoned
                   && result.TryGetValue(out var orphan) && ReferenceEquals(orphan, content),
                "content arriving after a timeout must still reach the caller for release");
        }

        private static void ForgetDropsEverything()
        {
            var load = new PendingOperation<IWorldContent>();
            var completion = new TaskCompletionSource<OperationResult<IWorldContent>>();
            var observed = CancellationToken.None;
            load.Begin(
                token =>
                {
                    observed = token;
                    return completion.Task;
                },
                CancellationToken.None,
                now: 0f);

            load.Forget();
            Assert(observed.IsCancellationRequested, "forgetting must still cancel the SDK work");
            Assert(!load.IsInFlight && load.Poll(1f, Timeout, out _) == PendingOperationState.Idle,
                "forgetting must leave nothing armed or draining");
        }

        private sealed class FakeWorldContent : IWorldContent
        {
            public bool Disposed { get; private set; }

            public IEntity Root => throw new NotSupportedException("The drain never inspects content.");

            public bool IsAlive => !Disposed;

            public void Dispose() => Disposed = true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Pending world content load: " + message);
            }
        }
    }
}
