using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// SESSION: the invite, the players, and leaving or ending the session. Players are listed, and kick, ban and mute are
    /// offered, only once the session is Ready: during a join the engine's snapshot can still carry the previous host's
    /// participants until the new host's first update, so nothing is shown or acted on before then. Participant ids are
    /// reused across hosts and restarts, so a selection remembers the player's key as well as its id, and every action
    /// checks that key against the current snapshot before it fires: when another player now holds the id, nobody is acted
    /// on and the player is told the list changed. Removing a player or ending the session confirms through the destructive
    /// modal first; leaving is never refused.
    /// </summary>
    internal sealed partial class SessionPresenter : ISessionScreen
    {
        internal const string KickMessage = "The host removed you from the session.";
        internal const string WaitingNote = "Players appear once the session is ready.";
        internal const string LanInvite = "Players on your local network find this session in BROWSE, under LAN.";
        internal const string InviteNeedsService = "Invite codes need the TopiaForge service, which is not configured in this build.";
        internal const string ListChanged = "The player list changed, so nothing was done. Select the player again.";

        private readonly MultiplayerScreensContext context;
        private readonly Dictionary<string, MultiplayerPlayerKey> keys = new Dictionary<string, MultiplayerPlayerKey>(StringComparer.Ordinal);
        private IReadOnlyList<PlayerRow> players = Array.Empty<PlayerRow>();
        private MultiplayerSessionSnapshot? shownSnapshot;
        private InternalSessionRole shownRole;
        private bool shownReady;
        private bool rebuild;
        private Selection? selected;
        private string title = "Not in a session";
        private string subtitle = string.Empty;
        private int titleVersion = -1;

        internal SessionPresenter(MultiplayerScreensContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public string Title => title;

        public string Subtitle => subtitle;

        public string InviteText =>
            context.Status.Role != InternalSessionRole.ListenHost ? string.Empty
            : context.Settings.Current.HostVisibility == InternalSessionVisibility.Lan ? LanInvite
            : InviteNeedsService;

        public bool PlayersVisible => IsReady(context.Status);

        public string PlayersNote =>
            PlayersVisible ? string.Empty
            : context.Status.Role == InternalSessionRole.Solo && !MultiplayerStatusTracker.IsInFlight(context.Status.Status) ? "Host a game or join one to play together."
            : WaitingNote;

        public IReadOnlyList<PlayerRow> Players => players;

        public string? SelectedKey => selected?.Id;

        public bool CanKick => SelectedRow()?.CanModerate == true;

        public bool CanBan => CanKick;

        public bool CanMute => CanKick;

        public bool IsSelectedMuted => SelectedRow()?.Muted == true;

        public bool CanEnd => context.Status.Role == InternalSessionRole.ListenHost;

        public bool CanLeave => context.Status.Role == InternalSessionRole.Client;

        /// <summary>
        /// Gets whether the session is Ready: this game hosts it, or joined it and the join is in session, and the session
        /// snapshot says Ready. Only then do its participants describe this session.
        /// </summary>
        internal static bool IsReady(MultiplayerStatusTracker tracker)
        {
            var snapshot = tracker.Snapshot;
            if (snapshot == null || snapshot.State != MultiplayerSessionState.Ready) return false;
            return tracker.Role == InternalSessionRole.ListenHost ||
                   (tracker.Role == InternalSessionRole.Client && tracker.Status.JoinPhase == InternalJoinPhase.InSession);
        }

        /// <summary>Selects the row with <paramref name="key"/>, remembering which player holds it now.</summary>
        public void Select(string? key)
        {
            if (key == null || !keys.TryGetValue(key, out var playerKey))
            {
                selected = null;
                return;
            }

            selected = new Selection(key, playerKey, NameOf(key));
        }

        public void Kick()
        {
            var target = Moderatable();
            if (target == null) return;
            context.Ask(PrivacyPrompts.Destructive("Remove " + target.Name + "?", target.Name + " leaves the session and can join again.", "REMOVE", confirmed =>
            {
                if (confirmed) Act(target, control => control.Kick(new ParticipantId(target.Id), KickMessage), "Removed " + target.Name + " from the session.");
            }));
        }

        public void Ban()
        {
            var target = Moderatable();
            if (target == null) return;
            context.Ask(PrivacyPrompts.Destructive("Ban " + target.Name + "?", target.Name + " leaves the session and cannot join it again while you host.", "BAN", confirmed =>
            {
                if (confirmed) Act(target, control => control.Ban(new ParticipantId(target.Id)), "Banned " + target.Name + " from the session.");
            }));
        }

        public void ToggleMute()
        {
            var target = Moderatable();
            if (target == null) return;
            var mute = !SelectedRow()!.Muted;
            Act(target, control => control.SetServerMute(new ParticipantId(target.Id), mute), (mute ? "Muted " : "Unmuted ") + target.Name + " for everyone.");
        }

        public void EndForEveryone()
        {
            if (!CanEnd) return;
            context.Ask(PrivacyPrompts.Destructive(HostPresenter.EndTitle, HostPresenter.EndBody, HostPresenter.EndConfirm, confirmed =>
            {
                if (confirmed) context.LeaveOnline("The session ended for everyone.");
            }));
        }

        public void Leave()
        {
            if (!CanLeave) return;
            context.Ask(PrivacyPrompts.Destructive("Leave server?", "You leave the session and keep playing on your own.", "LEAVE", confirmed =>
            {
                if (confirmed) context.LeaveOnline("You left the session.");
            }));
        }

        private PlayerRow? SelectedRow()
        {
            var current = selected;
            if (current == null || !PlayersVisible) return null;
            var row = Find(current.Id);
            return row != null && keys.TryGetValue(current.Id, out var key) && key.Equals(current.PlayerKey) ? row : null;
        }

        private Selection? Moderatable() => SelectedRow()?.CanModerate == true ? selected : null;

        private string NameOf(string key) => Find(key)?.Name ?? string.Empty;

        private PlayerRow? Find(string key)
        {
            for (var index = 0; index < players.Count; index++)
            {
                if (string.Equals(players[index].Key, key, StringComparison.Ordinal)) return players[index];
            }

            return null;
        }

        /// <summary>A selected player: the id the host gave them and the key that says who holds it.</summary>
        private sealed class Selection
        {
            internal Selection(string id, MultiplayerPlayerKey playerKey, string name)
            {
                Id = id;
                PlayerKey = playerKey;
                Name = name;
            }

            internal string Id { get; }

            internal MultiplayerPlayerKey PlayerKey { get; }

            internal string Name { get; }
        }
    }
}
