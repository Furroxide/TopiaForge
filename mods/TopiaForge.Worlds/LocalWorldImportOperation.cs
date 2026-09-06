using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    /// <summary>Runs a local content import under the same admission and native drain as scene loads.</summary>
    internal static class LocalWorldImportOperation
    {
        internal static OperationResult<bool> Run(IInternalSceneTransitionService transitions,
            SceneSnapshot scene, CancellationToken token, Func<Action, OperationResult<bool>> import)
        {
            var dispatched = transitions.TryDispatch(
                new NativeSceneRequest(scene.Name, false, "local world import", observeSceneArrival: false),
                new DelegateNativeSceneDispatch(completion =>
                {
                    var entered = false;
                    var result = import(() => entered = true);
                    if (result.Succeeded)
                    {
                        completion.NativeCompleted(OperationResult<SceneSnapshot>.Success(scene));
                        return NativeSceneDispatchStatus.Dispatched;
                    }
                    completion.FailCaller(result.ErrorCode, result.ErrorMessage);
                    return entered ? NativeSceneDispatchStatus.Indeterminate : NativeSceneDispatchStatus.NotDispatched;
                }), token);
            if (!dispatched.TryGetValue(out var operation))
                return Failure(dispatched.ErrorCode, dispatched.ErrorMessage);
            if (!operation.Completion.IsCompleted)
                return Failure(ModErrorCode.External, "The native importer has not confirmed completion.");
            var outcome = operation.Completion.GetAwaiter().GetResult();
            return outcome.Succeeded ? OperationResult<bool>.Success(true)
                : Failure(outcome.ErrorCode, outcome.ErrorMessage);
        }

        private static OperationResult<bool> Failure(ModErrorCode code, string message) =>
            OperationResult<bool>.Failure(code, message);
    }
}
