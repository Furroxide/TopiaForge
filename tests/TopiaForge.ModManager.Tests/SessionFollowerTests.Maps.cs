using TopiaForge.ModManager.Sessions;
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
            using var rig = new FollowRig(root);
            rig.Join();
            rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "first world runs");
            That(rig.Net.Host.ReplaceSession().Succeeded, "map restart replaces the streams");
            rig.Frame();
            That(rig.Follower.HoldsReadiness && rig.Status.Stage == FollowStage.AwaitingHost && rig.Follower.Applied == null,
                "replacement retires the old announcement and takes readiness before the new world arrives");
            var next = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(next));
            rig.Until(() => rig.Test.Hosted.FollowedSessionId == next && rig.Status.Stage == FollowStage.Running, "new world runs");
            That(!rig.Follower.HoldsReadiness, "new readiness releases only after the replacement world runs");
        }

    }
}
