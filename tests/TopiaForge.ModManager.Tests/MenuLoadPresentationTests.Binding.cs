using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        private static void BindingAndStaleReports()
        {
            using var test = new Fixture();
            var pending = test.Start();
            var source = test.Sessions.MenuPresentation;
            var first = source.CurrentAttempt;
            var binding = new MenuNoticeBinding();
            var build = new object();
            var owner = new object();
            Check(binding.TryRead(source, out var text, out var busy) && text == MenuLoadPresentation.OpeningText && busy,
                "literal view reads fixed pending state");
            Check(MenuNoticeBinding.CanFinish(build, build, owner, owner, source, source, first)
                && !MenuNoticeBinding.CanFinish(null, build, owner, owner, source, source, first)
                && !MenuNoticeBinding.CanFinish(new object(), build, owner, owner, source, source, first),
                "hidden or rebuilt view cannot finish an old action");
            Check(MenuNoticeBinding.CanFinish(build, build, owner, owner, null, null, null)
                && !MenuNoticeBinding.CanFinish(null, build, owner, owner, null, null, null)
                && !MenuNoticeBinding.CanFinish(build, build, new object(), owner, null, null, null),
                "current-build refusal without a notice remains visible while disposed or replaced owners are ignored");
            for (var index = 0; index < 200; index++) binding.TryRead(test.Sessions.MenuPresentation, out _, out _);
            var changes = 0;
            var allocated = TopiaForge.TestSupport.AllocationProbe.Measure("unchanged menu binding",
                () => { if (binding.TryRead(test.Sessions.MenuPresentation, out _, out _)) changes++; }, 10_000);
            Check(changes == 0 && allocated == 0, "unchanged menu binding is dirty-checked with exact-zero idle allocation");

            ForwardingIsTransparent(test, source, first!);
            test.CompleteNative();
            test.Until(() => pending.IsCompleted, "first real local attempt");
            var second = test.Start("second-menu");
            var next = source.CurrentAttempt;
            var version = source.Version;
            source.Complete(first, OperationResult<bool>.Failure(ModErrorCode.External, Fixture.PrivateFailure));
            Check(!ReferenceEquals(first, next) && source.Version == version && source.Pending
                && source.Text == MenuLoadPresentation.OpeningText
                && !MenuNoticeBinding.CanFinish(build, build, owner, owner, source, source, first),
                "late completion from an actual old attempt cannot repaint its successor");
            var otherOwnerActive = true;
            var successorRuntime = new MenuLoadPresentation(test.Dispatcher, () => otherOwnerActive);
            successorRuntime.Begin("successor-runtime-menu");
            Check(binding.TryRead(successorRuntime, out text, out busy) && text == MenuLoadPresentation.OpeningText && busy
                && !MenuNoticeBinding.CanFinish(build, build, owner, owner, successorRuntime, source, next),
                "view source identity rejects a replaced runtime even at an equal presentation version");
            otherOwnerActive = false;
            Check(binding.TryRead(successorRuntime, out text, out busy) && text.Length == 0 && !busy,
                "owner retirement clears notice without requiring a version mutation");
            binding.Reset();
            Check(binding.TryRead(source, out text, out busy) && text == MenuLoadPresentation.OpeningText && busy,
                "a rebuilt view binds the current attempt afresh");
            test.CompleteNative();
            test.Until(() => second.IsCompleted, "second real attempt");
            Check(MenuLoadPresentation.TerminalText(ModErrorCode.Conflict) == MenuLoadPresentation.BusyText
                && MenuLoadPresentation.TerminalText((ModErrorCode)int.MaxValue) == MenuLoadPresentation.FailedText,
                "presentation maps only fixed known text without native error interpolation");
        }

        private static void ForwardingIsTransparent(Fixture test, MenuLoadPresentation source, MenuLoadPresentation.Attempt attempt)
        {
            var inner = new RecordingTransitions();
            var observed = source.ObserveTransitions(inner, attempt);
            using var cancellation = new CancellationTokenSource();
            var request = new NativeSceneRequest("MainMenu", false, "menu presentation forwarding", observeSceneArrival: false);
            var dispatch = new DelegateNativeSceneDispatch(_ => NativeSceneDispatchStatus.NotDispatched);
            Check(observed.IsBusy && ReferenceEquals(observed.Acquire("MainMenu", true, "reason"), inner.Acquired)
                && inner.Scene == "MainMenu" && inner.Automatic && inner.Reason == "reason", "observer forwards admission queries unchanged");
            var result = observed.TryDispatch(request, dispatch, cancellation.Token);
            Check(ReferenceEquals(result, inner.Dispatched) && ReferenceEquals(inner.Request, request)
                && ReferenceEquals(inner.Dispatch, dispatch) && inner.Token == cancellation.Token && test.Dispatches == 1,
                "observer preserves exact dispatch arguments and result without creating native work");
        }

        private sealed class RecordingTransitions : IInternalSceneTransitionService
        {
            internal readonly OperationResult<IInternalSceneTransitionLease> Acquired = OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.Conflict, "busy");
            internal readonly OperationResult<IInternalNativeSceneOperation> Dispatched = OperationResult<IInternalNativeSceneOperation>.Failure(ModErrorCode.Unavailable, "refused");
            internal string? Scene, Reason;
            internal bool Automatic;
            internal NativeSceneRequest? Request;
            internal IInternalNativeSceneDispatch? Dispatch;
            internal CancellationToken Token;
            public bool IsBusy => true;
            public OperationResult<IInternalSceneTransitionLease> Acquire(string scene, bool automatic, string reason)
            { Scene = scene; Automatic = automatic; Reason = reason; return Acquired; }
            public OperationResult<IInternalNativeSceneOperation> TryDispatch(NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken token = default)
            { Request = request; Dispatch = dispatch; Token = token; return Dispatched; }
        }
    }
}
