using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // Core scenes share the native executor. This adds owner cleanup evidence, not another transition.
    internal static class OwnerNativeSceneLoad
    {
        internal static Task<OperationResult<SceneSnapshot>> Start(IModLifetime lifetime, string description,
            Func<OperationResult<IInternalNativeSceneOperation>> dispatch, CancellationToken token)
        {
            if (lifetime.IsStopping || token.IsCancellationRequested) return Refused(ModErrorCode.Cancelled, "The scene owner or caller has stopped.");
            if (!(lifetime is IInternalNativeWorkLifetime nativeLifetime))
                return Refused(ModErrorCode.Unavailable, "The scene owner cannot retain native work.");
            AssetNativeWorkTicket ticket;
            try { ticket = nativeLifetime.RegisterNativeWork(description); }
            catch (ObjectDisposedException) { return Refused(ModErrorCode.Cancelled, "The scene owner has stopped."); }
            OperationResult<IInternalNativeSceneOperation> result;
            try { result = dispatch(); }
            catch (Exception error)
            {
                // The shared executor retains any indeterminate native dispatch and returns its operation.
                // A throw here occurs before such an operation was accepted (for example invalid arguments).
                ticket.Complete();
                return Refused(ModErrorCode.External, "The scene request could not be accepted: " + error.Message);
            }
            if (!result.TryGetValue(out var operation))
            {
                ticket.Complete();
                return Refused(result.ErrorCode, result.ErrorMessage);
            }
            _ = ObserveAsync(operation, ticket);
            return operation.Completion;
        }
        private static Task<OperationResult<SceneSnapshot>> Refused(ModErrorCode code, string message) =>
            Task.FromResult(OperationResult<SceneSnapshot>.Failure(code, message));

        private static async Task ObserveAsync(IInternalNativeSceneOperation operation, AssetNativeWorkTicket ticket)
        {
            var failures = new List<Exception>();
            try { await operation.NativeDrained.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try
            {
                var terminal = await operation.NativeCompletion.ConfigureAwait(false);
                var caller = operation.Completion.Status == TaskStatus.RanToCompletion ? operation.Completion.Result : null;
                if (!terminal.Succeeded && terminal.ErrorCode != ModErrorCode.Cancelled
                    && (caller == null || caller.ErrorCode != terminal.ErrorCode
                        || !string.Equals(caller.ErrorMessage, terminal.ErrorMessage, StringComparison.Ordinal)))
                    failures.Add(new InvalidOperationException("Native scene work failed after its caller outcome: "
                        + terminal.ErrorCode + ": " + terminal.ErrorMessage));
            }
            catch (Exception error) { failures.Add(error); }
            ticket.Complete(failures.Count == 0 ? null : new AggregateException("Core scene work failed to drain cleanly.", failures));
        }
    }
}
