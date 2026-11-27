using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MenuLoadPresentation
    {
        internal IInternalSceneTransitionService ObserveTransitions(IInternalSceneTransitionService inner, Attempt attempt) =>
            new ObservedTransitions(inner, this, attempt);

        private sealed class ObservedTransitions : IInternalSceneTransitionService
        {
            private readonly IInternalSceneTransitionService inner;
            private readonly MenuLoadPresentation presentation;
            private readonly Attempt attempt;
            internal ObservedTransitions(IInternalSceneTransitionService inner, MenuLoadPresentation presentation, Attempt attempt)
            { this.inner = inner; this.presentation = presentation; this.attempt = attempt; }
            public bool IsBusy => inner.IsBusy;
            public OperationResult<IInternalSceneTransitionLease> Acquire(string name, bool automatic, string reason) =>
                inner.Acquire(name, automatic, reason);
            public OperationResult<IInternalNativeSceneOperation> TryDispatch(NativeSceneRequest request,
                IInternalNativeSceneDispatch dispatch, CancellationToken token = default)
            {
                var result = inner.TryDispatch(request, dispatch, token);
                if (result.TryGetValue(out var operation)) _ = presentation.ObserveAsync(attempt, operation);
                return result;
            }
        }
    }
}
