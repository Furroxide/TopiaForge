using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Robotopia.Worlds;

namespace Robotopia.ModManager.Tests
{
    internal static class MainThreadDispatchQueueTests
    {
        public static void Run()
        {
            DeliversWorkerCompletionOnlyFromDrainThread();
            DisposeDropsPendingAndFutureCompletions();
            DisposeDuringDispatchDropsRemainingCompletions();
            Console.WriteLine("MainThreadDispatchQueueTests passed.");
        }

        private static void DeliversWorkerCompletionOnlyFromDrainThread()
        {
            var queue = new MainThreadDispatchQueue<int>();
            var mainThread = Environment.CurrentManagedThreadId;
            var deliveryThread = -1;
            var delivered = new List<int>();

            Task.Run(() => queue.TryEnqueue(42)).GetAwaiter().GetResult();
            Assert(delivered.Count == 0, "worker completion must remain queued before the main-thread drain");

            queue.Drain(value =>
            {
                deliveryThread = Environment.CurrentManagedThreadId;
                delivered.Add(value);
            });

            Assert(delivered.Count == 1 && delivered[0] == 42, "drain should deliver the queued completion exactly once");
            Assert(deliveryThread == mainThread, "completion must be delivered on the thread that owns the drain");
        }

        private static void DisposeDropsPendingAndFutureCompletions()
        {
            var queue = new MainThreadDispatchQueue<string>();
            Assert(queue.TryEnqueue("pending"), "live queue should accept a completion");

            queue.Dispose();

            Assert(!queue.TryEnqueue("late"), "disposed queue must reject a late completion");
            var delivered = 0;
            queue.Drain(_ => delivered++);
            Assert(delivered == 0, "dispose must discard callbacks from both pending and late completions");
        }

        private static void DisposeDuringDispatchDropsRemainingCompletions()
        {
            var queue = new MainThreadDispatchQueue<int>();
            queue.TryEnqueue(1);
            queue.TryEnqueue(2);
            var delivered = new List<int>();

            queue.Drain(value =>
            {
                delivered.Add(value);
                queue.Dispose();
            });

            Assert(delivered.Count == 1 && delivered[0] == 1,
                "disposing from an unload callback must prevent later queued callbacks from running");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
