using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Observes save failures without publishing their file-service text or changing the task's result. The identity's
    /// load, and the password key that waits for it, can fail with the file service's text too, so they are observed
    /// the same way.
    /// </summary>
    internal static class NetworkSaveTasks
    {
        /// <summary>
        /// Keeps a save's failure out of the global unobserved-exception event even if it lands after closing's wait
        /// expires, and a failed identity load's or password key's however hosting lets it go. The caller still
        /// receives the original task, with its fault or cancellation unchanged. This is used only when queuing a save
        /// or closing, and once for each load and key, never by a frame that has nothing to save.
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
