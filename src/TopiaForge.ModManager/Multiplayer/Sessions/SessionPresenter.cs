using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// SESSION: the invite, the players, and leaving or ending the session. Players are listed, and kick, ban and mute are
    /// offered, once the session's host has listed them: a joining player's snapshot lists nobody until the new host's own
    /// list arrives, before Ready, and keeps it while a lost link reconnects, so a list is always that host's players under
    /// that host's ids. Participant ids are reused across hosts and restarts, so a selection remembers the player's key as
    /// well as its id, and every action
    /// checks that key against the current snapshot before it fires: when another player now holds the id, nobody is acted
    /// on and the player is told the list changed. Removing a player or ending the session confirms through the destructive
    /// modal first; leaving is never refused.
    /// </summary>
    internal sealed partial class SessionPresenter : ISessionScreen
    {
        internal const string KickMessage = "The host removed you from the session.";
        internal const string WaitingNote = "Players appear once the host lists them.";
        internal const string LanInvite = "Players on your local network find this session in BROWSE, under LAN.";
        internal const string InvitePending = "The invite code shows here once the TopiaForge service has registered this session.";
        internal const string InvitePrefix = "Invite code: ";
        internal const string InviteSuffix = ". Players join with it in CONNECT; share it only with players you want.";
        internal const string ListChanged = "The player list changed, so nothing was done. Select the player again.";
        internal const string InviteCopied = "Copied the invite code. Paste it where your players are; share it only with players you want.";
        internal const string InviteNotCopied = "The invite code could not be copied. Read it from this tab instead.";

        private readonly MultiplayerScreensContext context;
        private readonly Dictionary<string, MultiplayerPlayerKey> keys = new Dictionary<string, MultiplayerPlayerKey>(StringComparer.Ordinal);
        private IReadOnlyList<PlayerRow> players = Array.Empty<PlayerRow>();
        private MultiplayerSessionSnapshot? shownSnapshot;
        private InternalSessionRole shownRole;
        private bool shownListed;
        private bool rebuild;
        private Selection? selected;
        private string title = "Not in a session";
        private string subtitle = string.Empty;
        private string inviteText = string.Empty;
        private int titleVersion = -1;

        internal SessionPresenter(MultiplayerScreensContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public string Title => title;

        public string Subtitle => subtitle;

        /// <summary>
        /// Gets how players reach this session, for its host: the invite code once the TopiaForge service has made one, which
        /// shows only here and is never logged, or how LAN players find it. Rebuilt only when the status moves.
        /// </summary>
        public string InviteText => inviteText;

        public bool CanCopyInvite => HostedInvite() != null;

        public bool PlayersVisible => ListsPlayers(context.Status);

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
        /// Gets whether the session's players can be listed: this game hosts the session or is joined to it, and its snapshot
        /// lists anyone. The engine lists a joining player nobody until the host's own list arrives, so whatever it lists is
        /// that host's players under that host's ids, before Ready and while a lost link reconnects alike. A join that has
        /// not connected runs no session of its own: what the snapshot lists then is solo play's.
        /// </summary>
        internal static bool ListsPlayers(MultiplayerStatusTracker tracker)
        {
            var snapshot = tracker.Snapshot;
            if (snapshot == null || snapshot.Participants.Count == 0) return false;
            return tracker.Role == InternalSessionRole.ListenHost || tracker.Role == InternalSessionRole.Client;
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

        /// <summary>
        /// Puts the invite code on the clipboard, so the host pastes it to its players instead of copying it out by hand. Only
        /// the host's own click does this; the code reaches no toast or log.
        /// </summary>
        public void CopyInvite()
        {
            var invite = HostedInvite();
            if (invite == null) return;
            try
            {
                context.Environment.CopyToClipboard(invite.Display);
            }
            catch (Exception)
            {
                // Nothing the clipboard throws may carry the code into the kit's log of failed clicks.
                context.Notify(InviteNotCopied, ScreenTone.Warning);
                return;
            }

            context.Notify(InviteCopied, ScreenTone.Success);
        }

        public void Kick()
        {
            var target = Moderatable();
            if (target == null) return;
            context.Ask(PrivacyPrompts.Destructive("Remove " + target.Name + "?", target.Name + " leaves the session and can join again.", "REMOVE", confirmed =>
            {
                if (confirmed) Act(target, control => control.Kick(new ParticipantId(target.Id), KickMessage), "Removed " + target.Name + " from the session.");
            }, context.IsHosting));
        }

        public void Ban()
        {
            var target = Moderatable();
            if (target == null) return;
            context.Ask(PrivacyPrompts.Destructive("Ban " + target.Name + "?", target.Name + " leaves the session and cannot join it again while you host.", "BAN", confirmed =>
            {
                if (confirmed) Act(target, control => control.Ban(new ParticipantId(target.Id)), "Banned " + target.Name + " from the session.");
            }, context.IsHosting));
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
            }, context.IsHosting));
        }

        /// <summary>A joined player's game runs the host's world, which it cannot keep alone, so leaving returns it to the main menu (WS3.8).</summary>
        internal const string LeaveBody = "You leave the session and return to the main menu.";

        public void Leave()
        {
            if (!CanLeave) return;
            context.Ask(PrivacyPrompts.Destructive("Leave server?", LeaveBody, "LEAVE", confirmed =>
            {
                if (confirmed) context.LeaveOnline("You left the session.");
            }, context.IsJoined));
        }

        private PlayerRow? SelectedRow()
        {
            var current = selected;
            if (current == null || !PlayersVisible) return null;
            var row = Find(current.Id);
            return row != null && keys.TryGetValue(current.Id, out var key) && key.Equals(current.PlayerKey) ? row : null;
        }

        private Selection? Moderatable() => SelectedRow()?.CanModerate == true ? selected : null;

        private InternalInvite? HostedInvite() =>
            context.Status.Role == InternalSessionRole.ListenHost ? context.Status.Status.Internet.Invite : null;

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
