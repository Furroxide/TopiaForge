using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>One shared load of a bundle or a prefab, polled each frame. Releasing it disposes what it loaded, now or when it lands.</summary>
    internal sealed class WorldAssetLoad<T> where T : class, IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<OperationResult<T>>? task;
        private bool released;

        internal bool IsSettled => task == null || task.IsCompleted;

        internal bool Succeeded => Value != null;

        internal T? Value
        {
            get
            {
                if (released || task == null || task.Status != TaskStatus.RanToCompletion) return null;
                var result = task.Result;
                return result != null && result.TryGetValue(out var value) ? value : null;
            }
        }

        internal static WorldAssetLoad<T> Start(Func<CancellationToken, Task<OperationResult<T>>> start)
        {
            var load = new WorldAssetLoad<T>();
            try
            {
                load.task = start(load.cancellation.Token);
            }
            catch (Exception)
            {
                load.task = null;
            }

            return load;
        }

        /// <summary>
        /// Lets go of what the load holds. One still running is cancelled first: the engine's loader then lets go of whatever
        /// it loads as it lands, on the game thread. A continuation here could not promise that thread, since the loader runs
        /// what waits for a load elsewhere, and a handle let go of there stays loaded, so a later load of its bundle fails.
        /// </summary>
        internal void Release()
        {
            if (released) return;
            released = true;
            var current = task;
            if (current == null) return;
            if (!current.IsCompleted)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (Exception)
                {
                    // A loader whose cancellation throws is let go of below, like one that takes no notice of it.
                }
            }

            // A loader that takes no notice of cancellation hands back what it loaded anyway, and that goes as it lands.
            if (current.IsCompleted) DisposeResult(current);
            else current.ContinueWith(landed => DisposeResult(landed), TaskContinuationOptions.ExecuteSynchronously);
        }

        private static void DisposeResult(Task<OperationResult<T>> landed)
        {
            try
            {
                if (landed.Status == TaskStatus.RanToCompletion && landed.Result != null && landed.Result.TryGetValue(out var value)) value.Dispose();
            }
            catch (Exception)
            {
                // A handle that cannot be let go of goes with its mod's lifetime.
            }
        }
    }
}
