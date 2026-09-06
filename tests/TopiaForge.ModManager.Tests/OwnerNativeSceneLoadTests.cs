using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
namespace TopiaForge.ModManager.Tests
{
    internal static class OwnerNativeSceneLoadTests
    {
        internal static void Run()
        {
            HiddenNativeFailure();
            DeliveredFailureIsHandled();
            DrainAndTerminalFailuresAreBothRetained();
            StoppedOwnerDoesNotDispatch();
            RefusalDoesNotRetainWork();
            Console.WriteLine("OwnerNativeSceneLoadTests passed.");
        }
        private static void HiddenNativeFailure()
        {
            using var lifetime = new OwnerModLifetime();
            var operation = new Operation(); var enrolled = false;
            var caller = OwnerNativeSceneLoad.Start(lifetime, "core load", () =>
            {
                enrolled = lifetime.HasPendingNativeWork;
                return OperationResult<IInternalNativeSceneOperation>.Success(operation);
            }, default);
            Assert(enrolled, "Core native scene work must enroll before dispatch.");
            operation.Caller.SetResult(Failure(ModErrorCode.Cancelled, "caller cancellation"));
            lifetime.BeginStop(); var drain = lifetime.DrainNativeWorkAsync();
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled && !drain.IsCompleted,
                "Cancelled core load retains native ownership until its actual completion.");
            operation.Terminal.SetResult(Failure(ModErrorCode.External, "late core scene failure"));
            Assert(!drain.IsCompleted, "Terminal metadata does not establish native drain.");
            operation.Drained.SetResult(true);
            Assert(Failed(drain).Contains("late core scene failure"), "Late native failure must reach session/package cleanup.");
        }
        private static void DeliveredFailureIsHandled()
        {
            using var lifetime = new OwnerModLifetime(); var operation = new Operation();
            var caller = OwnerNativeSceneLoad.Start(lifetime, "core missing scene", () => OperationResult<IInternalNativeSceneOperation>.Success(operation), default);
            var missing = Failure(ModErrorCode.NotFound, "scene unavailable");
            operation.Caller.SetResult(missing); operation.Terminal.SetResult(missing); operation.Drained.SetResult(true);
            Await(lifetime.DrainNativeWorkAsync());
            Assert(caller.Result.ErrorCode == ModErrorCode.NotFound, "Delivered expected scene failure remains handled and keeps its code.");
        }
        private static void DrainAndTerminalFailuresAreBothRetained()
        {
            using var lifetime = new OwnerModLifetime(); var operation = new Operation();
            OwnerNativeSceneLoad.Start(lifetime, "faulted core tasks", () => OperationResult<IInternalNativeSceneOperation>.Success(operation), default);
            operation.Caller.SetResult(Failure(ModErrorCode.Cancelled, "cancelled"));
            operation.Drained.SetException(new InvalidOperationException("scene-drain-failure"));
            operation.Terminal.SetException(new InvalidOperationException("scene-terminal-failure"));
            var message = Failed(lifetime.DrainNativeWorkAsync());
            Assert(message.Contains("scene-drain-failure") && message.Contains("scene-terminal-failure"),
                "A failed drain observation must not skip independent terminal-result observation.");
        }
        private static void StoppedOwnerDoesNotDispatch()
        {
            using var lifetime = new OwnerModLifetime(); lifetime.BeginStop(); var dispatched = false;
            var result = OwnerNativeSceneLoad.Start(lifetime, "stopped", () =>
            { dispatched = true; return OperationResult<IInternalNativeSceneOperation>.Failure(ModErrorCode.Conflict, "busy"); }, default);
            Assert(!dispatched && result.Result.ErrorCode == ModErrorCode.Cancelled, "A stopped core scene owner must reject before dispatch.");
        }
        private static void RefusalDoesNotRetainWork()
        {
            using var lifetime = new OwnerModLifetime();
            var result = OwnerNativeSceneLoad.Start(lifetime, "busy", () => OperationResult<IInternalNativeSceneOperation>.Failure(ModErrorCode.Conflict, "busy"), default);
            Await(lifetime.DrainNativeWorkAsync());
            Assert(result.Result.ErrorCode == ModErrorCode.Conflict && !lifetime.HasPendingNativeWork,
                "A scene executor refusal is delivered normally without invented pending native work.");
        }
        private static OperationResult<SceneSnapshot> Failure(ModErrorCode code, string message) => OperationResult<SceneSnapshot>.Failure(code, message);
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Await(Task task) { if (!task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Core scene work did not drain."); }
        private static string Failed(Task task)
        { try { Await(task); } catch (AggregateException error) { return error.ToString(); } throw new InvalidOperationException("Expected late scene fault."); }
        private sealed class Operation : IInternalNativeSceneOperation
        {
            internal readonly TaskCompletionSource<OperationResult<SceneSnapshot>> Caller = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<OperationResult<SceneSnapshot>> Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<OperationResult<SceneSnapshot>> Completion => Caller.Task;
            public Task<OperationResult<SceneSnapshot>> NativeCompletion => Terminal.Task;
            public Task NativeDrained => Drained.Task;
            public NativeSceneDispatchStatus DispatchStatus => NativeSceneDispatchStatus.Dispatched;
        }
    }
}
