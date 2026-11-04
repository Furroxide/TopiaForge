using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        private sealed class Fixture : IDisposable
        {
            private readonly List<Exception> reports = new List<Exception>();
            internal readonly HostDispatcher Dispatcher;
            internal readonly SceneCoordinator Coordinator;
            internal readonly GamemodeSessionOrchestrator Sessions;
            internal readonly List<LaunchOutcome> Outcomes = new List<LaunchOutcome>();
            internal IInternalNativeSceneCompletion? Sink;
            internal IInternalNativeSceneOperation Operation => (IInternalNativeSceneOperation)Sink!;
            internal int Dispatches;
            private bool expectedDispatch, pendingBeforeDispatch;
            internal bool RequireManaged;
            internal NativeSceneDispatchStatus DispatchStatus = NativeSceneDispatchStatus.Dispatched;
            internal const string PrivateFailure = "private native detail https://example.invalid/secret";
            internal Fixture()
            {
                Dispatcher = new HostDispatcher(reports.Add);
                Coordinator = new SceneCoordinator(dispatcher: Dispatcher);
                Sessions = new GamemodeSessionOrchestrator(Dispatcher, Coordinator, new MenuEnvironment(ActualNativeLoad(Dispatch)), "menu-test-runtime");
                Sessions.Outcome += Outcomes.Add;
            }
            private NativeSceneDispatchStatus Dispatch(SceneLoadRequest request, IInternalNativeSceneCompletion completion)
            {
                Dispatches++; Sink = completion;
                expectedDispatch = request.SceneName == GameScenes.MainMenuSceneName && request.Mode == SceneLoadMode.Single;
                pendingBeforeDispatch = Sessions.MenuPresentation.Text == MenuLoadPresentation.OpeningText && Sessions.MenuPresentation.Pending;
                if (RequireManaged) completion.RequireManagedCompletion();
                if (DispatchStatus == NativeSceneDispatchStatus.NotDispatched)
                    completion.FailCaller(ModErrorCode.Unavailable, PrivateFailure);
                return DispatchStatus;
            }
            internal Task<OperationResult<bool>> Start(string? requestId = "first-menu", CancellationToken token = default)
            {
                var task = Sessions.ReturnToMainMenuAsync(cancellationToken: token, requestedId: requestId);
                Dispatcher.Drain();
                if (Dispatches > 0)
                {
                    Check(expectedDispatch, "actual native menu method retains its scene and load mode");
                    Check(pendingBeforeDispatch, "fixed pending notice precedes native dispatch");
                }
                return task;
            }
            internal void Timeout() => Coordinator.CheckTimeout(DateTime.UtcNow.AddSeconds(31), TimeSpan.FromSeconds(30));
            internal void CompleteNative(bool success = true) => Sink!.NativeCompleted(success
                ? OperationResult<SceneSnapshot>.Success(new SceneSnapshot(GameScenes.MainMenuSceneName, true, true))
                : OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, PrivateFailure));
            internal void Until(Func<bool> condition, string reason)
            {
                var deadline = Stopwatch.StartNew();
                do
                {
                    Dispatcher.Drain();
                    Check(reports.Count == 0, "host dispatcher reports no menu callback failure");
                    if (condition()) return;
                    Thread.Yield();
                } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Menu presentation fixture did not finish: " + reason);
            }
            public void Dispose()
            {
                try
                {
                    if (Sink != null && !Operation.NativeDrained.IsCompleted)
                    {
                        CompleteNative();
                        if (RequireManaged) Sink.ManagedCompleted(OperationResult<bool>.Success(true));
                        Until(() => Operation.NativeDrained.IsCompleted, "cleanup real native drain");
                    }
                    var shutdown = Sessions.ShutdownAsync();
                    Until(() => shutdown.IsCompleted && !Dispatcher.HasPendingWork, "actual session shutdown and callback drain");
                    shutdown.GetAwaiter().GetResult();
                }
                finally { Dispatcher.Dispose(); }
            }
        }

        private sealed class MenuEnvironment : IRuntimeSessionEnvironment
        {
            private readonly Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load;
            internal MenuEnvironment(Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load) { this.load = load; }
            public RuntimeSessionSnapshot Capture() => throw new NotSupportedException("A direct local menu does not resolve a launch profile.");
            public Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken token) => load(transitions, token);
            public Task<OperationResult<bool>> LoadSceneAsync(IInternalSceneTransitionService transitions, string name, TransformState? spawn, CancellationToken token) =>
                throw new NotSupportedException("This is a menu-only controlled native boundary.");
        }
    }
}
