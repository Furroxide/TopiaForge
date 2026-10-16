using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class LocalImportOperationTests
    {
        internal static async Task RunAsync()
        {
            await HiddenTerminalFailureKeepsCode();
            await DrainFailureStillReleasesContent();
            await CancellationReleasesReturnedContent();
            await IndeterminateMutationKeepsNativeOwnership();
            Console.WriteLine("Local import native ownership tests passed (4 cases).");
        }
        private static async Task DrainFailureStillReleasesContent()
        {
            var owned = new Owned(); var transition = new Transitions { DrainFailure = true };
            try { await Run(transition, new Transaction(owned)); throw new Exception("Expected drain failure."); }
            catch (Exception error) { Assert(error.Message.Contains("drain"), "The native drain failure must survive."); }
            Assert(owned.Disposals == 1, "A failed native drain must still release returned imported content.");
        }
        private static async Task HiddenTerminalFailureKeepsCode()
        {
            var owned = new Owned(); var transition = new Transitions { TerminalFailure = true };
            var result = await Run(transition, new Transaction(owned));
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.External && result.ErrorMessage == "late native import failure",
                "A later native failure must not become an error with code None.");
            Assert(owned.Disposals == 1, "An invalid terminal import result must release its content.");
        }
        private static async Task CancellationReleasesReturnedContent()
        {
            using var cancellation = new CancellationTokenSource();
            var owned = new Owned(); var transition = new Transitions();
            var transaction = new Transaction(owned) { DuringImport = cancellation.Cancel };
            var result = await LocalWorldImportOperation.RunAsync(transition, new WorldSceneIdentity(1, "world"), cancellation.Token, transaction);
            Assert(result.ErrorCode == ModErrorCode.Cancelled && owned.Disposals == 1, "Cancelled import must clean a native result returned after cancellation.");
        }
        private static async Task IndeterminateMutationKeepsNativeOwnership()
        {
            var transition = new Transitions();
            var transaction = new Transaction(new Owned()) { Throw = true, NativeReturned = false };
            var task = Run(transition, transaction);
            Assert(!task.IsCompleted && transition.Operation.DispatchStatus == NativeSceneDispatchStatus.Indeterminate,
                "Unknown native mutation must retain ownership after caller failure.");
            transition.Operation.NativeCompleted(OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "late import fault"));
            try { await task; throw new Exception("Expected mutation failure."); }
            catch (AggregateException error) { Assert(error.ToString().Contains("mutation"), "Native mutation failure must survive eventual drain."); }
        }
        private static Task<OperationResult<IDisposable>> Run(Transitions transitions, Transaction transaction) =>
            LocalWorldImportOperation.RunAsync(transitions, new WorldSceneIdentity(1, "world"), CancellationToken.None, transaction);
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private sealed class Owned : IDisposable { internal int Disposals; public void Dispose() { Disposals++; } }
        private sealed class Transaction : ILocalImportTransaction
        {
            private readonly Owned owned;
            internal Transaction(Owned owned) { this.owned = owned; }
            internal Action? DuringImport; internal bool Throw;
            public bool NativeEntered => true;
            public bool NativeReturned { get; set; } = true;
            public OperationResult<IDisposable> Import()
            {
                DuringImport?.Invoke();
                if (Throw) throw new InvalidOperationException("mutation failed");
                return OperationResult<IDisposable>.Success(owned);
            }
            public void Dispose() { }
        }
        private sealed class Transitions : IInternalSceneTransitionService
        {
            internal bool DrainFailure; internal bool TerminalFailure;
            internal readonly Operation Operation = new Operation();
            public bool IsBusy => !Operation.NativeDrained.IsCompleted;
            public OperationResult<IInternalSceneTransitionLease> Acquire(string name, bool automatic, string reason) => throw new NotSupportedException();
            public OperationResult<IInternalNativeSceneOperation> TryDispatch(NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken token = default)
            {
                Operation.DrainFailure = DrainFailure; Operation.TerminalFailure = TerminalFailure;
                token.Register(() => Operation.FailCaller(ModErrorCode.Cancelled, "cancelled"));
                Operation.DispatchStatus = dispatch.Begin(Operation);
                return OperationResult<IInternalNativeSceneOperation>.Success(Operation);
            }
        }
        private sealed class Operation : IInternalNativeSceneOperation, IInternalNativeSceneCompletion
        {
            private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> caller = new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> native = new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            private readonly TaskCompletionSource<bool> drain = new TaskCompletionSource<bool>();
            internal bool DrainFailure; internal bool TerminalFailure;
            public Task<OperationResult<SceneSnapshot>> Completion => caller.Task;
            public Task<OperationResult<SceneSnapshot>> NativeCompletion => native.Task;
            public Task NativeDrained => drain.Task;
            public NativeSceneDispatchStatus DispatchStatus { get; set; }
            public void FailCaller(ModErrorCode code, string message) { caller.TrySetResult(OperationResult<SceneSnapshot>.Failure(code, message)); }
            public void NativeCompleted(OperationResult<SceneSnapshot> result)
            {
                caller.TrySetResult(result);
                native.TrySetResult(TerminalFailure ? OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "late native import failure") : result);
                if (DrainFailure) drain.TrySetException(new InvalidOperationException("drain failed")); else drain.TrySetResult(true);
            }
            public void RequireManagedCompletion() { }
            public void ManagedCompleted(OperationResult<bool> result) { }
        }
    }
}
