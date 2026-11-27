using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        internal static void Run()
        {
            PrepareActualNativeLoad();
            OwnerThreadMenuAdmission();
            TimeoutRetainsOwnership();
            CancellationAndLateFailure();
            ImmediateRefusalAndLocalSuccess();
            WorkerCallbacksStayOnHost();
            OwnerRetirement();
            FixedPluginReplies();
            BindingAndStaleReports();
            CallerWiring();
            ActualOverlayDisposal();
            Console.WriteLine("Menu presentation: actual native operation, caller timeout, drain, cancellation, local outcome and stale-view controls passed.");
        }

        private static void OwnerThreadMenuAdmission()
        {
            using var test = new Fixture();
            var gate = new LaunchDiscoveryGate(test.Dispatcher, () => throw new InvalidOperationException("A menu must not wait for discovery."));
            var pending = gate.ExplicitAsync(() => test.Sessions.ReturnToMainMenuAsync(requestedId: "owner-thread-menu"));
            Check(test.Dispatcher.IsCurrent && test.Dispatches == 1 && test.Sessions.MenuPresentation.CurrentAttempt?.RequestId == "owner-thread-menu",
                "actual owner-thread discovery gate admits the correlated menu attempt before returning its task");
            test.CompleteNative();
            test.Until(() => pending.IsCompleted, "owner-thread menu completion");
            Check(pending.Result.Succeeded, "owner-thread menu finishes through the original native drain path");
        }

        private static void TimeoutRetainsOwnership()
        {
            using var test = new Fixture();
            var pending = test.Start();
            Check(test.Sessions.MenuPresentation.Text == MenuLoadPresentation.OpeningText && test.Sessions.MenuPresentation.Pending
                && test.Sessions.MenuPresentation.CurrentAttempt!.RequestId == "first-menu",
                "admitted menu has correlated fixed pending text before native completion");
            Check(test.Sessions.Current.Phase == SessionPhase.Idle && test.Sessions.RealmPhase == SessionPhase.Stopping,
                "SDK Idle is distinct from busy native menu work");
            var attempt = test.Sessions.MenuPresentation.CurrentAttempt;
            test.Timeout();
            test.Until(() => test.Sessions.MenuPresentation.Text == MenuLoadPresentation.WaitingText,
                "caller timeout notice before native drain");
            Check(test.Operation.Completion.IsCompleted && !test.Operation.NativeDrained.IsCompleted && !pending.IsCompleted
                && test.Coordinator.IsSceneBusy && test.Outcomes.All(outcome => outcome.RequestId != "first-menu"),
                "timeout feedback never completes the command, publishes its final outcome or releases quarantine");
            var competing = test.Start("competing-menu");
            test.Until(() => competing.IsCompleted, "competing menu refusal");
            Check(competing.Result.ErrorCode == ModErrorCode.Conflict && test.Dispatches == 1
                && ReferenceEquals(attempt, test.Sessions.MenuPresentation.CurrentAttempt),
                "a competing request neither dispatches nor replaces the pending notice");
            test.CompleteNative();
            test.Until(() => pending.IsCompleted && test.Outcomes.Any(outcome => outcome.RequestId == "first-menu"), "late native completion");
            Check(!pending.Result.Succeeded && pending.Result.ErrorCode == ModErrorCode.External && !test.Coordinator.IsSceneBusy
                && test.Outcomes.Single(outcome => outcome.RequestId == "first-menu").Status == "failed"
                && test.Sessions.MenuPresentation.Text == MenuLoadPresentation.FailedText && !test.Sessions.MenuPresentation.Pending,
                "late native success preserves caller timeout failure and finalizes notice only after real drain");
        }

        private static void CancellationAndLateFailure()
        {
            using (var test = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = test.Start(token: cancellation.Token);
                cancellation.Cancel();
                test.Until(() => test.Sessions.MenuPresentation.Text == MenuLoadPresentation.CancellingText,
                    "cancelled caller still draining notice");
                Check(!pending.IsCompleted && test.Coordinator.IsSceneBusy && !test.Operation.NativeDrained.IsCompleted,
                    "post-dispatch cancellation retains command and native ownership");
                test.CompleteNative();
                test.Until(() => pending.IsCompleted, "cancelled real drain");
                Check(pending.Result.ErrorCode == ModErrorCode.Cancelled && test.Sessions.MenuPresentation.Text == MenuLoadPresentation.CancelledText,
                    "ordinary cancellation remains cancelled after native drain");
            }
            using (var test = new Fixture { RequireManaged = true })
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = test.Start(token: cancellation.Token);
                cancellation.Cancel();
                test.Until(() => test.Sessions.MenuPresentation.Text == MenuLoadPresentation.CancellingText, "cancelled managed operation notice");
                test.CompleteNative();
                Check(!test.Operation.NativeDrained.IsCompleted && !pending.IsCompleted, "managed completion remains part of real native drain");
                test.Sink!.ManagedCompleted(OperationResult<bool>.Failure(ModErrorCode.External, Fixture.PrivateFailure));
                test.Until(() => pending.IsCompleted, "late managed failure");
                Check(pending.Result.ErrorCode == ModErrorCode.External && test.Sessions.MenuPresentation.Text == MenuLoadPresentation.FailedText
                    && !test.Sessions.MenuPresentation.Text.Contains("private", StringComparison.Ordinal)
                    && !test.Sessions.MenuPresentation.Text.Contains("://", StringComparison.Ordinal),
                    "late terminal failure outranks cancellation without exposing native text in presentation");
            }
            using (var test = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var pending = test.Start(token: cancellation.Token);
                test.Until(() => pending.IsCompleted, "pre-dispatch cancellation");
                Check(pending.Result.ErrorCode == ModErrorCode.Cancelled && test.Dispatches == 0 && !test.Coordinator.IsSceneBusy,
                    "pre-dispatch cancellation creates no native work or fictitious readiness");
            }
        }

        private static void ImmediateRefusalAndLocalSuccess()
        {
            using (var test = new Fixture { DispatchStatus = TopiaForge.Mods.Internal.NativeSceneDispatchStatus.NotDispatched })
            {
                var pending = test.Start();
                test.Until(() => pending.IsCompleted, "preflight native refusal");
                Check(pending.Result.ErrorCode == ModErrorCode.Unavailable && !test.Coordinator.IsSceneBusy
                    && test.Sessions.MenuPresentation.Text == MenuLoadPresentation.FailedText,
                    "native preflight refusal retains its code and fixed failure presentation");
            }
            using (var test = new Fixture())
            {
                var pending = test.Start(requestId: null);
                Check(test.Sessions.MenuPresentation.Pending && test.Dispatches == 1, "direct local menu needs no launcher command to show pending text");
                test.CompleteNative();
                test.Until(() => pending.IsCompleted && test.Outcomes.Count == 1, "direct local completion");
                Check(pending.Result.Succeeded && test.Sessions.MenuPresentation.Text == MenuLoadPresentation.CompletedText
                    && !test.Sessions.MenuPresentation.Pending && !test.Coordinator.IsSceneBusy && test.Outcomes[0].Command == "main-menu",
                    "local success follows real drain and never asserts native UI or authentication readiness");
            }
        }

        private static void WorkerCallbacksStayOnHost()
        {
            using var test = new Fixture();
            var pending = test.Start();
            var model = test.Sessions.MenuPresentation;
            var version = model.Version;
            Task.Run(() => test.Sink!.FailCaller(ModErrorCode.Unavailable, Fixture.PrivateFailure)).GetAwaiter().GetResult();
            Check(model.Version == version && !test.Operation.Completion.IsCompleted,
                "worker native reports wait for the real host dispatcher");
            test.Until(() => model.Text == MenuLoadPresentation.WaitingText, "host publishes worker failure notice while native drain is pending");
            Task.Run(() => test.CompleteNative()).GetAwaiter().GetResult();
            Check(!test.Operation.NativeDrained.IsCompleted, "worker completion cannot release admission before the host turn");
            test.Until(() => pending.IsCompleted, "host processes worker native drain");
            Check(pending.Result.ErrorCode == ModErrorCode.Unavailable && !test.Coordinator.IsSceneBusy,
                "host preserves the original caller failure after worker drain");
        }

        private static void OwnerRetirement()
        {
            using var test = new Fixture();
            var pending = test.Start();
            var model = test.Sessions.MenuPresentation;
            var version = model.Version;
            var shutdown = test.Sessions.ShutdownAsync();
            test.Dispatcher.Drain();
            Check(!model.OwnerCurrent && model.Text.Length == 0, "runtime shutdown retires presentation before native drain");
            test.Timeout();
            test.CompleteNative();
            test.Until(() => pending.IsCompleted && shutdown.IsCompleted, "shutdown real drain");
            Check(model.Version == version && !test.Coordinator.IsSceneBusy, "retired runtime callbacks cannot repaint presentation or release work early");
        }

        private static void FixedPluginReplies()
        {
            var neverCompletes = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notReady = ActualReply(neverCompletes.Task, ready: false).GetAwaiter().GetResult();
            Check(!notReady.Ok && notReady.Message == "The runtime is not ready to return to the main menu.",
                "actual current not-ready menu refusal remains truthful without a notice or native operation");
            var failed = ActualReply(Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.External, Fixture.PrivateFailure))).GetAwaiter().GetResult();
            Check(!failed.Ok && failed.Message == MenuLoadPresentation.FailedText,
                "actual plugin menu reply never forwards raw native failure text");
            var faulted = ActualReply(Task.FromException<OperationResult<bool>>(new InvalidOperationException(Fixture.PrivateFailure))).GetAwaiter().GetResult();
            Check(!faulted.Ok && faulted.Message == MenuLoadPresentation.FailedText,
                "actual plugin menu catch uses fixed failure text");
            var cancelled = ActualReply(Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, Fixture.PrivateFailure))).GetAwaiter().GetResult();
            var busy = ActualReply(Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Conflict, Fixture.PrivateFailure))).GetAwaiter().GetResult();
            Check(cancelled.Message == MenuLoadPresentation.CancelledText && busy.Message == MenuLoadPresentation.BusyText,
                "actual plugin maps cancellation and admission refusal without interpreting native details");
        }

        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Menu presentation: " + message); }
    }
}
