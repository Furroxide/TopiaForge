using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Observes save failures without publishing their file-service text or changing the task's result.</summary>
    internal static class NetworkSaveTasks
    {
        /// <summary>
        /// Keeps a save's failure out of the global unobserved-exception event even if it lands after closing's wait
        /// expires. The caller still receives the original task, with its fault or cancellation unchanged. This is used
        /// only when queuing a save or closing, never by a frame that has nothing to save.
        /// </summary>
        internal static Task Observe(Task saved)
        {
            _ = saved.ContinueWith(failed =>
            {
                _ = failed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return saved;
        }
    }
}
