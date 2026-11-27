using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SessionLaunchRecordsTests
    {
        private static void FollowFailuresKeepTheirReason(SessionFixture fixture, string root)
        {
            foreach (var started in new[] { false, true })
                foreach (var kind in new[] { FollowFailureKind.WorldLoadFailed, FollowFailureKind.CleanupFailed })
                {
                    var store = Store(Path.Combine(root, started + "-" + kind));
                    var errors = new List<Exception>();
                    var identity = new LaunchSessionIdentity(RequestId, "join", IntentId);
                    using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, RequestId, errors.Add, session: identity);
                    var launch = new LaunchedSession(identity, Plan("join"), publisher, () => LaunchedGame.Ready);
                    var script = new Script(fixture.Hosted, launch);
                    script.Runtime(1, "loading-world", GameSession);
                    launch.Started(0);
                    var status = new InternalLiveStatus
                    {
                        Role = InternalSessionRole.Client,
                        JoinPhase = started ? InternalJoinPhase.InSession : InternalJoinPhase.Synchronizing,
                        Server = Server(JoinServer)
                    };
                    launch.Follow(status, 1, 1);
                    var follow = new SessionFollowStatus();
                    var words = FollowFailureWords.For(kind, ModErrorCode.External);
                    follow.Joined("another-join");
                    follow.Fail(words);
                    launch.Follow(status, 1, 1, follow);
                    That(!launch.IsFinished, "another join's failure cannot settle this launch");
                    follow.Joined(RequestId);
                    follow.Fail(words);
                    script.Runtime(2, "idle");

                    // No transport refresh is needed: a world failure wins before Leave becomes a generic transport end.
                    launch.Follow(status, 1, 1, follow);
                    var path = store.OutcomePath(RequestId, started);
                    var bytes = File.ReadAllText(path);
                    var outcome = LaunchTransportJson.ReadOutcome(bytes);
                    That(launch.IsFinished && launch.IsSettled && outcome.Multiplayer!.Failure == (started ? null : "world-follow-failed"),
                        "the launcher receives the world's failure, not cancelled or left");
                    That(outcome.Multiplayer!.Detail == words.Title + ". " + words.Message,
                        "only fixed, actionable follow words reach the launch record");
                    That(started ? outcome.Kind == "session" && outcome.Multiplayer.EndReason == "world-follow-failed"
                        && outcome.SessionId == GameSession : outcome.Status == "failed", "the result uses the right record and retains the last world");
                    That(!started || outcome.Status == (kind == FollowFailureKind.CleanupFailed ? "failed" : "succeeded"),
                        "a terminal record still says whether world cleanup completed cleanly");
                    status.JoinPhase = InternalJoinPhase.Ended;
                    status.JoinFailure = InternalLiveFailure.Left;
                    launch.Follow(status, 2, 2, follow);
                    That(File.ReadAllText(path) == bytes && errors.Count == 0, "later transport status cannot overwrite the failure");
                }
        }
    }
}
