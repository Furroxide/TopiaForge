using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>The caller's result and irreversible native drain are deliberately independent.</summary>
    internal sealed class NativeSceneOperation : IInternalNativeSceneOperation, IInternalNativeSceneCompletion
    {
        private readonly SceneCoordinator coordinator;
        private readonly NativeTransitionReservation reservation;
        private readonly NativeSceneRequest request;
        private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> completion =
            new TaskCompletionSource<OperationResult<SceneSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> drained =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DateTime startedUtc = DateTime.UtcNow;
        private readonly CancellationToken token;
        private CancellationTokenRegistration cancellation;
        private bool beginning = true;
        private bool nativeFinished;
        private bool retired;
        private bool managedRequired;
        private bool managedFinished;
        private OperationResult<SceneSnapshot>? nativeResult;

        internal NativeSceneOperation(SceneCoordinator coordinator, NativeTransitionReservation reservation,
            string ownerPackageId, NativeSceneRequest request, CancellationToken token)
        {
            this.coordinator = coordinator;
            this.reservation = reservation;
            OwnerPackageId = ownerPackageId;
            this.request = request;
            this.token = token;
        }
        internal string OwnerPackageId { get; }
        public Task<OperationResult<SceneSnapshot>> Completion => completion.Task;
        public Task NativeDrained => drained.Task;
        public NativeSceneDispatchStatus DispatchStatus { get; private set; } = NativeSceneDispatchStatus.Indeterminate;
        internal ModErrorCode InitialErrorCode { get; private set; } = ModErrorCode.External;
        internal string InitialErrorMessage { get; private set; } = "The native adapter did not dispatch an operation.";

        internal void Begin(IInternalNativeSceneDispatch dispatch)
        {
            cancellation = token.Register(() => coordinator.Post(() => Cancel("The scene request was cancelled.")));
            try
            {
                if (token.IsCancellationRequested)
                {
                    DispatchStatus = NativeSceneDispatchStatus.NotDispatched;
                    Fail(ModErrorCode.Cancelled, "The transition was cancelled before native dispatch.");
                }
                else DispatchStatus = dispatch.Begin(this);
                if (DispatchStatus == NativeSceneDispatchStatus.NotDispatched)
                {
                    nativeFinished = true;
                    if (!completion.Task.IsCompleted) Fail(InitialErrorCode, InitialErrorMessage);
                }
            }
            catch (Exception error)
            {
                // Once adapter dispatch begins, a throw cannot prove that no native effect occurred.
                DispatchStatus = NativeSceneDispatchStatus.Indeterminate;
                Fail(ModErrorCode.External, "Native dispatch became uncertain: " + error.Message);
            }
            finally
            {
                beginning = false;
                PublishNativeCompletion();
                RetireIfFinished();
            }
        }

        public void FailCaller(ModErrorCode code, string message) => coordinator.Post(() => Fail(code, message));
        private void Fail(ModErrorCode code, string message)
        {
            if (retired) return;
            InitialErrorCode = code == ModErrorCode.None ? ModErrorCode.External : code;
            InitialErrorMessage = string.IsNullOrWhiteSpace(message) ? "The native operation failed." : message;
            completion.TrySetResult(OperationResult<SceneSnapshot>.Failure(InitialErrorCode, InitialErrorMessage));
        }
        internal void Cancel(string message) => Fail(ModErrorCode.Cancelled, message);

        public void NativeCompleted(OperationResult<SceneSnapshot> result) => coordinator.Post(() =>
        {
            if (retired || nativeFinished) return;
            nativeFinished = true;
            nativeResult = result;
            if (token.IsCancellationRequested) Cancel("The scene request was cancelled.");
            PublishNativeCompletion();
            RetireIfFinished();
        });

        public void RequireManagedCompletion()
        {
            coordinator.AssertCurrent();
            if (!beginning) throw new InvalidOperationException("A loader observer must attach during native dispatch.");
            managedRequired = true;
        }
        public void ManagedCompleted(OperationResult<bool> result) => coordinator.Post(() =>
        {
            if (retired || managedFinished) return;
            managedFinished = true;
            if (!result.Succeeded) Fail(result.ErrorCode, result.ErrorMessage);
            PublishNativeCompletion();
            RetireIfFinished();
        });

        private void PublishNativeCompletion()
        {
            if (beginning || !nativeFinished || (managedRequired && !managedFinished) || nativeResult == null) return;
            if (token.IsCancellationRequested) Cancel("The scene request was cancelled.");
            completion.TrySetResult(nativeResult);
        }

        internal void ObserveScene(SceneSnapshot scene)
        {
            if (!request.ObserveSceneArrival || retired || nativeFinished) return;
            if (string.Equals(scene.Name, request.SceneName, StringComparison.OrdinalIgnoreCase))
                NativeCompleted(OperationResult<SceneSnapshot>.Success(scene));
            // Unrelated arrivals do not retire an uncancellable older scene load.
        }
        internal void CheckTimeout(DateTime nowUtc, TimeSpan timeout)
        {
            if (!retired && (!nativeFinished || (managedRequired && !managedFinished)) && nowUtc - startedUtc >= timeout)
                Fail(ModErrorCode.External, "The native transition timed out; admission remains quarantined until it drains.");
        }
        private void RetireIfFinished()
        {
            if (beginning || !nativeFinished || (managedRequired && !managedFinished) || retired) return;
            retired = true;
            cancellation.Dispose();
            drained.TrySetResult(true);
            reservation.OperationDrained(this);
        }
    }
}
