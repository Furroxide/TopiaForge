using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A joined player's session follower over the loopback network, a real composition root and a real orchestrator: it
    /// holds readiness from link-up and follows each announcement of the host's, re-follows when the host moves on or this
    /// game leaves the host's world by itself, never replaces a game the player started (#224), and ends every failure in
    /// this game's own words at the main menu.
    /// </summary>
    internal static partial class SessionFollowerTests
    {
        internal static void Run(string root)
        {
            JoinsAndFollowsTheHostsWorld(root + "-join");
            ReadyOnlyOnceBothHoldsRelease(root + "-holds");
            TheHostMovingOnMidFollowIsFollowed(root + "-moving");
            TheHostChangingWorldsAfterTheJoinIsFollowed(root + "-changes");
            LeavingWhileTheWorldLoadsReturnsToTheMenu(root + "-leave");
            AFailureEndsTheJoinInItsOwnWords(root + "-failure");
            AGameStartedWhileJoiningIsNeverReplaced(root + "-rule-one");
            ANativeLevelReadsAsAGameWhenJoining(root + "-rule-two");
            LevelsAndTheMenuAreFollowed(root + "-levels");
            LeavingTheHostsWorldByItselfFollowsAgain(root + "-refollow");
            ThePlayersOwnWayOutLeavesOnce(root + "-own-menu");
            ShuttingDownLoadsNoMenu(root + "-shutdown");
            UnreadableAnnouncementsChangeNothing(root + "-unreadable");
            ALaunchedJoinsFollowsReportUnderItsRequest(root + "-launched");
            ABusyGameIsTriedAgainThenGivenUp(root + "-busy");
            AWorldNotFoundIsLookedForOnce(root + "-not-found");
            NativeLoadsWhileWaitingAtTheMenuAreCorrected(root + "-waiting-native");
            KeepControllerWorldsStillFollowNativeLoads(root + "-keep-native");
            NativeReloadsOfTheSameLevelAreCorrected(root + "-same-native");
            FollowCleanupDiagnosticsAreScrubbed(root + "-cleanup-diagnostics");
            FollowFailureWordsAreThisGamesOwn();
            SessionFollowWireTests.Run();
            Console.WriteLine("SessionFollowerTests passed.");
        }

        private static void JoinsAndFollowsTheHostsWorld(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            That(rig.Runtime.Realm == NetRealm.Client && rig.Follower.IsFollowing && rig.Policy.IsFollowing,
                "from link-up the follower follows, and holds the follow grant");
            That(rig.Guest.Holds == 1 && rig.Follower.HoldsReadiness, "and holds this player short of Ready");
            That(rig.Status.Stage == FollowStage.AwaitingHost && rig.Status.Detail == "Asking the host for its world", "and waits for the host's world");
            var host = SessionFollowTests.NewSessionId();
            var stages = rig.Record();
            rig.Announce(rig.World(host));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs here");
            That(stages.SequenceEqual(new[] { FollowStage.Preparing, FollowStage.LoadingWorld, FollowStage.Synchronizing, FollowStage.Participant, FollowStage.Running }),
                "the overlay hears every state of the follow (" + string.Join(", ", stages) + ")");
            That(rig.Test.Hosted.FollowedSessionId == host && rig.Test.Hosted.Current.Phase == SessionPhase.Running, "with the host's session id");
            That(rig.Guest.Holds == 0 && !rig.Follower.HoldsReadiness, "and the player is Ready once it runs");
            That(rig.Policy.IsFollowing, "the grant stays while joined, for the host's next world");
            That(rig.Status.NoticeVersion == 0, "the first arrival belongs to the join, so it adds no re-follow toast");
            That(rig.Guest.ProgressReports >= 3, "each stage of the follow counts as progress on its hold (" + rig.Guest.ProgressReports + ")");

            var allocated = Allocated(() => rig.Follow.Run(0.016f));
            That(allocated == 0, "a joined player's follow at rest allocates nothing (" + allocated + " bytes)");
        }

        private static void ReadyOnlyOnceBothHoldsRelease(string root)
        {
            using var rig = new FollowRig(root);
            rig.Arrival = new NetworkWorldProgress(NetworkWorldPhase.ReceivingBaseline, 2, 5, 0);
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.Synchronizing && rig.Status.Detail == "2/5", "the follow waits for the world, n of m");
            var reports = rig.Guest.ProgressReports;
            for (var frame = 0; frame < 200; frame++) rig.Frame();
            That(rig.Follower.HoldsReadiness && rig.Status.Stage == FollowStage.Synchronizing, "the follow holds while the world is still arriving");
            That(rig.Guest.ProgressReports == reports, "and never reports progress for a world that does not arrive, so the hold's idle bound still applies");
            rig.Arrival = new NetworkWorldProgress(NetworkWorldPhase.ReceivingBaseline, 4, 5, 0);
            rig.Frame();
            That(rig.Status.Detail == "4/5", "the count moves as the world arrives");
            rig.Arrival = new NetworkWorldProgress(NetworkWorldPhase.Synchronized, 5, 5, 0);
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "once the world is in place the follow runs");
            That(!rig.Follower.HoldsReadiness, "and releases its hold");
        }

        private static void TheHostMovingOnMidFollowIsFollowed(string root)
        {
            using var rig = new FollowRig(root);
            var stalled = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new List<string>();
            SessionFixture.Load = (_, _) => stalled.Task;
            rig.Join();
            var first = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(first));
            rig.Until(() => rig.Status.Stage == FollowStage.LoadingWorld, "the host's first world loads");
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("second"))));
            var second = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(second));
            rig.Frame();
            stalled.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() => disposed.Add("first"))));
            rig.Until(() => rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == second, "the host's newer world runs");
            That(disposed.SequenceEqual(new[] { "first" }), "the world the host left behind is disposed, and the newer one kept");
            That(!rig.Follower.HoldsReadiness && rig.Runtime.Realm == NetRealm.Client, "the player is Ready in the host's newer world, still joined");
        }

        private static void TheHostChangingWorldsAfterTheJoinIsFollowed(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
            That(!rig.Status.IsRefollow, "the first follow of a join is the join's own");
            var notices = rig.Status.NoticeVersion;
            var next = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(next));
            rig.Until(() => rig.Status.IsRefollow && rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == next,
                "the host's next world is followed");
            That(rig.Status.NoticeVersion == notices + 1 && rig.Status.Notice == FollowFailureWords.Rejoined, "and the player is told it is back in the host's world");
            That(rig.Guest.Holds == 0 && rig.Runtime.Realm == NetRealm.Client, "the hold the re-follow took is released");
        }

        private static void LeavingWhileTheWorldLoadsReturnsToTheMenu(string root)
        {
            using var rig = new FollowRig(root);
            var stalled = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
            SessionFixture.Load = (_, _) => stalled.Task;
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.LoadingWorld, "the host's world loads");
            That(rig.Runtime.Leave().Result.Succeeded, "leaving is never refused");
            rig.Frame();
            That(!rig.Follower.IsFollowing && !rig.Policy.IsFollowing && rig.Guest.Holds == 0, "leaving ends the follow, its grant and its hold at once");
            That(rig.Status.Stage == FollowStage.ReturningToMenu, "and the overlay says the game returns to the main menu");
            stalled.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            rig.Until(() => rig.Test.MenuLoads == 1 && rig.Status.Stage == FollowStage.None, "the main menu loads once the follow has ended");
            That(rig.Test.Hosted.Current.Phase == SessionPhase.Idle && rig.Test.Parent.ActiveChildScopeCount == 0 && !rig.Test.Hosted.RunsFollowedSession,
                "and nothing of the host's world is left");
        }

        private static void AFailureEndsTheJoinInItsOwnWords(string root)
        {
            using var rig = new FollowRig(root);
            var diagnostics = new List<Exception>();
            rig.Test.Hosted.DiagnosticFailure += diagnostics.Add;
            SessionFixture.Load = (_, _) =>
            {
                // The world's scene arrived, and then its provider failed.
                rig.Scene.Activated("World");
                return Task.FromResult(OperationResult<IWorldInstance>.Failure(ModErrorCode.NotFound,
                    "Caller: External: Scene D:\\Games\\Robotopia\\Levels\\Gone.unity at 192.0.2.4 is gone. Caller: External: Scene gone."));
            };
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.Failed, "the follow fails");
            var failure = rig.Status.Failure!;
            That(failure.Kind == FollowFailureKind.WorldLoadFailed && failure.Code == ModErrorCode.NotFound && failure.Title == "Part of the host's world is missing",
                "the failure has its own kind and words (" + failure.Title + ")");
            rig.Frame();
            That(rig.Runtime.Realm == NetRealm.Offline && !rig.Policy.IsFollowing && rig.Guest.Holds == 0, "the join ends: this game left the session");
            rig.Until(() => rig.Test.MenuLoads == 1, "and returns to the main menu");
            That(rig.Status.Stage == FollowStage.Failed, "the failure stays for the overlay to show");
            var logged = string.Join("\n", rig.Log.Lines);
            That(logged.Contains("WorldLoadFailed (NotFound)") && !logged.Contains("D:\\") && !logged.Contains("192.0.2.4") && !logged.Contains("Robotopia\\Levels"),
                "the reason goes to the manager log without a path or an address: " + logged);
            That(!failure.Message.Contains("Caller") && !failure.Title.Contains("Caller") && !failure.Message.Contains("Gone"),
                "and none of it reaches the player's words");
            That(diagnostics.Count > 0 && diagnostics.All(error => !error.ToString().Contains("192.0.2.4") && !error.ToString().Contains("Gone.unity")),
                "the session lifecycle diagnostic sink also scrubs follow failures");
        }

        private static void AGameStartedWhileJoiningIsNeverReplaced(string root)
        {
            using (var rig = new FollowRig(root + "-session"))
            {
                // The join was asked for from Idle; the player started a game while it resolved, and then it linked up.
                rig.Test.Launch("started-while-joining");
                rig.Join();
                That(rig.Status.Stage == FollowStage.Failed && rig.Status.Failure!.Kind == FollowFailureKind.StartedGameWhileJoining,
                    "session follow refuses the link-up, in its own words");
                That(rig.Status.Failure!.Retryable, "and offers RETRY, which asks \"Leave current game?\" first");
                rig.Frame();
                That(rig.Runtime.Realm == NetRealm.Offline && rig.Guest.Holds == 0 && !rig.Policy.IsFollowing, "this game left the session at once");
                That(rig.Test.Hosted.Current.Phase == SessionPhase.Running && rig.Test.MenuLoads == 0 && !rig.Test.Hosted.RunsFollowedSession,
                    "and the game the player started keeps running, untouched");
                That(rig.Runtime.EvaluateRealmChange(NetRealm.Client).Verdict == NetRealmTransitionVerdict.RequiresLeave,
                    "joining again asks \"Leave current game?\" first");
            }

            using (var rig = new FollowRig(root + "-native"))
            {
                // A level the game's own menus loaded while the join resolved.
                rig.Scene.Activated("Downtown");
                rig.Join();
                That(rig.Status.Failure?.Kind == FollowFailureKind.StartedGameWhileJoining, "a native level started while joining is refused too");
                rig.Frame();
                That(rig.Runtime.Realm == NetRealm.Offline && rig.Test.MenuLoads == 0 && rig.Test.Levels.Count == 0, "and stays as the player left it");
            }
        }

        private static void ANativeLevelReadsAsAGameWhenJoining(string root)
        {
            using var rig = new FollowRig(root);
            That(rig.Runtime.EvaluateRealmChange(NetRealm.Client).Verdict == NetRealmTransitionVerdict.Allowed, "at the main menu a join goes ahead");
            rig.Scene.Activated("Downtown");
            That(rig.Test.Hosted.Current.Phase == SessionPhase.Idle, "a native level outside the orchestrator still reads as Idle (#224)");
            var verdict = rig.Runtime.EvaluateRealmChange(NetRealm.Client);
            That(verdict.Verdict == NetRealmTransitionVerdict.RequiresLeave, "so session follow counts it as a game the player is in: \"Leave current game?\" (" + verdict.Verdict + ")");
            var joined = rig.Runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(!joined.Result.Succeeded && joined.Result.ErrorMessage == MultiplayerRuntime.LeaveGameFirst, "and a join from it is refused until the player leaves it");
            That(rig.Runtime.EvaluateRealmChange(NetRealm.ListenHost).Verdict == NetRealmTransitionVerdict.Allowed, "hosting from a native level is unchanged");
            var allocated = Allocated(() => rig.Runtime.EvaluateRealmChange(NetRealm.Client));
            That(allocated == 0, "asking allocates nothing, so a frame may ask (" + allocated + " bytes)");
            rig.Scene.Activated(GameScenes.MainMenuSceneName);
            That(rig.Runtime.EvaluateRealmChange(NetRealm.Client).Verdict == NetRealmTransitionVerdict.Allowed, "back at the menu, the join goes ahead");
        }

        private static void LevelsAndTheMenuAreFollowed(string root)
        {
            using (var rig = new FollowRig(root))
            {
                rig.Test.Menu = _ =>
                {
                    rig.Scene.Activated(GameScenes.MainMenuSceneName);
                    return Task.FromResult(OperationResult<bool>.Success(true));
                };
                rig.Test.Level = (scene, _) =>
                {
                    rig.Scene.Activated(scene);
                    return Task.FromResult(OperationResult<bool>.Success(true));
                };
                rig.Join();
                rig.Announce(HostWorldState.Level("Downtown"));
                rig.Until(() => rig.Status.Stage == FollowStage.InLevel, "the host's level loads here");
                That(rig.Test.Levels.Single().Scene == "Downtown" && !rig.Follower.HoldsReadiness, "by its scene name, and the player is Ready in it");
                var notices = rig.Status.NoticeVersion;
                rig.Announce(HostWorldState.Menu);
                rig.Until(() => rig.Status.Stage == FollowStage.AtMenu, "the host went to the main menu");
                That(rig.Test.MenuLoads == 1 && rig.Runtime.Realm == NetRealm.Client, "this game loads it too, and stays joined");
                That(rig.Status.Notice == FollowFailureWords.HostAtMenu && rig.Status.WaitingLine == FollowFailureWords.HostAtMenu,
                    "and the player is told the host is at its menu, once as a toast and for as long as the game waits there");
                That(rig.Status.NoticeVersion == notices + 1, "the menu arrival adds exactly one notice");
                rig.Announce(HostWorldState.Menu);
                for (var frame = 0; frame < 20; frame++) rig.Frame();
                That(rig.Status.NoticeVersion == notices + 1, "waiting and a repeated menu announcement add no further notice");
            }

            using (var rig = new FollowRig(root + "-missing"))
            {
                rig.Test.Level = (_, _) => Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.NotFound, "The native scene is unavailable: Downtown"));
                rig.Join();
                rig.Announce(HostWorldState.Level("Downtown"));
                rig.Until(() => rig.Status.Stage == FollowStage.LevelUnavailable, "a level this game cannot load leaves it at the menu");
                That(rig.Status.Detail == FollowFailureWords.LevelNotLoaded, "waiting there as the level not loaded, never as a host at its menu");
                That(rig.Test.Levels.Count == 1 && rig.Test.MenuLoads == 1, "having tried the level once and loaded the menu");
                That(rig.Status.Notice == FollowFailureWords.LevelNotLoaded && rig.Runtime.Realm == NetRealm.Client && !rig.Follower.HoldsReadiness,
                    "joined, told why, and Ready there for the host's next world");
                That(rig.Status.WaitingLine == FollowFailureWords.HostLevelUnavailable, "and why it waits is there for as long as it waits");
                for (var frame = 0; frame < 20; frame++) rig.Frame();
                That(rig.Test.Levels.Count == 1 && rig.Runtime.Realm == NetRealm.Client, "it does not try the level again, nor leave");
                rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
                rig.Until(() => rig.Status.Stage == FollowStage.Running, "and follows the host's next world");
                That(rig.Status.WaitingLine.Length == 0, "which clears why it waited");
            }

            using (var rig = new FollowRig(root + "-menu"))
            {
                rig.Join();
                rig.Announce(HostWorldState.Menu);
                rig.Frame();
                That(rig.Status.Stage == FollowStage.AtMenu && rig.Test.MenuLoads == 0 && !rig.Follower.HoldsReadiness,
                    "a host at the main menu, joined from it, loads nothing and the player is Ready at once");
                That(rig.Status.NoticeVersion == 0 && rig.Status.WaitingLine == FollowFailureWords.HostAtMenu,
                    "the join's own toast says the host is at its menu, so the follow adds none, and why the game waits stays");
            }
        }

        private static void LeavingTheHostsWorldByItselfFollowsAgain(string root)
        {
            using (var rig = new FollowRig(root, endOnScene: true))
            {
                rig.Join();
                var host = SessionFollowTests.NewSessionId();
                rig.Announce(rig.World(host));
                rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
                var loads = 0;
                SessionFixture.Load = (_, _) =>
                {
                    loads++;
                    return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
                };

                // A native load on this game alone ends the world session, as its gamemode asks.
                rig.Test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(77, "Elsewhere", SceneLifecyclePhase.Activated, SceneLoadMode.Single, isActive: true));
                rig.Until(() => loads == 1 && rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == host,
                    "this game follows the host's world again");
                That(rig.Follower.Refollows == 1 && rig.Status.IsRefollow, "counted as a re-follow");
            }

            using (var rig = new FollowRig(root + "-bound"))
            {
                rig.Test.Level = (scene, _) =>
                {
                    rig.Scene.Activated(scene);
                    return Task.FromResult(OperationResult<bool>.Success(true));
                };
                rig.Join();
                rig.Announce(HostWorldState.Level("Downtown"));
                rig.Until(() => rig.Status.Stage == FollowStage.InLevel, "the host's level loads");
                for (var away = 1; away <= SessionFollower.MaximumRefollows; away++)
                {
                    rig.Scene.Activated("Elsewhere");
                    rig.Until(() => rig.Status.Stage == FollowStage.InLevel && rig.Scene.Name == "Downtown" && rig.Follower.Refollows == away,
                        "a level this game went to by itself is left for the host's, time " + away);
                }

                rig.Scene.Activated("Elsewhere");
                rig.Until(() => rig.Status.Stage == FollowStage.Failed, "a game that keeps leaving the host's world");
                That(rig.Status.Failure!.Kind == FollowFailureKind.KeepsLeaving && !rig.Status.Failure.Retryable, "fails after eight re-follows, in its own words");
                rig.Frame();
                That(rig.Runtime.Realm == NetRealm.Offline, "and leaves the session");
            }

            using (var rig = new FollowRig(root + "-menu"))
            {
                rig.Join();
                rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
                rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
                rig.Scene.Activated(GameScenes.MainMenuSceneName);
                rig.Frame();
                That(rig.Status.Notice == FollowFailureWords.MenuLeft, "back at the main menu by itself, this game says it left the session");
                rig.Frame();
                That(rig.Runtime.Realm == NetRealm.Offline && rig.Test.MenuLoads == 0, "and leaves it, loading no second menu");
            }
        }

        private static void ThePlayersOwnWayOutLeavesOnce(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            var host = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(host));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
            var menu = rig.Test.Hosted.ReturnToMainMenuAsync(host);
            rig.Until(() => menu.IsCompleted, "the player's own return to the main menu completes");
            That(menu.Result.Succeeded && rig.Test.MenuLoads == 1, "it loads the menu: " + menu.Result.ErrorMessage);
            rig.Frame();
            rig.Frame();
            That(rig.Runtime.Realm == NetRealm.Offline && !rig.Policy.IsFollowing && rig.Test.MenuLoads == 1, "and leaves the session, with no second menu");
            That(rig.Status.Stage == FollowStage.None, "the follow simply ends");
        }

        private static void ShuttingDownLoadsNoMenu(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
            rig.Runtime.Disconnect();
            rig.Test.Host.Drain();
            That(!rig.Follower.IsFollowing && !rig.Policy.IsFollowing && rig.Guest.Holds == 0 && !rig.Follow.IsOpen, "detaching ends the follow and closes its channel");
            That(rig.Test.MenuLoads == 0, "and loads no menu while the game shuts down");
        }

        private static void UnreadableAnnouncementsChangeNothing(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            rig.SendRaw(new byte[] { 1, 9 });
            rig.SendRaw(new byte[] { 2, 3 });
            rig.SendRaw(new byte[] { 1, 2, 3, 0x40 });
            for (var frame = 0; frame < 5; frame++) rig.Frame();
            That(rig.Follower.Unreadable == 3 && rig.Status.Stage == FollowStage.AwaitingHost && rig.Test.Hosted.Current.Phase == SessionPhase.Idle,
                "announcements this game cannot read are counted and change nothing");
            rig.Announce(HostWorldState.Menu);
            rig.Frame();
            That(rig.Status.Stage == FollowStage.AtMenu, "and the host's next one is followed");
        }

        /// <summary>
        /// A join the launcher planned hands its request to the realm, and every follow of that join reports under it, the
        /// host's next world included, until this game leaves; a join started in game, after it, reports nothing at all.
        /// </summary>
        private static void ALaunchedJoinsFollowsReportUnderItsRequest(string root)
        {
            const string join = "join-0f1e2d3c";
            using var rig = new FollowRig(root);
            var progress = new List<LaunchProgress>();
            rig.Test.Hosted.Progress += progress.Add;
            That(rig.Runtime.Join(new FakeJoinTarget(), new InternalJoinSettings(), join).Succeeded && rig.Runtime.LaunchedJoin == join,
                "a launched join hands the realm its request");
            rig.Frame();
            That(rig.Follower.IsFollowing && rig.Follower.LaunchRequestId == join && rig.Status.LaunchRequestId == join,
                "the follower takes it at link-up, and so does its status, which the launch reads a failed follow from");
            var first = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(first));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the host's world runs");
            var second = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(second));
            rig.Until(() => rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == second, "and the host's next world");
            That(progress.Count > 0 && progress.All(record => record.RequestId == join) && progress.Any(record => record.SessionId == first)
                && progress[progress.Count - 1].Phase == "running" && progress[progress.Count - 1].SessionId == second,
                "every follow of the join reports its phases under the join's request (" + string.Join(", ", progress.Select(record => record.Phase)) + ")");
            That(rig.Test.Outcomes.Count == 1 && rig.Test.Outcomes[0].Kind == "session" && rig.Test.Outcomes[0].RequestId == join && rig.Test.Outcomes[0].SessionId == first,
                "and the end of the world it left behind");

            That(rig.Runtime.Leave().Succeeded && rig.Runtime.LaunchedJoin == null, "leaving lets go of the launched join");
            rig.Frame();
            That(!rig.Follower.IsFollowing && rig.Follower.LaunchRequestId == null, "and so does the follower");
            rig.Until(() => rig.Test.Hosted.Current.Phase == SessionPhase.Idle && rig.Test.MenuLoads == 1, "the game returns to the main menu");
            rig.Scene.Activated(GameScenes.MainMenuSceneName);

            // The player joins again from the game: nothing it follows reaches the launcher.
            var reported = progress.Count;
            var ends = rig.Test.Outcomes.Count;
            That(rig.Runtime.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded && rig.Runtime.LaunchedJoin == null, "an in-game join names no request");
            rig.Frame();
            That(rig.Follower.IsFollowing && rig.Follower.LaunchRequestId == null && rig.Status.LaunchRequestId == null,
                "so its follower has none, and its status no longer names the earlier launch");
            var third = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(third));
            rig.Until(() => rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == third, "the host's world runs again");
            var fourth = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(fourth));
            rig.Until(() => rig.Status.Stage == FollowStage.Running && rig.Test.Hosted.FollowedSessionId == fourth, "and its next one");
            That(progress.Count == reported && rig.Test.Outcomes.Count == ends,
                "an in-game join's follows publish nothing to the launcher (" + (progress.Count - reported) + " progress, " + (rig.Test.Outcomes.Count - ends) + " outcomes)");
        }

        private static void FollowFailureWordsAreThisGamesOwn()
        {
            var codes = (ModErrorCode[])Enum.GetValues(typeof(ModErrorCode));
            foreach (FollowFailureKind kind in Enum.GetValues(typeof(FollowFailureKind)))
            {
                foreach (var code in codes)
                {
                    var words = FollowFailureWords.For(kind, code);
                    var text = words.Title + " " + words.Message;
                    That(words.Title.Length > 0 && words.Message.Length > 0, kind + "/" + code + ": every failure has a title and a message");
                    That(!text.Contains("Caller:") && !text.Contains(code.ToString() + ":") && !text.Contains("Exception") && !text.Contains("io.") && !text.Contains("\\")
                        && !text.Contains("/"), kind + "/" + code + ": no internal text, error code, exception, id or path: " + text);
                    var sentences = words.Message.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries).Select(sentence => sentence.Trim().TrimEnd('.')).ToArray();
                    That(sentences.Distinct(StringComparer.Ordinal).Count() == sentences.Length, kind + "/" + code + ": no sentence is said twice: " + words.Message);
                    That(words.Message.EndsWith(".", StringComparison.Ordinal) && char.IsUpper(words.Message[0]), kind + "/" + code + ": a whole sentence: " + words.Message);
                }
            }

            var notices = new[] { FollowFailureWords.LevelNotLoaded, FollowFailureWords.MenuLeft, FollowFailureWords.Rejoined, FollowFailureWords.HostAtMenu, FollowFailureWords.HostLevelUnavailable };
            foreach (var notice in notices)
                That(!notice.Contains("Caller:") && notice.EndsWith(".", StringComparison.Ordinal), "each notice is a plain sentence: " + notice);
            That(SessionFollower.Scrub("Caller: External: C:\\Games\\x.unity /srv/games/levels at 192.0.2.4 and host.example.com:27250 failed")
                    == "Caller: External: [path] [path] at [address] and [address] failed",
                "the manager log keeps no path and no address: " + SessionFollower.Scrub("Caller: External: C:\\Games\\x.unity /srv/games/levels at 192.0.2.4 and host.example.com:27250 failed"));
        }

        /// <summary>A joined player's composition root, orchestrator and follower over the loopback network, and its host's channel.</summary>
        internal sealed class FollowRig : IDisposable
        {
            private readonly WorldWireWriter writer = new WorldWireWriter(SessionFollowWire.MaximumMessageBytes);

            internal FollowRig(string root, bool endOnScene = false, Action<ModManifest>? configure = null)
            {
                Guest = Net.AddPlayer("guest");
                Runtime = new MultiplayerRuntime(() => Guest, new MultiplayerRuntimeHost("2545", "0.2.0", Log), Log);
                Test = new SessionFixture(root, endOnScene, replication: new MultiplayerReplication(Runtime), configure: configure)
                {
                    FactoryType = typeof(SessionTestParticipantFactory)
                };
                Runtime.SessionPhaseSource = () => Test.Hosted.RealmPhase;

                // A world's scene becomes the active one as it loads, as in the game.
                SessionFixture.Load = (_, _) =>
                {
                    Scene.Activated("World");
                    return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
                };
                Follow = new SessionFollow(Test.Hosted, Scene, Policy.GrantFollow, () => Runtime.Leave(), () => Arrival, _ =>
                {
                    Discoveries++;
                    return Task.CompletedTask;
                }, () => Runtime.Control == null, () => Runtime.LaunchedJoin, Test.Host, Log);
                Runtime.AddObserver(Follow);
                Runtime.SessionFollow = Follow;
                Runtime.NativeWorldSource = () => Scene.IsGameplay;
                Scene.Activated(GameScenes.MainMenuSceneName);
                That(Runtime.Connect() && Follow.IsOpen, "the guest's runtime attaches and opens the follow channel");
                Net.StartHosting();
                Net.Host.Pump();
                HostChannel = Net.Host.OpenSystemChannel(InternalSystemChannelKind.SceneFollow).Value!;
            }

            internal LoopbackNetPair Net { get; } = new LoopbackNetPair();

            internal LoopbackControl Guest { get; }

            internal RecordingRuntimeLog Log { get; } = new RecordingRuntimeLog();

            internal MultiplayerRuntime Runtime { get; }

            internal SessionFixture Test { get; }

            internal FollowedScene Scene { get; } = new FollowedScene();

            internal MultiplayerSceneTransitionAuthorityPolicy Policy { get; } = new MultiplayerSceneTransitionAuthorityPolicy(() => null);

            internal SessionFollow Follow { get; }

            internal SessionFollower Follower => Follow.Follower;

            internal SessionFollowStatus Status => Follower.Status;

            internal IInternalSystemChannel HostChannel { get; }

            internal NetworkWorldProgress Arrival { get; set; } = new NetworkWorldProgress(NetworkWorldPhase.Synchronized, 0, 0, 0);

            internal int Discoveries { get; private set; }

            /// <summary>The host's world session of the fixture's own target, as the host would announce it.</summary>
            internal HostWorldState World(string sessionId)
            {
                var descriptor = Test.Plan.Descriptor;
                return HostWorldState.World(sessionId, descriptor.Request, descriptor.GamemodeId, descriptor.WorldId, descriptor.WorldFamilyId,
                    descriptor.Transition, null, null);
            }

            internal void Join()
            {
                Net.Join(Guest);
                Frame();
            }

            internal void Announce(HostWorldState state)
            {
                SessionFollowWire.Write(writer, state);
                That(HostChannel.Send(writer.Segment, InternalSystemTarget.Everyone).Succeeded, "the host announces " + state);
                Net.Host.Flush();
            }

            internal void SendRaw(byte[] payload)
            {
                That(HostChannel.Send(new ArraySegment<byte>(payload), InternalSystemTarget.Everyone).Succeeded, "the host sends raw bytes");
                Net.Host.Flush();
            }

            /// <summary>Records each stage the follow moves to, once each time it changes.</summary>
            internal List<FollowStage> Record()
            {
                var stages = new List<FollowStage>();
                Status.Moved += stages.Add;
                return stages;
            }

            internal void Frame()
            {
                Runtime.BeforeModsUpdate(0.016f);
                Test.Host.Drain();
                Runtime.AfterModsUpdate(0.016f);
                Test.Host.Drain();
            }

            internal void Until(Func<bool> done, string what)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!done() && DateTime.UtcNow < deadline)
                {
                    Frame();
                    Thread.Sleep(1);
                }

                That(done(), what + " (stage " + Status.Stage + ", " + Status.Detail + ")");
            }

            public void Dispose()
            {
                Runtime.Disconnect();
                Test.Dispose();
            }
        }
    }
}
