using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    // In-process provider hosting only. The runtime selects the native entrypoints; this grants no
    // reflection, Harmony or arbitrary patch authority and is not part of the public author surface.
    internal interface IInternalNativeSpeechContext
    {
        IInternalNativeSpeechHost? NativeSpeech { get; }
    }

    internal interface IInternalNativeSpeechHost
    {
        // One installation per provider lifetime. Failure rolls back both native patches.
        IInternalNativeSpeechLease Install(IInternalNativeSpeechRouter router);
    }

    internal interface IInternalNativeSpeechRouter
    {
        // False means this head is not owned by the provider and native behavior must remain intact.
        // An owned head must return true even when routing fails or the request is unsupported.
        bool TryRoute(object head, string text, CancellationToken token, bool supported,
            out Task<OperationResult<bool>>? result);
        // Exact provider/entity/consuming-context generation; no routing or playback side effects.
        bool IsCurrent(object head, object association);
        CancellationToken AssociationStopping(object head, object association);
        // Called once after native patches are gone, including teardown retried by the owner lifetime.
        void OnStopped();
    }

    internal interface IInternalNativeSpeechLease : IDisposable
    {
        bool IsAvailable { get; }
        // An installation cannot play arbitrary native objects. Only a current provider association can bind.
        IInternalNativeSpeechPlayback Register(object head, object association);
    }

    internal interface IInternalNativeSpeechPlayback : IDisposable
    {
        bool IsAvailable { get; }
        // Captures one head and association; revalidates on every call and cancels when retired.
        Task PlayAsync(string text, CancellationToken token);
    }
}
