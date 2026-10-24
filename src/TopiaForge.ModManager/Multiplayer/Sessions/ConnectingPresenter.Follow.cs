using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The connecting overlay over session follow (WS3.8). While a join synchronizes, the follow's stages fill its last three
    /// steps: Loading world (the host's level, then its world), Synchronizing (the world arriving, n of m) and Spawning (this
    /// player's controls). A join the follow ends shows the follow's own failure, in this game's words. After the join, when
    /// the host changes worlds or this game leaves the host's world by itself, the overlay shows "Following the host" with
    /// the first four steps done, says "Back in the host's world." once it is, and Cancel leaves the session. The follow's
    /// notices show once each. A frame that changes nothing allocates nothing.
    /// </summary>
    internal sealed partial class ConnectingPresenter
    {
        internal const string FollowingTitle = "Following the host";
        internal const string HostAtMenu = "The host is at the main menu. You join their next world.";
        internal const string LeftWhileFollowing = "You left the session.";

        /// <summary>The steps a join has done before a follow after it begins: Resolving to Verifying mods.</summary>
        private const int JoinedSteps = (int)ConnectingStep.LoadingWorld;

        private int followSerial;
        private int followShown = -1;
        private int noticeShown;

        // Cancel leaves the session; until the follow it cancelled has ended, the overlay does not show it again.
        private bool followCancelled;

        private SessionFollowStatus FollowStatus => context.Environment.Follow;

        /// <summary>
        /// The request of the launcher-planned join <paramref name="join"/> carries out, while that launch still waits for its
        /// result; session follow reports the host's world to the launcher under it, and under nothing else. Null for a join
        /// started in game, and for a launch already settled, as when RETRY joins again after it failed: the launcher keeps
        /// the result it has, and the player's own join reports nothing.
        /// </summary>
        private static string? LaunchedJoin(JoinRequest join) => join.Launch != null && !join.Launch.IsSettled ? join.Launch.RequestId : null;

        /// <summary>Remembers which join the follow speaks for from now: the next link-up's.</summary>
        private void FollowJoinStarted()
        {
            followSerial = FollowStatus.JoinSerial;
            followShown = -1;
        }

        /// <summary>Gets whether the follow speaks for the join this overlay runs.</summary>
        private bool FollowsThisJoin => FollowStatus.JoinSerial > followSerial;

        /// <summary>
        /// The step a synchronizing join is on, from the follow once it prepares the host's world; -1 while the provider's
        /// own step stands. It sets the step's line to the follow's.
        /// </summary>
        private int FollowStep()
        {
            var follow = FollowStatus;
            if (!FollowsThisJoin) return -1;
            int step;
            switch (follow.Stage)
            {
                case FollowStage.Preparing:
                case FollowStage.LoadingWorld:
                case FollowStage.LoadingLevel:
                    step = (int)ConnectingStep.LoadingWorld;
                    break;
                case FollowStage.Synchronizing:
                    step = (int)ConnectingStep.Synchronizing;
                    break;
                case FollowStage.Participant:
                    step = (int)ConnectingStep.Spawning;
                    break;
                default:
                    return -1;
            }

            if (follow.Version != followShown)
            {
                followShown = follow.Version;
                details[step] = follow.Steps == 2 ? follow.Detail + " (" + follow.Step + "/2)" : follow.Detail;
            }

            return step;
        }

        /// <summary>
        /// A join that ended because the follow failed shows the follow's failure, in this game's words, with RETRY when
        /// joining again can help. Gets whether it did.
        /// </summary>
        private bool FollowEnded(InternalLiveStatus status)
        {
            var follow = FollowStatus;
            if (cancelRequested || !FollowsThisJoin || follow.Stage != FollowStage.Failed || follow.Failure == null) return false;
            var failure = follow.Failure;
            var stop = (int)failure.Step;
            if (stop < reached) stop = reached;
            for (var step = reached + 1; step < stop; step++) skipped |= 1 << step;
            Fail(failure.ToFailureMessage(), stop, InternalLiveFailure.None, status);
            return true;
        }

        /// <summary>
        /// While no join runs: shows a follow that came after the join, and each of the follow's notices once. It compares
        /// two numbers when nothing changed.
        /// </summary>
        private void WatchFollow()
        {
            var follow = FollowStatus;
            if (follow.NoticeVersion != noticeShown)
            {
                noticeShown = follow.NoticeVersion;
                if (follow.Notice.Length > 0) context.Notify(follow.Notice);
            }

            var following = follow.IsRefollow && follow.Stage >= FollowStage.Preparing && follow.Stage <= FollowStage.Participant;
            if (!following) followCancelled = false;
            if (stage != Stage.Hidden && stage != Stage.Succeeded) return;
            if (!following || followCancelled) return;
            ShowFollowing();
        }

        private void ShowFollowing()
        {
            stage = Stage.Following;
            title = FollowingTitle;
            cancelRequested = false;
            failure = null;
            failureStatus = null;
            failureLines = System.Array.Empty<string>();
            hostSays = string.Empty;
            linger = 0f;
            complete = false;
            failed = -1;
            skipped = 0;
            reached = JoinedSteps;
            followShown = -1;
            for (var index = 0; index < details.Length; index++) details[index] = string.Empty;
            statusText = FollowingTitle + "...";
            Refollow();
        }

        /// <summary>Follows a follow after the join on the overlay: its step, then done or gone.</summary>
        private void Refollow()
        {
            var follow = FollowStatus;
            switch (follow.Stage)
            {
                case FollowStage.Running:
                case FollowStage.InLevel:
                case FollowStage.AtMenu:
                    complete = true;
                    active = -1;
                    stage = Stage.Succeeded;
                    linger = 0f;
                    statusText = follow.Stage == FollowStage.Running ? FollowFailureWords.Rejoined : follow.Detail;
                    Bump();
                    return;
                case FollowStage.Preparing:
                case FollowStage.LoadingWorld:
                case FollowStage.LoadingLevel:
                case FollowStage.Synchronizing:
                case FollowStage.Participant:
                    break;
                default:
                    // Left, failed or back at the menu: the join's own views and notices say so.
                    Hide();
                    return;
            }

            var step = (int)ConnectingStep.LoadingWorld;
            if (follow.Stage == FollowStage.Synchronizing) step = (int)ConnectingStep.Synchronizing;
            else if (follow.Stage == FollowStage.Participant) step = (int)ConnectingStep.Spawning;
            if (follow.Version != followShown)
            {
                followShown = follow.Version;
                details[step] = follow.Steps == 2 ? follow.Detail + " (" + follow.Step + "/2)" : follow.Detail;
            }

            if (active == step) return;
            active = step;
            Bump();
        }

        /// <summary>Cancel on a follow after the join leaves the session; leaving is never refused.</summary>
        private void CancelFollowing()
        {
            followCancelled = true;
            context.Environment.Leave();
            context.Status.RequestRefresh();
            Hide();
            context.Notify(LeftWhileFollowing);
        }
    }
}
