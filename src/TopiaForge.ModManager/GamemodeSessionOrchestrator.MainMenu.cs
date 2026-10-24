using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        /// <summary>
        /// Ends the session and loads the main menu. A joined player's session is the host's world (WS3.8), which this game
        /// cannot keep alone, so its own request to go to the main menu leaves the joined session first, and the menu loads
        /// as the session follower's, which the multiplayer authority policy allows while this game is joined.
        /// </summary>
        internal Task<OperationResult<bool>> ReturnToMainMenuAsync(string? sessionId = null, CancellationToken cancellationToken = default, string? requestedId = null) =>
            ReplaceWorldAsync(sessionId, cancellationToken, requestedId, followed: false, environment.LoadMainMenuAsync);

        /// <summary>
        /// Loads the main menu for the session follower: after this game left a joined session, or when the host itself is
        /// at the main menu. It ends whatever session runs, as a return to the menu does, and publishes no launch outcome.
        /// </summary>
        internal Task<OperationResult<bool>> FollowMainMenuAsync(CancellationToken cancellationToken = default) =>
            ReplaceWorldAsync(null, cancellationToken, null, followed: true, environment.LoadMainMenuAsync);

        private Task<OperationResult<bool>> ReplaceWorldAsync(string? sessionId, CancellationToken cancellationToken, string? requestedId,
            bool followed, Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load)
        {
            var completion = Completion();
            var requestId = requestedId ?? Guid.NewGuid().ToString("N");
            _ = new LaunchProgress(requestId, 0, "idle");
            _ = dispatcher.InvokeAsync(() =>
            {
                if (shuttingDown || cancellationToken.IsCancellationRequested)
                { Finish(completion, requestId, followed, ExceptionFailure(new OperationCanceledException())); return; }

                // The player's own way out of the host's world leaves the session; the follower then has nothing left to do.
                var follow = followed || (sessionId != null && current?.Follow != null && current.Identity.SessionId == sessionId);
                if (follow && !followed) Follower?.LeaveForMainMenu();
                var admission = lifecycle.TryAcquire(native.IsSceneBusy, sessionId, out var lease);
                if (admission != SessionAdmission.Accepted)
                { Finish(completion, requestId, followed, AdmissionFailure(admission)); return; }
                var owner = current == null ? "topiaforge.manager" : Owner(current.Plan, current.Plan.TargetId).Id;
                OperationResult<INativeTransitionReservation> reserved;
                try
                {
                    reserved = native.TryReserve(new NativeTransitionOwner(owner, runtimeOwnershipId + ":menu:" + requestId), requestId,
                        follow ? SceneTransitionPriority.SessionFollow : SceneTransitionPriority.UserInitiated);
                }
                catch (Exception error)
                {
                    lifecycle.Release(lease!);
                    Finish(completion, requestId, followed, ExceptionFailure(error));
                    return;
                }
                if (!reserved.TryGetValue(out var reservation))
                {
                    lifecycle.Release(lease!);
                    Finish(completion, requestId, followed, Failure(reserved));
                    return;
                }
                if (!followed)
                    Publish(Progress, new LaunchProgress(requestId, NextSequence(), current == null ? "idle" : "stopping",
                        current?.Identity.SessionId, nativeBusy: true));
                RunDriver(() => MainMenuAsync(current, lease!, reservation, owner, requestId, cancellationToken, completion, followed, load));
            });
            return completion.Task;
        }

        private async Task<bool> MainMenuAsync(SessionRecord? previous, SessionOperationLease lease,
            INativeTransitionReservation reservation, string owner, string requestId, CancellationToken cancellationToken,
            TaskCompletionSource<OperationResult<bool>> completion, bool followed,
            Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load)
        {
            var result = OperationResult<bool>.Success(true);
            var failures = new List<Exception>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            menuCancellation = cancellation;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (previous != null)
                {
                    previous.Lease = lease;
                    CancelRecord(previous);
                    await CleanupAsync(previous, retainLease: true, replacementReserved: true);
                    if (previous.Errors.Count > 0) throw new InvalidOperationException("The previous session failed to clean up.");
                }
                if (shuttingDown) cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                using (var grant = reservation.BorrowFor(owner, requestId))
                    result = await dispatcher.InvokeCallbackAsync(() => load(grant.SceneTransitions, cancellation.Token));
            }
            catch (Exception error)
            {
                result = ExceptionFailure(error);
                if (!(error is OperationCanceledException)) failures.Add(error);
            }
            finally
            {
                // A late close failure is still terminal cleanup evidence; it cannot strand
                // the command task or the lifecycle lease after native ownership has drained.
                try { await reservation.CloseAsync(); }
                catch (Exception error) { failures.Add(error); }
                menuCancellation = null;
            }
            if (failures.Count != 0)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.External,
                    Message(string.Join("; ", new[] { result.ErrorMessage }.Concat(failures.SelectMany(CleanupMessages))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))));
                foreach (var error in failures) Report(error);
            }
            if (!followed)
                Publish(Progress, new LaunchProgress(requestId, NextSequence(), PhaseName(lifecycle.Current.Phase),
                    current?.Identity.SessionId, native.IsSceneBusy));
            lifecycle.Release(lease);
            Finish(completion, requestId, followed, result);
            return result.Succeeded;
        }

        /// <summary>Completes a return to the menu: the player's own as the launch command it is, the follower's as nothing the launcher asked for.</summary>
        private void Finish(TaskCompletionSource<OperationResult<bool>> completion, string requestId, bool followed, OperationResult<bool> result)
        {
            if (followed) completion.TrySetResult(result.Succeeded ? result : Failure(result));
            else CompleteCommand(completion, requestId, "main-menu", null, result);
        }

        public void OnSceneLifecycle(SceneLifecycleEvent scene)
        {
            var observed = lifecycle.Current;
            if (observed.Phase != SessionPhase.Running || scene.IsInitial) return;
            var owner = observed.Identity;
            dispatcher.Post(() =>
            {
                var record = current;
                if (record == null || !ReferenceEquals(record.Identity, owner) || lifecycle.Current.Phase != SessionPhase.Running
                    || record.Plan.Gamemode.SceneChangePolicy != ModGamemodeDeclaration.EndSessionPolicy) return;
                var owned = record.Readiness!.Scene;
                var sameInstance = scene.SceneInstanceId == owned.InstanceId;
                if ((sameInstance && scene.Phase == SceneLifecyclePhase.Unloaded)
                    || (!sameInstance && scene.IsActive && (scene.Phase == SceneLifecyclePhase.Activated
                        || (scene.Phase == SceneLifecyclePhase.Loaded && scene.Mode == SceneLoadMode.Single))))
                    RequestBoundStop(record.Identity.SessionId);
            });
        }
    }
}
