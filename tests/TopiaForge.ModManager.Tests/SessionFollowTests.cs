using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Session follow in the orchestrator (WS3.8): a joined player's game runs its host's world through every state, takes
    /// the host's session id, ends each failure with its own kind and leaves nothing behind, can be cancelled at each state,
    /// replaces a world it followed with the host's next, and is never stopped, restarted or replaced by a local command.
    /// </summary>
    internal static class SessionFollowTests
    {
        internal static void Run(string root)
        {
            EveryStateInOrder(root + "-states");
            MapIdentityReachesProvider(root + "-map");
            AnAdditiveArenaLoadsItsLevelFirst(root + "-additive");
            EachFailureEndsWithItsOwnKind(root + "-failures");
            CancellationAtEachState(root + "-cancel");
            TheHostChangingWorldsReplacesTheFollow(root + "-worlds");
            AJoinedPlayersOwnCommandsAreRefused(root + "-commands");
            TheFollowGrantLetsOnlyTheFollowThrough(root + "-grant");
            AFollowedLevelReplacesTheSession(root + "-level");
            OnlyALaunchedJoinsFollowReportsToTheLauncher(root + "-launcher");
            Console.WriteLine("SessionFollowTests passed.");
        }

        internal static string NewSessionId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// The host's launch of the fixture's target, followed: its own resolution, and the host's session id; with
        /// <paramref name="launchRequestId"/>, as the follow of a launcher-planned join.
        /// </summary>
        internal static SessionFollowPlan Plan(SessionFixture test, string sessionId, Func<CancellationToken, Task<OperationResult<bool>>>? synchronize = null,
            string? baseScene = null, TransformState? spawn = null, string? gamemode = null, string? launchRequestId = null)
        {
            var request = new LaunchRequest(test.Plan.Descriptor.Request.TargetId);
            var resolution = LaunchResolver.Resolve(test.Profile, request);
            That(resolution.Resolved, "the host's launch resolves here: " + string.Join("; ", resolution.Blocks.Select(block => block.Code + " " + block.Subject)));
            var resolved = resolution.Plan!.Descriptor;
            return new SessionFollowPlan(sessionId, request, gamemode ?? resolved.GamemodeId, resolved.WorldId, resolved.WorldFamilyId,
                resolved.Transition, baseScene, spawn, synchronize ?? (_ => Task.FromResult(OperationResult<bool>.Success(true))), launchRequestId);
        }

        internal static SessionFollowResult Follow(SessionFixture test, SessionFollowPlan plan, List<string>? reached = null, CancellationToken token = default)
        {
            var task = test.Hosted.FollowAsync(plan, (stage, step, steps) => reached?.Add(stage + (steps > 0 ? " " + step + "/" + steps : string.Empty)), token);
            test.Wait(task);
            return task.Result;
        }

        private static void MapIdentityReachesProvider(string root)
        {
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            var bytes = MapDocumentTests.Project();
            var identity = SessionMapStoreTests.Identity(bytes);
            var source = Plan(test, NewSessionId());
            var plan = new SessionFollowPlan(source.HostSessionId, source.Request, source.GamemodeId, source.WorldId,
                source.WorldFamilyId, source.Transition, null, null, source.Synchronize, map: identity,
                readMap: (_, __) => Task.FromResult(OperationResult<byte[]>.Success(bytes)));
            var calls = 0;
            SessionFixture.Load = async (context, token) =>
            {
                var follow = (IInternalMapFollowContext)context;
                That(ReferenceEquals(follow.ExpectedMap, identity) && follow.BeginMapHost == null, "joined load receives the host identity and cannot replace the host");
                var copy = await follow.ReadHostMap!(token, null);
                That(copy.Succeeded && copy.Value!.SequenceEqual(bytes), "host copy callback reaches the provider through the owned load context");
                calls++;
                return OperationResult<IWorldInstance>.Success(new SessionTestWorld());
            };
            That(Follow(test, plan).Succeeded && calls == 1, "exact map follows the normal orchestrator lifecycle");
        }

        private static void EveryStateInOrder(string root)
        {
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            var phases = new List<SessionPhase>();
            test.Hosted.StateChanged += state => phases.Add(state.Phase);
            var reached = new List<string>();
            var synchronizing = 0;
            var host = NewSessionId();
            var result = Follow(test, Plan(test, host, _ =>
            {
                synchronizing++;
                That(test.Hosted.Current.Phase == SessionPhase.LoadingWorld, "Synchronizing runs inside the LoadingWorld phase");
                return Task.FromResult(OperationResult<bool>.Success(true));
            }), reached);
            That(result.Succeeded, "the follow reaches Running: " + result);
            That(reached.SequenceEqual(new[] { "Preparing", "LoadingWorld 1/1", "Synchronizing", "Participant", "Running" }),
                "the follow reports every state as it begins (" + string.Join(", ", reached) + ")");
            That(phases.SequenceEqual(new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Running }),
                "the lifecycle commits Preparing, LoadingWorld, StartingMode and Running, as a session this game starts does");
            That(synchronizing == 1, "the follow waits once for the host's world to arrive");
            That(test.Hosted.Current.Identity!.SessionId == host && test.Hosted.FollowedSessionId == host && test.Hosted.RunsFollowedSession,
                "the session takes the host's world session id");
            That(test.ScopeProfiles.Select(profile => profile.Context).SequenceEqual(new[] { ModExecutionContext.WorldOwner, ModExecutionContext.Participant }),
                "the follow's scope is a world owner's, and this player's participant runs in a scope of its own");
            That(SessionFixture.ParticipantSession != null && SessionFixture.ModeSession == null,
                "this player's participant controller starts, and the gamemode's server controller never runs here");
            That(test.Outcomes.All(outcome => outcome.Kind != "launch"), "a follow publishes no launch outcome of this game's own");
        }

        /// <summary>A joined player whose target builds an additive arena over the level its player is in.</summary>
        internal static SessionFixture Additive(string root) => GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client,
            configure: manifest => manifest.Contributions!.LaunchTargets[0].Transition = ModTransitions.AdditiveArena);

        private static void AnAdditiveArenaLoadsItsLevelFirst(string root)
        {
            using var test = Additive(root);
            var order = new List<string>();
            test.Level = (scene, _) =>
            {
                order.Add("level " + scene);
                return Task.FromResult(OperationResult<bool>.Success(true));
            };
            SessionFixture.Load = (context, _) =>
            {
                order.Add("world " + context.Transition);
                return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            };
            var spawn = new TransformState(new Vec3(4f, 1f, -2f), Quat.Identity, new Vec3(1f, 1f, 1f));
            var reached = new List<string>();
            var result = Follow(test, Plan(test, NewSessionId(), baseScene: "Downtown", spawn: spawn), reached);
            That(result.Succeeded, "an additive arena is followed: " + result);
            That(order.SequenceEqual(new[] { "level Downtown", "world " + WorldLoadTransition.AdditiveArena }),
                "the level the host's arena is built over loads first, then the arena over it (" + string.Join(", ", order) + ")");
            That(test.Levels.Single().Spawn.Equals(spawn), "the player stands where the host's stood as its arena was built");
            That(reached.Take(3).SequenceEqual(new[] { "Preparing", "LoadingWorld 1/2", "LoadingWorld 2/2" }),
                "each load reports its own progress, in one follow (" + string.Join(", ", reached) + ")");
        }

        private static void EachFailureEndsWithItsOwnKind(string root)
        {
            var cases = new (string Name, Action<SessionFixture> Arrange, Func<SessionFixture, SessionFollowPlan> Plan, FollowFailureKind Kind, SessionFollowStage Stage, ModErrorCode Code)[]
            {
                ("missing-world", _ => SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Failure(ModErrorCode.NotFound, "Scene Assets/Levels/Gone.unity is gone.")),
                    test => Plan(test, NewSessionId()), FollowFailureKind.WorldLoadFailed, SessionFollowStage.LoadingWorld, ModErrorCode.NotFound),
                ("world-throws", _ => SessionFixture.Load = (_, _) => throw new InvalidOperationException("The provider broke at 192.0.2.7."),
                    test => Plan(test, NewSessionId()), FollowFailureKind.WorldLoadFailed, SessionFollowStage.LoadingWorld, ModErrorCode.External),
                ("level", test => test.Level = (_, _) => Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.TimedOut, "Caller: External: late")),
                    test => Plan(test, NewSessionId(), baseScene: "Downtown"), FollowFailureKind.BaseLevelFailed,
                    SessionFollowStage.LoadingWorld, ModErrorCode.TimedOut),
                ("synchronize", _ => { },
                    test => Plan(test, NewSessionId(), _ => Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.TimedOut, "The baseline stalled."))),
                    FollowFailureKind.SynchronizeFailed, SessionFollowStage.Synchronizing, ModErrorCode.TimedOut),
                ("participant", _ => SessionFixture.StartParticipant = (_, _) => Task.FromResult(OperationResult<IGamemodeParticipantController>.Failure(
                        ModErrorCode.Unavailable, "No tool bar slot.")),
                    test => Plan(test, NewSessionId()), FollowFailureKind.ParticipantFailed, SessionFollowStage.Participant, ModErrorCode.Unavailable),
                ("differs", _ => { }, test => Plan(test, NewSessionId(), gamemode: "io.github.furroxide.topiaforge.worlds.other_mode"),
                    FollowFailureKind.Differs, SessionFollowStage.Preparing, ModErrorCode.InvalidState),
                ("not-installed", _ => { }, test => new SessionFollowPlan(NewSessionId(), new LaunchRequest("io.example.elsewhere.target"),
                        "io.example.elsewhere.mode", "io.example.elsewhere.world", null, ModTransitions.SceneReplacement, null, null,
                        _ => Task.FromResult(OperationResult<bool>.Success(true))),
                    FollowFailureKind.NotInstalled, SessionFollowStage.Preparing, ModErrorCode.InvalidState),
                ("unbound", test => test.InvalidBindings = true, test => Plan(test, NewSessionId()),
                    FollowFailureKind.ModFailed, SessionFollowStage.Preparing, ModErrorCode.InvalidState)
            };
            foreach (var (name, arrange, plan, kind, stage, code) in cases)
            {
                using var test = name == "level" ? Additive(root + "-" + name) : GamemodeParticipantRealmTests.InRealm(root + "-" + name, NetRealm.Client);
                arrange(test);
                var result = Follow(test, plan(test));
                That(!result.Succeeded && !result.IsCancelled && result.Failure == kind && result.Stage == stage && result.Code == code,
                    name + ": the follow fails as " + kind + " at " + stage + " with " + code + " (" + result + ")");
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                That(test.Parent.ActiveChildScopeCount == 0 && !test.Native.IsSceneBusy && !test.Hosted.RunsFollowedSession,
                    name + ": nothing of the follow is left");
                That(test.Outcomes.All(outcome => outcome.Kind != "launch"), name + ": and no launch outcome of this game's own is published");
            }

            using var offline = GamemodeParticipantRealmTests.InRealm(root + "-offline", NetRealm.Offline);
            var notJoined = Follow(offline, Plan(offline, NewSessionId()));
            That(notJoined.IsCancelled && offline.Hosted.Current.Phase == SessionPhase.Idle, "a game that is not joined has nothing to follow");
        }

        private static void CancellationAtEachState(string root)
        {
            foreach (var stage in new[] { SessionFollowStage.Preparing, SessionFollowStage.LoadingWorld, SessionFollowStage.Synchronizing, SessionFollowStage.Participant })
            {
                using var test = GamemodeParticipantRealmTests.InRealm(root + "-" + stage, NetRealm.Client);
                using var cancellation = new CancellationTokenSource();
                var world = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var arrival = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var participant = new TaskCompletionSource<OperationResult<IGamemodeParticipantController>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var disposed = new List<string>();
                if (stage == SessionFollowStage.LoadingWorld) SessionFixture.Load = (_, _) => world.Task;
                else SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("world"))));
                if (stage == SessionFollowStage.Participant) SessionFixture.StartParticipant = (_, _) => participant.Task;
                var reached = new List<string>();
                var task = test.Hosted.FollowAsync(Plan(test, NewSessionId(), token => stage == SessionFollowStage.Synchronizing ? arrival.Task
                    : Task.FromResult(OperationResult<bool>.Success(true))), (at, _, _) =>
                {
                    reached.Add(at.ToString());
                    if (at == stage) cancellation.Cancel();
                }, cancellation.Token);
                test.Until(() => reached.Contains(stage.ToString()));
                world.TrySetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("world"))));
                arrival.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The follow was cancelled."));
                participant.TrySetResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController(() => disposed.Add("participant"))));
                test.Wait(task);
                That(task.Result.IsCancelled, stage + ": cancelling ends the follow as cancelled (" + task.Result + ")");
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                That(test.Parent.ActiveChildScopeCount == 0 && !test.Native.IsSceneBusy && !test.Hosted.RunsFollowedSession,
                    stage + ": nothing of the follow is left");
                That(stage == SessionFollowStage.Preparing || disposed.Contains("world"), stage + ": the world it loaded is disposed");
                That(stage != SessionFollowStage.Participant || disposed.Contains("participant"), stage + ": a participant that started late is disposed");
                That(Follow(test, Plan(test, NewSessionId())).Succeeded, stage + ": the next follow runs cleanly");
            }
        }

        private static void TheHostChangingWorldsReplacesTheFollow(string root)
        {
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            var disposed = new List<string>();
            var first = NewSessionId();
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("first"))));
            That(Follow(test, Plan(test, first)).Succeeded, "the host's first world is followed");
            var second = NewSessionId();
            var loads = 0;
            SessionFixture.Load = (_, _) =>
            {
                loads++;
                That(disposed.Contains("first"), "the world followed before is gone before the next one loads");
                return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("second"))));
            };
            That(Follow(test, Plan(test, second)).Succeeded && loads == 1, "the host's next world replaces it");
            That(test.Hosted.FollowedSessionId == second && test.Hosted.Current.Phase == SessionPhase.Running && test.Parent.ActiveChildScopeCount == 2,
                "this game runs the host's next world, with only its scopes");

            // A host that moves on while this game still loads its world: that follow is cancelled, and the next one runs.
            var stalled = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
            SessionFixture.Load = (_, _) => stalled.Task;
            using var moving = new CancellationTokenSource();
            var reached = new List<string>();
            var third = test.Hosted.FollowAsync(Plan(test, NewSessionId()), (stage, _, _) => reached.Add(stage.ToString()), moving.Token);
            test.Until(() => reached.Contains("LoadingWorld"));
            moving.Cancel();
            stalled.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("third"))));
            test.Wait(third);
            That(third.Result.IsCancelled && disposed.Contains("third"), "a follow the host left behind is cancelled, and its world disposed");
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            var fourth = NewSessionId();
            That(Follow(test, Plan(test, fourth)).Succeeded && test.Hosted.FollowedSessionId == fourth, "and the host's newest world runs");
        }

        private static void AJoinedPlayersOwnCommandsAreRefused(string root)
        {
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            var owner = new RecordingFollower { IsFollowing = true };
            test.Hosted.Follower = owner;
            var host = NewSessionId();
            That(Follow(test, Plan(test, host)).Succeeded, "the host's world is followed");
            var stop = test.Hosted.StopAsync(host);
            test.Wait(stop);
            That(stop.Result.ErrorCode == ModErrorCode.NotAuthoritative && stop.Result.ErrorMessage == GamemodeSessionOrchestrator.FollowerCannotStop,
                "a joined player cannot stop the host's world (" + stop.Result.ErrorMessage + ")");
            var restart = test.Hosted.RestartAsync(host);
            test.Wait(restart);
            That(restart.Result.ErrorCode == ModErrorCode.NotAuthoritative && restart.Result.ErrorMessage == GamemodeSessionOrchestrator.FollowerCannotRestart,
                "nor restart it (" + restart.Result.ErrorMessage + ")");
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "local-launch");
            test.Wait(launch);
            That(launch.Result.ErrorCode == ModErrorCode.NotAuthoritative && launch.Result.ErrorMessage == GamemodeSessionOrchestrator.FollowerCannotLaunch,
                "nor start a game of its own while it follows (" + launch.Result.ErrorMessage + ")");
            That(test.Hosted.FollowedSessionId == host && test.Hosted.Current.Phase == SessionPhase.Running, "and the host's world keeps running");

            var menus = test.MenuLoads;
            var menu = test.Hosted.ReturnToMainMenuAsync(host);
            test.Wait(menu);
            That(menu.Result.Succeeded && owner.LeftForMenu == 1 && test.MenuLoads == menus + 1,
                "the player's own way to the main menu leaves the session first, then loads the menu");
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            That(!test.Hosted.RunsFollowedSession && test.Parent.ActiveChildScopeCount == 0, "and nothing of the host's world is left");

            owner.IsFollowing = false;
            test.Launch("after-leaving");
            That(test.Hosted.Current.Phase == SessionPhase.Running && !test.Hosted.RunsFollowedSession, "once it no longer follows, a game of its own starts");
        }

        private static void TheFollowGrantLetsOnlyTheFollowThrough(string root)
        {
            // A joined player that synchronizes, and later one that is Ready: neither is the canonical server.
            var state = MultiplayerSessionState.Synchronizing;
            var policy = new MultiplayerSceneTransitionAuthorityPolicy(() => new MultiplayerSessionSnapshot(new MultiplayerSessionId("tests.follow"), state,
                MultiplayerProcessKind.Interactive, MultiplayerExecutionSide.Client, null, Array.Empty<MultiplayerParticipant>(), new NetworkTick(0), new SessionSeed(1)));
            using var host = new HostDispatcher();
            var scenes = new SceneCoordinator(null, policy, host);
            OperationResult<INativeTransitionReservation> Reserve(SceneTransitionPriority priority) =>
                scenes.TryReserve(new NativeTransitionOwner("io.example.follow", "follow:" + priority + ":" + Guid.NewGuid().ToString("N"), null),
                    "follow-" + Guid.NewGuid().ToString("N"), priority);

            foreach (var joined in new[] { MultiplayerSessionState.Synchronizing, MultiplayerSessionState.Ready })
            {
                state = joined;
                var withoutGrant = Reserve(SceneTransitionPriority.SessionFollow);
                That(!withoutGrant.Succeeded && withoutGrant.ErrorCode == ModErrorCode.NotAuthoritative,
                    joined + ": without the follow grant, a joined player's follow transition is refused like any other (" + withoutGrant.ErrorMessage + ")");
                using (policy.GrantFollow())
                {
                    That(policy.IsFollowing, joined + ": the grant is held");
                    var follow = Reserve(SceneTransitionPriority.SessionFollow);
                    That(follow.Succeeded, joined + ": with it, the follow's transition is let through: " + follow.ErrorMessage);
                    follow.Value!.Dispose();
                    var user = Reserve(SceneTransitionPriority.UserInitiated);
                    That(!user.Succeeded && user.ErrorCode == ModErrorCode.NotAuthoritative, joined + ": and only the follow's: a player's own stays refused");
                    That(!Reserve(SceneTransitionPriority.Automatic).Succeeded, joined + ": and so does an automatic one");
                }

                That(!policy.IsFollowing && !Reserve(SceneTransitionPriority.SessionFollow).Succeeded, joined + ": released, the grant lets nothing through any more");
            }

            var grant = policy.GrantFollow();
            grant.Dispose();
            grant.Dispose();
            That(!policy.IsFollowing, "a grant released twice is released once");
            host.Drain();
        }

        private static void AFollowedLevelReplacesTheSession(string root)
        {
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            That(Follow(test, Plan(test, NewSessionId())).Succeeded, "the host's world is followed");
            var level = test.Hosted.FollowLevelAsync("Downtown", CancellationToken.None);
            test.Wait(level);
            That(level.Result.Succeeded && test.Levels.Last().Scene == "Downtown" && !test.Levels.Last().Spawn.HasValue,
                "the level the host went to outside a session loads by its scene name");
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            That(!test.Hosted.RunsFollowedSession && test.Parent.ActiveChildScopeCount == 0, "and the host's world session ended first, as the host's did");
            var menu = test.Hosted.FollowMainMenuAsync();
            test.Wait(menu);
            That(menu.Result.Succeeded && test.MenuLoads == 1 && test.Outcomes.All(outcome => outcome.Kind != "launch"),
                "the follower's own return to the menu loads it and publishes no launch outcome");
        }

        /// <summary>
        /// A follow reports to the launcher only when it belongs to a launcher-planned join, and then under that join's
        /// request: each phase of the host's world session as this game runs it, and each followed session's end. A follow of
        /// a join started in game publishes nothing to the launcher, from its start to its end, and no follow publishes a
        /// launch outcome, which is the launch command's alone.
        /// </summary>
        private static void OnlyALaunchedJoinsFollowReportsToTheLauncher(string root)
        {
            using (var test = GamemodeParticipantRealmTests.InRealm(root + "-in-game", NetRealm.Client))
            {
                var progress = new List<LaunchProgress>();
                test.Hosted.Progress += progress.Add;
                That(Follow(test, Plan(test, NewSessionId())).Succeeded && Follow(test, Plan(test, NewSessionId())).Succeeded,
                    "an in-game join follows the host's world, and the host's next one");
                var menu = test.Hosted.FollowMainMenuAsync();
                test.Wait(menu);
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                That(menu.Result.Succeeded && progress.Count == 0 && test.Outcomes.Count == 0,
                    "and publishes nothing to the launcher, neither progress nor a session's end (" + progress.Count + " progress, " + test.Outcomes.Count + " outcomes)");
            }

            using (var test = GamemodeParticipantRealmTests.InRealm(root + "-launched", NetRealm.Client))
            {
                const string join = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
                var progress = new List<LaunchProgress>();
                test.Hosted.Progress += progress.Add;
                var first = NewSessionId();
                That(Follow(test, Plan(test, first, launchRequestId: join)).Succeeded, "a launched join follows the host's world");
                That(progress.Select(record => record.Phase).SequenceEqual(new[] { "preparing", "loading-world", "starting-mode", "running" })
                    && progress.All(record => record.RequestId == join && record.SessionId == first) && test.Hosted.Current.Identity!.RequestId == join,
                    "its session takes the join's request, and each phase it commits is published under it (" + string.Join(", ", progress.Select(record => record.Phase)) + ")");
                That(test.Outcomes.Count == 0, "a session that runs has not ended");

                var second = NewSessionId();
                That(Follow(test, Plan(test, second, launchRequestId: join)).Succeeded, "the host's next world is followed");
                var ended = test.Outcomes.Single();
                That(ended.Kind == "session" && ended.RequestId == join && ended.SessionId == first && ended.Status == "succeeded" && ended.Command == null,
                    "the world followed before ends under the join's request, as a session's end, never as a launch (" + ended.Kind + " " + ended.Status + ")");
                var last = progress[progress.Count - 1];
                That(last.Phase == "running" && last.SessionId == second && last.RequestId == join, "and the next one runs under it");
                That(progress.Select(record => record.Sequence).Zip(progress.Select(record => record.Sequence).Skip(1), (before, after) => after > before).All(rises => rises),
                    "under one rising sequence");
                That(test.Outcomes.All(outcome => outcome.Kind != "launch"), "no follow publishes a launch outcome");
            }
        }

        /// <summary>A follower as the orchestrator sees it, recording the player's own way to the menu.</summary>
        internal sealed class RecordingFollower : ISessionFollowOwner
        {
            public bool IsFollowing { get; set; }

            internal int LeftForMenu { get; private set; }

            public void LeaveForMainMenu() => LeftForMenu++;
        }
    }
}
