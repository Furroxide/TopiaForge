using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// Loader-owned scene-transition gate used by trusted first-party providers without expanding the public SDK.
    /// </summary>
    internal interface IInternalSceneTransitionContext
    {
        IInternalSceneTransitionService SceneTransitions { get; }
    }

    /// <summary>Acquires an owner-bound claim before any native scene-loading side effect is dispatched.</summary>
    internal interface IInternalSceneTransitionService
    {
        OperationResult<IDisposable> Acquire(
            string sceneName,
            bool automatic,
            string reason);
    }
}
