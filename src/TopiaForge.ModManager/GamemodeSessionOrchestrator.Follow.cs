using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Session follow (WS3.8): a joined player's game runs its host's world. The follow takes the host's world session id,
    /// resolves the host's launch against this game's own packages, and runs Preparing (every scope a world owner),
    /// LoadingWorld (the same provider's <c>LoadAsync</c>, after the level an additive arena is built over), Synchronizing
    /// (the host's world arriving), Participant (this player's participant controller alone) and Running. The lifecycle
    /// commits Preparing, LoadingWorld (through Synchronizing too), StartingMode and Running, as a host's session does, so
    /// every consumer of the world session sees the host's. See docs/internal/MultiplayerRuntimeIntegration.md, section 18.
    /// </summary>
    internal sealed partial class GamemodeSessionOrchestrator
    {
        internal const string FollowerCannotStop = "Only the host can stop this session's world. Leave the session to stop playing.";
        internal const string FollowerCannotRestart = "Only the host can restart this session's world.";
        internal const string FollowerCannotLaunch = "You are in another player's session, so its world is the host's to choose. Leave the session to start your own game.";

        /// <summary>Gets or sets the session follower, which the runtime installs: while it follows a host, a local launch is refused.</summary>
        internal ISessionFollowOwner? Follower { get; set; }

        /// <summary>Gets whether the session this game runs now follows a host's world; main thread.</summary>
        internal bool RunsFollowedSession => current?.Follow != null;

        /// <summary>Gets the host's world session id this game follows now, or null; main thread.</summary>
        internal string? FollowedSessionId => current?.Follow?.HostSessionId;

        /// <summary>
        /// Follows the host's world: ends the session this game runs, if any, and runs the host's. <paramref name="reached"/>
        /// hears each stage as it begins, with the step and step count of a two-part load. Cancelling ends the follow at any
        /// stage, as a stop does, and leaves the game where it got to; the follower returns to the main menu.
        /// </summary>
        internal Task<SessionFollowResult> FollowAsync(SessionFollowPlan plan, Action<SessionFollowStage, int, int> reached, CancellationToken token)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (reached == null) throw new ArgumentNullException(nameof(reached));
            var completion = new TaskCompletionSource<SessionFollowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.InvokeAsync(() => BeginFollow(plan, reached, token, completion));
            return completion.Task;
        }

        /// <summary>
        /// Loads the level a host is in outside a world session, by its scene name, as the follower: whatever session this game
        /// runs ends first, and none starts, as the host has none.
        /// </summary>
        internal Task<OperationResult<bool>> FollowLevelAsync(string sceneName, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("A level's scene name is required.", nameof(sceneName));
            return ReplaceWorldAsync(null, token, null, followed: true,
                (transitions, cancellation) => environment.LoadSceneAsync(transitions, sceneName, null, cancellation));
        }

        /// <summary>
        /// Gets this game's world session as a listen host announces it to its players (WS3.8): its session id, the launch it
        /// made and what that resolved to, and for an additive arena the level it is built over and where the player stood as
        /// it was built. Null while no session runs, or one starts or stops. The same instance comes back while the session
        /// runs, so a frame at rest allocates nothing; main thread.
        /// </summary>
        internal HostWorldState? DescribeRunningWorld()
        {
            var record = current;
            if (record == null || !record.ReachedRunning || record.Follow != null || lifecycle.Current.Phase != SessionPhase.Running) return null;
            if (record.Announced != null) return record.Announced;
            var descriptor = record.Plan.Descriptor;
            string? level = null;
            TransformState? spawn = null;
            var readiness = record.Readiness;
            if (descriptor.Transition == ModTransitions.AdditiveArena && readiness != null && SessionFollowWire.IsSceneName(readiness.Scene.Name))
            {
                level = readiness.Scene.Name;
                if (SessionFollowWire.IsSpawn(readiness.Spawn)) spawn = readiness.Spawn;
            }

            try
            {
                record.Announced = HostWorldState.World(record.Identity.SessionId, descriptor.Request, descriptor.GamemodeId, descriptor.WorldId,
                    descriptor.WorldFamilyId, descriptor.Transition, level, spawn);
            }
            catch (ArgumentException error)
            {
                // A session this game cannot describe to its players is not announced; they stay where they are.
                Report(error);
                return null;
            }

            return record.Announced;
        }

        private void BeginFollow(SessionFollowPlan plan, Action<SessionFollowStage, int, int> reached, CancellationToken token,
            TaskCompletionSource<SessionFollowResult> completion)
        {
            if (shuttingDown || token.IsCancellationRequested)
            {
                completion.TrySetResult(SessionFollowResult.Cancelled(SessionFollowStage.Preparing));
                return;
            }

            var admission = lifecycle.TryAcquire(native.IsSceneBusy, null, out var lease);
            if (admission != SessionAdmission.Accepted)
            {
                completion.TrySetResult(SessionFollowResult.Busy(BusyMessage));
                return;
            }

            INativeTransitionReservation? reservation = null;

            // A follow of a launcher-planned join reports under that join's request; any other follow reports to no one, and its
            // own id names its reservation alone (ReportsToLauncher).
            var requestId = plan.LaunchRequestId ?? Guid.NewGuid().ToString("N");
            try
            {
                var snapshot = environment.Capture();
                if (snapshot.PackageCleanupPending)
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(SessionFollowResult.Busy("Package native cleanup is Busy."));
                    return;
                }

                var resolution = LaunchResolver.Resolve(snapshot.Profile, plan.Request, snapshot.Observation, snapshot.Bindings);
                var refused = !resolution.Resolved ? Unresolved(resolution, plan)
                    : !plan.Matches(resolution.Plan!.Descriptor) ? SessionFollowResult.Failed(FollowFailureKind.Differs, SessionFollowStage.Preparing,
                        ModErrorCode.InvalidState, "This game resolves the host's launch to another gamemode, world or transition.")
                    : null;
                if (refused != null)
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(refused);
                    return;
                }

                var resolved = resolution.Plan!;
                var modeOwner = Owner(resolved, resolved.GamemodeId);
                var worldOwner = Owner(resolved, resolved.WorldId);
                var mode = snapshot.Gamemodes.SingleOrDefault(item => item.Package.Equals(modeOwner) && Same(item.DeclarationId, resolved.GamemodeId));
                var world = snapshot.Worlds.SingleOrDefault(item => item.Package.Equals(worldOwner) && Same(item.DeclarationId, resolved.WorldFamilyId ?? resolved.WorldId));
                if (mode == null || world == null)
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(SessionFollowResult.Failed(FollowFailureKind.ModFailed, SessionFollowStage.Preparing,
                        ModErrorCode.InvalidState, "The host's gamemode or world has no binding in this game."));
                    return;
                }

                foreach (var package in new[] { modeOwner, worldOwner, Owner(resolved, resolved.TargetId) }.Distinct())
                    if (!snapshot.Contexts.TryGetValue(package.Id, out var context) || context.Lifetime.IsStopping)
                        throw new InvalidOperationException("The selected package context is unavailable: " + package.Id);

                // Only a joined player follows: one whose realm left Client meanwhile has nothing to follow.
                if (RealmOf(snapshot.Contexts[modeOwner.Id]) != NetRealm.Client)
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(SessionFollowResult.Cancelled(SessionFollowStage.Preparing));
                    return;
                }

                var identity = new SessionIdentity(plan.HostSessionId, requestId, resolved.Descriptor);
                var reserved = native.TryReserve(new NativeTransitionOwner(worldOwner.Id, runtimeOwnershipId + ":" + identity.SessionId, identity.SessionId),
                    requestId, SceneTransitionPriority.SessionFollow);
                if (!reserved.TryGetValue(out reservation))
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(SessionFollowResult.Failed(FollowFailureKind.PrepareFailed, SessionFollowStage.Preparing,
                        reserved.ErrorCode, reserved.ErrorMessage));
                    return;
                }

                var record = new SessionRecord(identity, resolved, snapshot, mode, world, lease!, reservation, Completion(), NetRealm.Client)
                {
                    Follow = plan
                };
                RunDriver(() => FollowCoreAsync(record, plan, reached, token, completion));
            }
            catch (Exception error)
            {
                reservation?.Dispose();
                lifecycle.Release(lease!);
                var failure = ExceptionFailure(error);
                completion.TrySetResult(SessionFollowResult.Failed(FollowFailureKind.PrepareFailed, SessionFollowStage.Preparing,
                    failure.ErrorCode, failure.ErrorMessage));
            }
        }

        private async Task<bool> FollowCoreAsync(SessionRecord record, SessionFollowPlan plan, Action<SessionFollowStage, int, int> reached,
            CancellationToken callerToken, TaskCompletionSource<SessionFollowResult> completion)
        {
            var stage = SessionFollowStage.Preparing;
            if (current != null)
            {
                // The host moved on, or this game left its world: what this game ran before ends, as a launch replaces it.
                var previous = current;
                previous.Lease = record.Lease;
                CancelRecord(previous);
                await CleanupAsync(previous, retainLease: true, replacementReserved: true);
                if (previous.Errors.Count > 0)
                {
                    await record.Reservation!.CloseAsync();
                    lifecycle.Release(record.Lease);
                    record.Cancellation.Dispose();
                    completion.TrySetResult(SessionFollowResult.Failed(FollowFailureKind.CleanupFailed, stage, ModErrorCode.External,
                        "The previous session failed to clean up."));
                    return false;
                }
            }

            current = record;
            try
            {
                Reach(reached, stage, 0, 0);
                Commit(record, SessionPhase.Preparing);
                foreach (var package in new[] { record.Mode.Package, record.World.Package, Owner(record.Plan, record.Plan.TargetId) }.Distinct())
                {
                    var slot = new NativeTransitionAccessSlot(record.Identity.SessionId + ":" + package.Id,
                        record.Identity.SessionId, () => !record.StopRequested);
                    record.Slots.Add(package.Id, slot);
                    var scope = await record.Snapshot.Contexts[package.Id].CreateChildScopeAsync(record.Identity.SessionId,
                        record.Cancellation.Token, () => RequestBoundStop(record.Identity.SessionId), slot, dispatcher,
                        ScopeContextFor(package.Id, record.Mode.Package.Id, NetRealm.Client));
                    record.Scopes.Add(package.Id, scope);
                }

                record.CallerCancellation = callerToken.Register(() => dispatcher.Post(() => RequestBoundStop(record.Identity.SessionId)));
                if (callerToken.IsCancellationRequested) CancelRecord(record);
                ThrowIfStopping(record);
                record.Provider = await dispatcher.InvokeAsync(record.World.Create);
                ThrowIfStopping(record);

                stage = SessionFollowStage.LoadingWorld;
                var steps = plan.BaseScene == null ? 1 : 2;
                Reach(reached, stage, 1, steps);
                Commit(record, SessionPhase.LoadingWorld);
                ThrowIfStopping(record);
                if (plan.BaseScene != null)
                {
                    // An additive arena is built over a level the host loaded first. This game loads the same level, and stands
                    // its player where the host's stood as the arena was built, so the arena lands where the host's did.
                    OperationResult<bool> level;
                    using (var grant = record.Reservation!.BorrowFor(record.World.Package.Id, record.Identity.SessionId))
                        level = await dispatcher.InvokeCallbackAsync(() =>
                            environment.LoadSceneAsync(grant.SceneTransitions, plan.BaseScene, plan.BaseSpawn, record.Cancellation.Token));
                    ThrowIfStopping(record);
                    if (!level.Succeeded) throw new FollowStop(FollowFailureKind.BaseLevelFailed, stage, level.ErrorCode, level.ErrorMessage);
                    Reach(reached, stage, 2, steps);
                }

                var worldContext = new WorldLoadContext(record.Identity, record.Scopes[record.World.Package.Id].Context, record.Plan.World.Spawn!);
                OperationResult<IWorldInstance> loaded;
                using (record.Slots[record.World.Package.Id].Install(record.Reservation!.BorrowFor(record.World.Package.Id, record.Identity.SessionId)))
                    loaded = await dispatcher.InvokeCallbackAsync(() => record.Provider.LoadAsync(worldContext, record.Cancellation.Token));
                if (loaded.TryGetValue(out var instance)) record.Instance = instance;
                ThrowIfStopping(record);
                if (!loaded.Succeeded) throw new FollowStop(FollowFailureKind.WorldLoadFailed, stage, loaded.ErrorCode, loaded.ErrorMessage);
                var ready = record.Instance!.Readiness ?? throw new FollowStop(FollowFailureKind.WorldLoadFailed, stage, ModErrorCode.InvalidState,
                    "The world provider returned no readiness evidence.");
                record.Readiness = ready;

                stage = SessionFollowStage.Synchronizing;
                Reach(reached, stage, 0, 0);
                var synchronized = await dispatcher.InvokeCallbackAsync(() => plan.Synchronize(record.Cancellation.Token));
                ThrowIfStopping(record);
                if (!synchronized.Succeeded)
                    throw new FollowStop(FollowFailureKind.SynchronizeFailed, stage, synchronized.ErrorCode, synchronized.ErrorMessage);

                stage = SessionFollowStage.Participant;
                Reach(reached, stage, 0, 0);
                Commit(record, SessionPhase.StartingMode);
                ThrowIfStopping(record);
                await StartGamemodeAsync(record, ready);
                await record.Reservation!.CloseAsync();
                record.Reservation = null;
                ThrowIfStopping(record);
                record.Starting = false;
                record.CallerCancellation.Dispose();
                record.ReachedRunning = true;
                Commit(record, SessionPhase.Running);
                lifecycle.Release(record.Lease);
                record.Start.TrySetResult(OperationResult<bool>.Success(true));
                Reach(reached, SessionFollowStage.Running, 0, 0);
                completion.TrySetResult(SessionFollowResult.Success);
                return true;
            }
            catch (Exception error)
            {
                var result = FollowFailure(error, stage);
                if (!result.IsCancelled) record.Errors.Add(error);
                CancelRecord(record, result.IsCancelled ? null : OperationResult<bool>.Failure(result.Code, result.Reason));
                record.Starting = false;
                await CleanupAsync(record);
                completion.TrySetResult(result);
                return false;
            }
        }

        /// <summary>How a follow that threw ended: cancelled, its own stop, or the stage it was in.</summary>
        private static SessionFollowResult FollowFailure(Exception error, SessionFollowStage stage)
        {
            if (error is OperationCanceledException || (error is StartupFailure cancelled && cancelled.Code == ModErrorCode.Cancelled)
                || (error is FollowStop stopped && stopped.Code == ModErrorCode.Cancelled))
                return SessionFollowResult.Cancelled(stage);
            if (error is FollowStop stop) return SessionFollowResult.Failed(stop.Kind, stop.Stage, stop.Code, stop.Message);
            var failure = error is StartupFailure startup ? OperationResult<bool>.Failure(startup.Code, startup.Message) : ExceptionFailure(error);
            var kind = stage == SessionFollowStage.Participant ? FollowFailureKind.ParticipantFailed
                : stage == SessionFollowStage.Synchronizing ? FollowFailureKind.SynchronizeFailed
                : stage == SessionFollowStage.LoadingWorld ? FollowFailureKind.WorldLoadFailed
                : FollowFailureKind.PrepareFailed;
            return SessionFollowResult.Failed(kind, stage, failure.ErrorCode, failure.ErrorMessage);
        }

        /// <summary>
        /// Why this game cannot start the host's launch: a discovered world it has not found, a mod that did not start, a
        /// launch its mods resolve differently, or something the host has that this game does not.
        /// </summary>
        private static SessionFollowResult Unresolved(LaunchResolution resolution, SessionFollowPlan plan)
        {
            var kind = FollowFailureKind.NotInstalled;
            var missingInstance = plan.WorldFamilyId != null;
            foreach (var block in resolution.Blocks)
            {
                switch (block.Code)
                {
                    case LaunchBlockCode.WorldNotDeclared:
                    case LaunchBlockCode.WorldUnavailable:
                    case LaunchBlockCode.NoAvailableTarget:
                        break;
                    default:
                        missingInstance = false;
                        break;
                }

                switch (block.Code)
                {
                    case LaunchBlockCode.GamemodeUnbound:
                    case LaunchBlockCode.WorldUnbound:
                    case LaunchBlockCode.PlanPackageSetMismatch:
                        if (kind == FollowFailureKind.NotInstalled) kind = FollowFailureKind.ModFailed;
                        break;
                    case LaunchBlockCode.WorldNotAdmittedByPolicy:
                    case LaunchBlockCode.WorldConsentMissing:
                    case LaunchBlockCode.WorldNotStaticallyDeclared:
                    case LaunchBlockCode.TransitionUnsatisfiable:
                    case LaunchBlockCode.TransitionNotOffered:
                    case LaunchBlockCode.SpawnRequirementUnsatisfied:
                    case LaunchBlockCode.PlanResolutionMismatch:
                        if (kind == FollowFailureKind.NotInstalled) kind = FollowFailureKind.Differs;
                        break;
                }
            }

            // A world the host found among the game's own levels, which this game has not listed, is not found rather than not
            // installed: this game's discovery may not have listed it yet, so the follower looks again once.
            if (missingInstance && resolution.Blocks.Count > 0) kind = FollowFailureKind.WorldNotFound;
            return SessionFollowResult.Failed(kind, SessionFollowStage.Preparing, ModErrorCode.InvalidState,
                "The host's launch is blocked here: " + string.Join("; ", resolution.Blocks.Select(block => block.Code.ToString())));
        }

        private void Reach(Action<SessionFollowStage, int, int> reached, SessionFollowStage stage, int step, int steps)
        {
            try { reached(stage, step, steps); }
            catch (Exception error) { Report(error); }
        }

        /// <summary>A follow's own stop at a stage, with the reason the runtime or a provider gave for the log.</summary>
        private sealed class FollowStop : Exception
        {
            internal FollowStop(FollowFailureKind kind, SessionFollowStage stage, ModErrorCode code, string reason) : base(reason)
            {
                Kind = kind;
                Stage = stage;
                Code = code;
            }

            internal FollowFailureKind Kind { get; }

            internal SessionFollowStage Stage { get; }

            internal ModErrorCode Code { get; }
        }
    }
}
