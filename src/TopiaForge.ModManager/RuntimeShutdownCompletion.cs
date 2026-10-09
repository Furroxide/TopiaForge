using System;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Keeps final plugin cleanup owned by the process host after Unity's synchronous OnDestroy returns.</summary>
    internal static class RuntimeShutdownCompletion
    {
        internal static Task Observe(IHostDispatcher dispatcher, Task<OperationResult<bool>> shutdown,
            Action<OperationResult<bool>> complete, Action<Exception> report)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (shutdown == null) throw new ArgumentNullException(nameof(shutdown));
            if (complete == null) throw new ArgumentNullException(nameof(complete));
            if (report == null) throw new ArgumentNullException(nameof(report));
            return dispatcher.InvokeCallbackAsync(async () =>
            {
                OperationResult<bool> outcome;
                try { outcome = await shutdown; }
                catch (Exception exception)
                {
                    Report(report, exception);
                    outcome = OperationResult<bool>.Failure(ModErrorCode.External, "Runtime shutdown failed; see manager logs.");
                }
                try { complete(outcome); }
                catch (Exception exception) { Report(report, exception); }
                return true;
            });
        }

        private static void Report(Action<Exception> report, Exception exception)
        {
            try { report(exception); }
            catch { /* A stopped diagnostic sink cannot interrupt independent teardown. */ }
        }
    }
}
