using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        private MenuLoadPresentation? menuPresentation;
        internal MenuLoadPresentation MenuPresentation => menuPresentation ??= new MenuLoadPresentation(dispatcher, () => !shuttingDown);
        /// <summary>
        /// Ends the session and loads the main menu. A joined player's session is the host's world (WS3.8), which this game
        /// cannot keep alone, so its own request to go to the main menu leaves the joined session first, and the menu loads
        /// as the session follower's, which the multiplayer authority policy allows while this game is joined.
        /// </summary>
        internal Task<OperationResult<bool>> ReturnToMainMenuAsync(string? sessionId = null, CancellationToken cancellationToken = default, string? requestedId = null) =>
            ReplaceWorldAsync(sessionId, cancellationToken, requestedId, followed: false, environment.LoadMainMenuAsync, showMenuNotice: true);

        /// <summary>
        /// Loads the main menu for the session follower: after this game left a joined session, or when the host itself is
        /// at the main menu. It ends whatever session runs, as a return to the menu does, and publishes no launch outcome.
        /// </summary>
        internal Task<OperationResult<bool>> FollowMainMenuAsync(CancellationToken cancellationToken = default) =>
            ReplaceWorldAsync(null, cancellationToken, null, followed: true, environment.LoadMainMenuAsync, showMenuNotice: true);

        private Task<OperationResult<bool>> ReplaceWorldAsync(string? sessionId, CancellationToken cancellationToken, string? requestedId,
            bool followed, Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load, bool showMenuNotice = false)
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
                var notice = showMenuNotice ? MenuPresentation.Begin(requestId) : null;
                if (!followed)
                    Publish(Progress, new LaunchProgress(requestId, NextSequence(), current == null ? "idle" : "stopping",
                        current?.Identity.SessionId, nativeBusy: true));
                RunDriver(() => MainMenuAsync(current, lease!, reservation, owner, requestId, cancellationToken, completion, followed, load, notice), followed: follow);
            });
            return completion.Task;
        }

        private async Task<bool> MainMenuAsync(SessionRecord? previous, SessionOperationLease lease,
            INativeTransitionReservation reservation, string owner, string requestId, CancellationToken cancellationToken,
            TaskCompletionSource<OperationResult<bool>> completion, bool followed,
            Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> load, MenuLoadPresentation.Attempt? notice)
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
                    result = await dispatcher.InvokeCallbackAsync(() => load(notice == null ? grant.SceneTransitions : MenuPresentation.ObserveTransitions(grant.SceneTransitions, notice), cancellation.Token));
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
                var isFollow = followed || previous?.Follow != null;
                if (isFollow) result = OperationResult<bool>.Failure(result.ErrorCode, SessionFollower.Scrub(result.ErrorMessage));
                foreach (var error in failures) Report(error, isFollow);
            }
            if (!followed)
                Publish(Progress, new LaunchProgress(requestId, NextSequence(), PhaseName(lifecycle.Current.Phase),
                    current?.Identity.SessionId, native.IsSceneBusy));
            lifecycle.Release(lease);
            if (notice != null) MenuPresentation.Complete(notice, result);
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
            // Invalidate before queued stop handling and independently of the controller's scene-change policy.
            if (current != null && ReferenceEquals(current.Identity, owner) && InvalidatesPresenceReadiness(current, scene))
                current.PresenceInvalidated = true;
            dispatcher.Post(() =>
            {
                var record = current;
                if (record == null || !ReferenceEquals(record.Identity, owner) || lifecycle.Current.Phase != SessionPhase.Running
                    || !InvalidatesPresenceReadiness(record, scene)) return;
                if (record.Follow == null && record.Plan.Gamemode.SceneChangePolicy != ModGamemodeDeclaration.EndSessionPolicy)
                {
                    // Single player keeps its controller: no other player's presence needs the lost readiness. The realm
                    // is read now, not at launch, because a host that started alone and opened to players later is online.
                    if (RealmOf(record.Snapshot.Contexts[record.Mode.Package.Id]) == NetRealm.Offline) return;
                    // Online, the provider owns immutable readiness for the lost scene. Retaining the controller here would
                    // leave Running with presence permanently closed; no existing contract can revalidate its world.
                    if (!record.PresenceFailureReported)
                    {
                        record.PresenceFailureReported = true; // Stop admission may defer; duplicate events report once.
                        record.Errors.Add(new InvalidOperationException(LostWorldReadinessMessage));
                    }
                }
                RequestBoundStop(record.Identity.SessionId);
            });
        }
    }
}
