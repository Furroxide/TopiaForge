using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>SESSION's player rows and the check every moderation action passes before it fires.</summary>
    internal sealed partial class SessionPresenter
    {
        /// <summary>Main thread, every frame: rebuilds the players only when the session, its readiness or a mute changed.</summary>
        internal void Tick()
        {
            var tracker = context.Status;
            var listed = ListsPlayers(tracker);
            if (tracker.Version != titleVersion || tracker.Role != shownRole)
            {
                titleVersion = tracker.Version;
                RebuildTitle(tracker);
            }

            if (ReferenceEquals(tracker.Snapshot, shownSnapshot) && tracker.Role == shownRole && listed == shownListed && !rebuild) return;
            shownSnapshot = tracker.Snapshot;
            shownRole = tracker.Role;
            shownListed = listed;
            rebuild = false;
            players = listed ? Rows(tracker) : Array.Empty<PlayerRow>();

            // A selection whose id another player now holds, or that left, is dropped rather than carried to them.
            if (selected != null && (!keys.TryGetValue(selected.Id, out var key) || !key.Equals(selected.PlayerKey))) selected = null;
        }

        /// <summary>
        /// Gets whether <paramref name="target"/> still names the player the host chose: the session lists its players, and the
        /// current snapshot's participant with that id is connected, not this player, and holds the same valid player key.
        /// </summary>
        internal bool StillHolds(MultiplayerSessionSnapshot? snapshot, string id, MultiplayerPlayerKey playerKey)
        {
            if (snapshot == null || !ListsPlayers(context.Status) || !playerKey.IsValid) return false;
            foreach (var participant in snapshot.Participants)
            {
                if (!string.Equals(participant.Id.Value, id, StringComparison.Ordinal)) continue;
                return participant.IsConnected && !participant.IsLocal && !participant.IsHost && participant.PlayerKey.Equals(playerKey);
            }

            return false;
        }

        private void Act(Selection target, Func<IInternalMultiplayerControl, OperationResult<bool>> action, string done)
        {
            var control = context.Environment.Control;
            if (control == null) return;

            // The rows may be a frame old, and a confirmation can take a while: check the player against the session now.
            if (!StillHolds(context.Environment.SessionSnapshot, target.Id, target.PlayerKey))
            {
                selected = null;
                rebuild = true;
                context.Notify(ListChanged, ScreenTone.Warning);
                return;
            }

            OperationResult<bool> result;
            try
            {
                result = action(control);
            }
            catch (Exception exception)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.InvalidState, exception.GetType().Name);
            }

            if (result.Succeeded) context.Notify(done, ScreenTone.Success);
            else context.Notify(HostText.ForDisplay(result.ErrorMessage, HostText.LineChars), ScreenTone.Danger);
            rebuild = true;
            context.Status.RequestRefresh();
        }

        private IReadOnlyList<PlayerRow> Rows(MultiplayerStatusTracker tracker)
        {
            var control = context.Environment.Control;
            var snapshot = tracker.Snapshot!;
            var hosting = tracker.Role == InternalSessionRole.ListenHost;
            keys.Clear();
            var rows = new List<PlayerRow>(snapshot.Participants.Count);
            foreach (var participant in snapshot.Participants)
            {
                if (!participant.IsConnected) continue;
                var muted = false;
                if (hosting && control != null && !participant.IsLocal)
                {
                    try
                    {
                        muted = control.IsServerMuted(participant.Id);
                    }
                    catch (Exception)
                    {
                        muted = false;
                    }
                }

                // Only a player whose key the host knows can be told apart from whoever holds the id next.
                var moderatable = hosting && !participant.IsLocal && !participant.IsHost && participant.PlayerKey.IsValid;
                keys[participant.Id.Value] = participant.PlayerKey;
                rows.Add(new PlayerRow(participant.Id.Value, HostText.ForDisplay(participant.DisplayName, InternalJoinSettings.MaximumDisplayNameLength + 8),
                    participant.IsHost, participant.IsLocal, participant.PingMilliseconds, muted, moderatable));
            }

            return rows.AsReadOnly();
        }

        private void RebuildTitle(MultiplayerStatusTracker tracker)
        {
            var status = tracker.Status;
            inviteText = string.Empty;
            var name = status.Server == null ? string.Empty : HostText.ForDisplay(status.Server.Name);
            switch (tracker.Role)
            {
                case InternalSessionRole.ListenHost:
                    title = "Hosting this game";
                    subtitle = HostPresenter.PlayersLine(Math.Max(status.ConnectedPlayers, 1), status.MaxPlayers);
                    var invite = status.Internet.Invite;
                    inviteText = invite != null ? InvitePrefix + invite.Display + InviteSuffix
                        : status.Internet.ListingPhase != InternalListingPhase.Off ? InvitePending
                        : LanInvite;
                    break;
                case InternalSessionRole.Client:
                    title = name.Length == 0 ? "Joined a session" : "Joined " + name;
                    subtitle = status.JoinPhase == InternalJoinPhase.Reconnecting ? "Reconnecting to the host..." : "Playing on the host's session.";
                    break;
                default:
                    title = "Not in a session";
                    subtitle = string.Empty;
                    break;
            }
        }
    }
}
