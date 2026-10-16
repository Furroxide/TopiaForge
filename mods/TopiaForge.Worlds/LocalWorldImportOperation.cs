using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal static class LocalWorldImportOperation
    {
        internal static async Task<OperationResult<IDisposable>> RunAsync(IInternalSceneTransitionService transitions,
            WorldSceneIdentity scene, CancellationToken token, ILocalImportTransaction transaction)
        {
            IDisposable? content = null;
            var errors = new List<Exception>();
            var snapshot = new SceneSnapshot(scene.Name, true, true);
            var dispatched = transitions.TryDispatch(new NativeSceneRequest(scene.Name, false, "local content import", observeSceneArrival: false),
                new DelegateNativeSceneDispatch(completion =>
                {
                    OperationResult<IDisposable> result;
                    try { result = transaction.Import(); }
                    catch (Exception error) { errors.Add(error); result = Failure(ModErrorCode.External, error.Message); }
                    if (result.TryGetValue(out var owned)) content = owned;
                    var outcome = result.Succeeded ? OperationResult<SceneSnapshot>.Success(snapshot)
                        : OperationResult<SceneSnapshot>.Failure(result.ErrorCode, result.ErrorMessage);
                    if (transaction.NativeReturned) completion.NativeCompleted(outcome);
                    else completion.FailCaller(result.ErrorCode, result.ErrorMessage);
                    return transaction.NativeReturned ? NativeSceneDispatchStatus.Dispatched
                        : transaction.NativeEntered ? NativeSceneDispatchStatus.Indeterminate : NativeSceneDispatchStatus.NotDispatched;
                }), token);
            if (!dispatched.TryGetValue(out var operation)) return Failure(dispatched.ErrorCode, dispatched.ErrorMessage);
            OperationResult<SceneSnapshot>? acknowledged = null;
            OperationResult<SceneSnapshot>? terminal = null;
            try { acknowledged = await operation.Completion; } catch (Exception error) { errors.Add(error); }
            try { await operation.NativeDrained; } catch (Exception error) { errors.Add(error); }
            try { terminal = await operation.NativeCompletion; } catch (Exception error) { errors.Add(error); }
            if (acknowledged?.Succeeded == true && terminal?.Succeeded == true && !token.IsCancellationRequested && content != null && errors.Count == 0)
                return OperationResult<IDisposable>.Success(content);
            try { content?.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) throw new AggregateException("Local content native mutation, drain or cleanup failed.", errors);
            if (terminal != null && !terminal.Succeeded && terminal.ErrorCode != ModErrorCode.Cancelled && acknowledged?.ErrorCode == ModErrorCode.Cancelled)
                throw new AggregateException("The cancelled local import later failed.", new InvalidOperationException(terminal.ErrorMessage));
            if (token.IsCancellationRequested) return Failure(ModErrorCode.Cancelled, "Local import was cancelled.");
            if (terminal != null && !terminal.Succeeded) return Failure(terminal.ErrorCode, terminal.ErrorMessage);
            if (acknowledged != null && !acknowledged.Succeeded) return Failure(acknowledged.ErrorCode, acknowledged.ErrorMessage);
            return Failure(ModErrorCode.External, "The native import completed without owned content.");
        }
        private static OperationResult<IDisposable> Failure(ModErrorCode code, string message) => OperationResult<IDisposable>.Failure(code, message);
    }
}
