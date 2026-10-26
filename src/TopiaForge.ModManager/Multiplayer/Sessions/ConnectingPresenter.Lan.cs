using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// A LAN join needs the host's beacon: the provider finds a LAN session only among those this game hears while it listens
    /// on the local network, and BROWSE stops listening once its tab closes, as it does while the player plays. So a join
    /// whose LAN session this game does not hear now, as RETRY after a follow failed, listens for it on the overlay first,
    /// up to the 15 seconds a launched LAN join listens, and joins once it is heard. A host no longer on the network ends
    /// it as "Session not found", in words that say so, with RETRY to listen again. Listening this overlay started stops
    /// once the provider has looked the session up or the join has ended; one that BROWSE or a launch started is theirs to
    /// stop. Relay and direct joins never listen.
    /// </summary>
    internal sealed partial class ConnectingPresenter
    {
        /// <summary>How long a join listens for its LAN session before it says the host is not there: a launched LAN join's bound.</summary>
        internal const float LanListenSeconds = LaunchSessionPresenter.LanSearchSeconds;

        internal const string ListeningOnLan = "Listening on your network";

        /// <summary>A LAN host this game did not hear while it listened: it may have stopped hosting, or left the network.</summary>
        internal static readonly FailureMessage NotHeardOnLan = new FailureMessage("Session not found",
            "The session was not heard on your local network. Check that its host is still hosting on this network, then try again.",
            ConnectingStep.Resolving, retryable: true);

        private float listened;
        private bool listeningForJoin;

        /// <summary>
        /// Gets whether <paramref name="join"/> must listen for its LAN session before it starts: a LAN target this game does
        /// not hear now. The overlay then listens, or goes on listening, and the join waits.
        /// </summary>
        private bool ListensFirst(JoinRequest join)
        {
            if (join.Target.Kind != InternalJoinTargetKind.Lan || Heard(join.Target.ServerId)) return false;
            if (stage != Stage.Listening || !ReferenceEquals(request, join))
            {
                Show(join, Stage.Listening);
                details[(int)ConnectingStep.Resolving] = ListeningOnLan;
                statusText = LaunchSessionPresenter.LookingOnLan;
                listened = 0f;
                StartListening();
            }

            return true;
        }

        /// <summary>Main thread, each frame a join listens: it starts once its session is heard, or ends as not heard.</summary>
        private void Listen(float deltaTime)
        {
            var join = request!;
            if (Heard(join.Target.ServerId))
            {
                // The game may have changed while the overlay listened, so the realm rules decide again.
                Decide(join, fromWait: true);
                return;
            }

            listened += deltaTime > 0f && !float.IsNaN(deltaTime) ? deltaTime : 0f;
            if (listened < LanListenSeconds) return;
            StopListening();
            Fail(NotHeardOnLan, (int)ConnectingStep.Resolving, InternalLiveFailure.NotFound,
                new InternalLiveStatus { JoinFailure = InternalLiveFailure.NotFound, JoinDetail = NotHeardOnLan.Message });
            join.Launch?.Refuse(ModErrorCode.NotFound, LaunchSessionPresenter.NotHeard);
        }

        /// <summary>
        /// Every frame: stops the listening this overlay started once nothing needs the host's beacon, that is, unless the
        /// join listens, or has started and the provider may still be looking the session up.
        /// </summary>
        private void KeepListening()
        {
            if (!listeningForJoin || stage == Stage.Listening) return;
            if (stage == Stage.Joining && (context.Status.Reads <= readsAtStart || context.Status.Status.JoinPhase <= InternalJoinPhase.Resolving)) return;
            StopListening();
        }

        private void StartListening()
        {
            if (listeningForJoin || context.Status.Status.IsBrowsing) return;
            try
            {
                context.Environment.Control?.StartBrowsing();
                listeningForJoin = true;
            }
            catch (Exception)
            {
                // Without the local network the session is never heard, and the join ends as not heard.
            }
        }

        private void StopListening()
        {
            if (!listeningForJoin) return;
            listeningForJoin = false;
            try
            {
                context.Environment.Control?.StopBrowsing();
            }
            catch (Exception)
            {
                // Stopping is best effort; the provider stops browsing with its sessions anyway.
            }
        }

        /// <summary>Gets whether the local network lists <paramref name="serverId"/>, by index: enumerating the list would allocate.</summary>
        private bool Heard(string? serverId)
        {
            if (serverId == null) return false;
            IReadOnlyList<InternalLanListing> heard;
            try
            {
                var control = context.Environment.Control;
                if (control == null) return false;
                heard = control.LanSessions;
            }
            catch (Exception)
            {
                return false;
            }

            for (var index = 0; index < heard.Count; index++)
            {
                if (string.Equals(heard[index].ServerId, serverId, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}
