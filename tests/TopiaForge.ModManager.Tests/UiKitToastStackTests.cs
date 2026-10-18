using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The toast stack's bookkeeping: a toast shows at once in a free view and rests below the toasts above it, however tall
    /// each is; at most four show and the rest wait; the waiting queue drops its oldest when full; and a view that takes a new
    /// toast is always presented fresh. Before, a view was presented fresh only while it still sat at its creation spot, so
    /// every toast after a view's first one kept the last exit's fade and slide: invisible, wherever it was raised. And every
    /// toast was one line high, so a longer result lost its second sentence, which says what to do next.
    /// </summary>
    internal static class UiKitToastStackTests
    {
        private const float Gap = 8f;

        public static void Run()
        {
            AToastShowsAtOnceInAFreshView();
            AReusedViewShowsFresh();
            AtMostFourShowAndTheRestWaitInOrder();
            TallerToastsPushTheOnesBelowDown();
            TheWaitingQueueDropsItsOldestWhenFull();
            OnlyShownToastsCountDown();
            ReleasesOutsideTheStackAreIgnored();
            Console.WriteLine("UiKitToastStackTests passed.");
        }

        private static void AToastShowsAtOnceInAFreshView()
        {
            var stack = new TopiaForgeToastStack<string>(4, 64, Gap);
            Assert(!stack.TryShowNext(out _, out _), "nothing shows before a toast is queued");
            Assert(!stack.Enqueue("Saved.", 3.5f), "a toast queues without dropping anything");
            Assert(stack.TryShowNext(out var view, out var toast) && view == 0 && toast == "Saved.", "the toast takes the first view");
            stack.SetHeight(view, 44f);
            Assert(stack.TryPlace(view, out var top, out var fresh) && top == 0f && fresh, "and rests at the top, fresh, to be brought in");
            Assert(stack.TryPlace(view, out top, out fresh) && top == 0f && !fresh, "once placed it is no longer fresh, so restacking only moves it");
        }

        private static void AReusedViewShowsFresh()
        {
            var stack = new TopiaForgeToastStack<string>(4, 64, Gap);
            var expired = new List<int>();
            stack.Enqueue("First.", 1f);
            stack.TryShowNext(out var first, out _);
            stack.TryPlace(first, out _, out _);
            stack.Expire(1f, expired);
            Assert(expired.Count == 1 && expired[0] == first && !stack.TryPlace(first, out _, out _), "an expired toast leaves and rests nowhere");
            stack.Enqueue("Next.", 1f);
            Assert(stack.TryShowNext(out var other, out _) && other != first && stack.ViewCount == 2, "a leaving view takes no new toast before its exit ends; another view does");

            var again = new TopiaForgeToastStack<string>(1, 64, Gap);
            again.Enqueue("One.", 1f);
            again.TryShowNext(out var only, out _);
            again.TryPlace(only, out _, out _);
            again.Expire(2f, expired);
            again.Release(only);
            again.Enqueue("Copied the invite code.", 3.5f);
            Assert(again.TryShowNext(out var reused, out var toast) && reused == only && again.ViewCount == 1 && toast == "Copied the invite code.",
                "the next toast reuses the freed view");
            Assert(again.TryPlace(reused, out var top, out var fresh) && top == 0f && fresh,
                "and is presented fresh, placed and brought in, not left as the last exit left it");
            Assert(again.TryPlace(reused, out _, out fresh) && !fresh, "once");
        }

        private static void AtMostFourShowAndTheRestWaitInOrder()
        {
            var stack = new TopiaForgeToastStack<string>(4, 64, Gap);
            var expired = new List<int>();
            var durations = new[] { 5f, 1f, 5f, 5f, 5f, 5f };
            for (var index = 0; index < durations.Length; index++) stack.Enqueue("Toast " + index, durations[index]);
            var shown = new List<string>();
            while (stack.TryShowNext(out var view, out var toast))
            {
                shown.Add(view + ":" + toast);
                stack.SetHeight(view, 44f);
                Assert(stack.TryPlace(view, out var top, out var fresh) && top == view * (44f + Gap) && fresh, "toast " + view + " rests below the ones before it");
            }

            Assert(string.Join("|", shown) == "0:Toast 0|1:Toast 1|2:Toast 2|3:Toast 3" && stack.WaitingCount == 2, "four show, in order, and two wait");
            stack.Expire(1f, expired);
            Assert(expired.Count == 1 && expired[0] == 1, "the shortest toast leaves first");
            Assert(stack.TryPlace(2, out var moved, out var stillFresh) && moved == 44f + Gap && !stillFresh &&
                   stack.TryPlace(3, out var last, out _) && last == 2 * (44f + Gap), "the toasts below a leaving one move up and are not presented again");
            Assert(!stack.TryShowNext(out _, out _), "nothing new shows while the leaving toast holds its view");
            stack.Release(1);
            Assert(stack.TryShowNext(out var freed, out var next) && freed == 1 && next == "Toast 4" && stack.ViewCount == 4,
                "its view takes the oldest waiting toast once the exit ends");
            stack.SetHeight(freed, 44f);
            Assert(stack.TryPlace(freed, out var freedTop, out var freedFresh) && freedTop == 44f + Gap && freedFresh, "presented fresh where it now rests");
        }

        private static void TallerToastsPushTheOnesBelowDown()
        {
            var stack = new TopiaForgeToastStack<string>(4, 64, Gap);
            foreach (var text in new[] { "Short.", "A result in three lines.", "Short again." }) stack.Enqueue(text, 5f);
            var heights = new[] { 44f, 80f, 44f };
            for (var index = 0; index < heights.Length; index++)
            {
                stack.TryShowNext(out var view, out _);
                stack.SetHeight(view, heights[index]);
            }

            Assert(stack.TryPlace(0, out var first, out _) && stack.TryPlace(1, out var second, out _) && stack.TryPlace(2, out var third, out _) &&
                   first == 0f && second == 44f + Gap && third == 44f + Gap + 80f + Gap, "each toast rests below the one above it, however tall that one is");
            stack.SetHeight(1, float.NaN);
            stack.SetHeight(7, 100f);
            Assert(stack.TryPlace(2, out third, out _) && third == 44f + Gap + 0f + Gap, "a height that is not a number counts as none, and a view the stack lacks is ignored");
        }

        private static void TheWaitingQueueDropsItsOldestWhenFull()
        {
            var stack = new TopiaForgeToastStack<int>(1, 3, Gap);
            Assert(!stack.Enqueue(1, 1f) && !stack.Enqueue(2, 1f) && !stack.Enqueue(3, 1f), "the queue holds three");
            Assert(stack.Enqueue(4, 1f) && stack.WaitingCount == 3, "a fourth drops the oldest");
            Assert(stack.TryShowNext(out _, out var shown) && shown == 2, "so the oldest left is the next to show");
            AssertThrows(() => _ = new TopiaForgeToastStack<int>(0, 1, Gap), "a stack shows at least one toast");
            AssertThrows(() => _ = new TopiaForgeToastStack<int>(1, 0, Gap), "and queues at least one");
            AssertThrows(() => _ = new TopiaForgeToastStack<int>(1, 1, -1f), "and keeps a gap of zero or more");
        }

        private static void OnlyShownToastsCountDown()
        {
            var stack = new TopiaForgeToastStack<string>(1, 64, Gap);
            var expired = new List<int>();
            stack.Enqueue("Shown.", 2f);
            stack.Enqueue("Waiting.", 2f);
            stack.TryShowNext(out var view, out _);
            stack.Expire(1.5f, expired);
            Assert(expired.Count == 0 && stack.TryPlace(view, out _, out _), "time counts down while a toast shows");
            stack.Expire(1f, expired);
            Assert(expired.Count == 1, "and it leaves once its own time is up");
            stack.Expire(5f, expired);
            Assert(expired.Count == 0, "a leaving toast is not dismissed twice");
            stack.Release(view);
            stack.TryShowNext(out view, out var toast);
            stack.Expire(1.9f, expired);
            Assert(toast == "Waiting." && expired.Count == 0, "a toast that waited starts its full time when it shows");

            // Every frame counts time down, so a frame that ends nothing allocates nothing.
            var steady = new TopiaForgeToastStack<string>(4, 64, Gap);
            steady.Enqueue("Steady.", 30f);
            steady.TryShowNext(out _, out _);
            var allocated = Seam.Allocated(() => steady.Expire(0.0001f, expired), 1000);
            Assert(allocated == 0, "counting down allocates nothing (" + allocated + " bytes)");
        }

        private static void ReleasesOutsideTheStackAreIgnored()
        {
            var stack = new TopiaForgeToastStack<string>(4, 64, Gap);
            stack.Release(-1);
            stack.Release(7);
            Assert(!stack.TryPlace(-1, out _, out _) && !stack.TryPlace(7, out _, out _), "a view the stack does not hold rests nowhere");
            stack.Enqueue("One.", 1f);
            stack.TryShowNext(out _, out _);
            stack.Clear();
            Assert(stack.ViewCount == 0 && stack.WaitingCount == 0 && !stack.TryPlace(0, out _, out _), "clearing forgets every view and waiting toast");
            stack.Release(0);
        }

        private static void AssertThrows(Action action, string message)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
