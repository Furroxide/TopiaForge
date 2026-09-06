using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    /// <summary>Loader-owned transition access for trusted first-party adapters.</summary>
    internal interface IInternalSceneTransitionContext
    {
        IInternalSceneTransitionService SceneTransitions { get; }
    }

    internal interface IInternalSceneTransitionService
    {
        bool IsBusy { get; }
        OperationResult<IInternalSceneTransitionLease> Acquire(string sceneName, bool automatic, string reason);
        OperationResult<IInternalNativeSceneOperation> TryDispatch(
            NativeSceneRequest request, IInternalNativeSceneDispatch dispatch,
            CancellationToken callerToken = default);
    }

    /// <summary>Closing admission never abandons an already dispatched native operation.</summary>
    internal interface IInternalSceneTransitionLease : IDisposable
    {
        IInternalSceneTransitionService Transitions { get; }
    }

    internal sealed class NativeSceneRequest
    {
        public NativeSceneRequest(string sceneName, bool automatic, string reason, bool observeSceneArrival = true)
        {
            SceneName = string.IsNullOrWhiteSpace(sceneName)
                ? throw new ArgumentException("An expected scene is required.", nameof(sceneName)) : sceneName;
            Automatic = automatic;
            Reason = reason ?? string.Empty;
            ObserveSceneArrival = observeSceneArrival;
        }
        public string SceneName { get; }
        public bool Automatic { get; }
        public string Reason { get; }
        public bool ObserveSceneArrival { get; }
    }

    internal enum NativeSceneDispatchStatus { NotDispatched, Dispatched, Indeterminate }

    internal interface IInternalNativeSceneDispatch
    {
        NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion);
    }

    /// <summary>Callbacks may originate on any thread; the manager marshals them to its host dispatcher.</summary>
    internal interface IInternalNativeSceneCompletion
    {
        void FailCaller(ModErrorCode code, string message);
        void NativeCompleted(OperationResult<SceneSnapshot> result);
        void RequireManagedCompletion();
        void ManagedCompleted(OperationResult<bool> result);
    }

    internal interface IInternalNativeSceneOperation
    {
        Task<OperationResult<SceneSnapshot>> Completion { get; }
        Task NativeDrained { get; }
        NativeSceneDispatchStatus DispatchStatus { get; }
    }

    internal sealed class DelegateNativeSceneDispatch : IInternalNativeSceneDispatch
    {
        private readonly Func<IInternalNativeSceneCompletion, NativeSceneDispatchStatus> begin;
        public DelegateNativeSceneDispatch(Func<IInternalNativeSceneCompletion, NativeSceneDispatchStatus> begin)
        {
            this.begin = begin ?? throw new ArgumentNullException(nameof(begin));
        }
        public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion) => begin(completion);
    }
}
