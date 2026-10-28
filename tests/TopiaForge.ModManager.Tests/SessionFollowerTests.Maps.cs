using System.Threading.Tasks;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SessionFollowerTests
    {
        private static void IdleMapTransferDoesNotRenewReadiness(string root)
        {
            using var rig = new FollowRig(root);
            var waiting = false;
            SessionFixture.Load = async (context, token) =>
            {
                waiting = true;
                var result = await ((IInternalMapFollowContext)context).ReadHostMap!(token, null);
                return OperationResult<IWorldInstance>.Failure(result.ErrorCode, result.ErrorMessage);
            };
            rig.Join();
            var world = rig.World(SessionFollowTests.NewSessionId());
            rig.Announce(HostWorldState.World(world.SessionId!, world.Request!, world.GamemodeId!, world.WorldId!, world.WorldFamilyId,
                world.Transition!, null, null, SessionMapStoreTests.Identity(MapDocumentTests.Project())));
            rig.Until(() => waiting, "map provider waits for the host's bytes");
            rig.Frame();
            var reports = rig.Guest.ProgressReports;
            for (var frame = 0; frame < 200; frame++) rig.Frame();
            That(rig.Guest.ProgressReports == reports && rig.Follower.HoldsReadiness,
                "an idle map transfer keeps readiness held without renewing its idle deadline");
        }

        private static void ReplacedStreamWaitsForANewAnnouncement(string root)
        {
            foreach (var outcome in new[] { "success", "failure", "cancel" })
            {
                using var rig = new FollowRig(root + "-" + outcome);
                var leaves = 0;
                var environment = new FakeSessionEnvironment(null, rig.Status, () =>
                {
                    leaves++;
                    return rig.Runtime.Leave();
                });
                var work = new ManualWorkScheduler();
                var presenter = new MultiplayerSessionsPresenter(environment, new MemorySettingsStore(), work);
                presenter.Start();
                work.RunAll();
                presenter.Tick(0.016f);
                rig.Join();
                rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
                rig.Until(() => rig.Status.Stage == FollowStage.Running, "first world runs");
                presenter.Tick(1f);
                That(!presenter.Connecting.IsVisible && !rig.Status.IsRefollow, "the initial arrival adds no re-follow overlay");
                That(rig.Net.Host.ReplaceSession().Succeeded, "map restart replaces the streams");
                rig.Frame();
                That(rig.Follower.HoldsReadiness && rig.Status.Stage == FollowStage.AwaitingHost && rig.Follower.Applied == null,
                    "replacement retires the old announcement and takes readiness before the new world arrives");
                for (var frame = 0; frame < 20; frame++) rig.Frame();
                That(rig.Follower.Applied == null && rig.Status.RefollowSerial == 0,
                    "waiting cannot replay the stale announcement or begin an attempt without a new announcement");
                var stalled = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
                SessionFixture.Load = (_, token) => stalled.Task.WaitAsync(token);
                var next = SessionFollowTests.NewSessionId();
                rig.Announce(rig.World(next));
                rig.Until(() => rig.Status.Stage == FollowStage.LoadingWorld, "replacement world loads");
                presenter.Tick(0.016f);
                var overlay = presenter.Connecting;
                That(overlay.IsVisible && overlay.CanCancel && overlay.Title == ConnectingPresenter.FollowingTitle
                    && overlay.Steps.Active == (int)ConnectingStep.LoadingWorld,
                    "the actual presenter shows replacement loading with Cancel");
                That(rig.Status.IsRefollow && rig.Status.RefollowSerial == 1 && rig.Follower.HoldsReadiness,
                    "the replacement begins exactly one re-follow and holds readiness");
                for (var frame = 0; frame < 20; frame++) rig.Frame();
                That(rig.Status.RefollowSerial == 1, "progress keeps the replacement attempt identity");
                if (outcome == "cancel")
                {
                    overlay.Cancel();
                    overlay.Cancel();
                    rig.Frame();
                    That(leaves == 1 && !rig.Follower.IsJoined && !overlay.IsVisible, "Cancel leaves the real follow exactly once");
                }
                else if (outcome == "failure") overlay.Dismiss();
                stalled.SetResult(outcome == "failure"
                    ? OperationResult<IWorldInstance>.Failure(ModErrorCode.External, "Map load failed")
                    : OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
                if (outcome == "cancel")
                {
                    rig.Until(() => rig.Test.MenuLoads == 1 && rig.Status.Stage == FollowStage.None, "cancelled replacement returns to the menu");
                    continue;
                }
                rig.Until(() => outcome == "failure" ? rig.Status.Stage == FollowStage.Failed
                    : rig.Test.Hosted.FollowedSessionId == next && rig.Status.Stage == FollowStage.Running, "replacement settles");
                presenter.Tick(0.016f);
                That(rig.Status.RefollowSerial == 1, "settlement keeps the replacement attempt identity");
                if (outcome == "failure")
                    That(overlay.IsVisible && overlay.HasFailed && overlay.FailureTitle == rig.Status.Failure!.Title && !overlay.CanCancel,
                        "the actual presenter reopens a dismissed replacement with its failure");
                else
                {
                    That(!rig.Follower.HoldsReadiness && overlay.Steps.Complete,
                        "new readiness releases only after the replacement world runs and the presenter completes");
                    That(rig.Net.Host.ReplaceSession().Succeeded, "another restart replaces the streams");
                    rig.Frame();
                    stalled = new TaskCompletionSource<OperationResult<IWorldInstance>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rig.Announce(rig.World(next));
                    rig.Until(() => rig.Status.Stage == FollowStage.LoadingWorld, "the same announcement on a new stream loads again");
                    presenter.Tick(0.016f);
                    That(rig.Status.RefollowSerial == 2 && overlay.IsVisible && overlay.CanCancel,
                        "a new stream starts exactly one new attempt even with an identical announcement");
                    stalled.SetResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
                    rig.Until(() => rig.Status.Stage == FollowStage.Running, "the repeated world runs");
                    That(rig.Status.RefollowSerial == 2, "arrival keeps the repeated announcement's attempt identity");
                }
            }

            using var joining = new FollowRig(root + "-initial");
            joining.Join();
            That(joining.Net.Host.ReplaceSession().Succeeded, "a stream can restart before the first arrival");
            joining.Frame();
            joining.Announce(joining.World(SessionFollowTests.NewSessionId()));
            joining.Until(() => joining.Status.Stage == FollowStage.Running, "the initial follow completes after stream replacement");
            That(!joining.Status.IsRefollow && joining.Status.RefollowSerial == 0,
                "replacement before any completed follow remains the initial join");
        }

    }
}
