using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    internal delegate bool ExecuteChatCommand(string name, IReadOnlyList<string> arguments, out OperationResult<string>? result);

    /// <summary>Chat composition, local command dispatch, link status and bounded membership notices. Unity-free.</summary>
    internal sealed class InSessionPresenter : IInSessionScreen, IDisposable
    {
        private readonly MultiplayerScreensContext context;
        private readonly ISessionScreen session;
        private readonly ExecuteChatCommand execute;
        private readonly SessionChat chat;
        private readonly Dictionary<MultiplayerPlayerKey, string> members = new Dictionary<MultiplayerPlayerKey, string>();
        private readonly Dictionary<MultiplayerPlayerKey, string> nextMembers = new Dictionary<MultiplayerPlayerKey, string>();
        private MultiplayerSessionSnapshot? seen;
        private MultiplayerSessionId sessionId;
        private ChatRateLimit notices;
        private int joined;
        private int left;
        private bool initialized;
        private double lostSeconds;
        private int revision;

        internal InSessionPresenter(MultiplayerScreensContext context, ISessionScreen session, ExecuteChatCommand execute, SessionChat? chat = null)
        {
            this.context = context;
            this.session = session;
            this.execute = execute;
            this.chat = chat ?? new SessionChat();
        }

        public bool IsAvailable => chat.IsAvailable;
        public bool IsOpen { get; private set; }
        /// <summary>Presence (3.10) reads this local-only seam; it needs no mod author code and carries no text.</summary>
        public bool IsTyping { get; private set; }
        public int Revision => chat.Revision;
        public bool IsReconnecting => lostSeconds >= 1.5;
        public string ConnectionText => IsReconnecting ? "Reconnecting..." : context.Status.Role == InternalSessionRole.ListenHost ? "Hosting" : "Connected";

        internal void Tick(float deltaTime)
        {
            chat.Tick(context.Environment.Control, context.Status.Snapshot);
            if (revision != chat.Revision || !IsAvailable)
            {
                revision = chat.Revision;
                Close();
            }
            var lost = IsAvailable && context.Status.Role == InternalSessionRole.Client &&
                (context.Status.Status.JoinPhase == InternalJoinPhase.Reconnecting || LocalDisconnected());
            lostSeconds = lost ? lostSeconds + (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0 ? 0 : deltaTime) : 0;
            Membership();
            if ((joined > 0 || left > 0) && notices.Take(chat.Clock()))
            {
                context.Notify(joined + " joined, " + left + " left the session.");
                joined = left = 0;
            }
        }

        public void Open() { if (IsAvailable) IsOpen = true; }
        public void Close() { IsOpen = false; IsTyping = false; }
        public void SetTyping(bool focused) => IsTyping = IsOpen && IsAvailable && focused;
        public bool TryTakeLine(out ChatLine line) => chat.TryTakeLine(out line);

        public bool Submit(string text)
        {
            if (!IsAvailable || !IsOpen) return false;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) { Close(); return true; }
            if (text[0] == '/') return Command(text);
            var error = chat.Send(text);
            if (error.Length > 0) { chat.Feedback(error, true); return false; }
            Close();
            return true;
        }

        private bool Command(string text)
        {
            if (!ChatCommand.TryParse(text, out var name, out var args)) return Fail("Check the command: use /help and close any quoted argument.");
            switch (name.ToLowerInvariant())
            {
                case "help":
                    chat.Feedback("/help; host: /kick, /ban, /mute followed by a player name in quotes. Use PLAYERS for duplicate names. Mod commands: /mod:command arguments. /mute toggles the server mute.");
                    return true;
                case "kick":
                case "ban":
                case "mute":
                    if (!session.CanEnd) return Fail("Only the host can use moderation commands.");
                    if (args.Count != 1) return Fail("Give one player name in quotes. Use PLAYERS for duplicate names.");
                    PlayerRow? target = null;
                    for (var index = 0; index < session.Players.Count; index++)
                    {
                        var row = session.Players[index];
                        if (row.Key != args[0] && !string.Equals(row.Name, args[0], StringComparison.OrdinalIgnoreCase)) continue;
                        if (target != null) return Fail("That name matches more than one player. Use PLAYERS to select one.");
                        target = row;
                    }
                    if (target == null || !target.CanModerate) return Fail("That player cannot be moderated. Check PLAYERS and try again.");
                    session.Select(target.Key);
                    Close();
                    if (name.Equals("kick", StringComparison.OrdinalIgnoreCase)) session.Kick();
                    else if (name.Equals("ban", StringComparison.OrdinalIgnoreCase)) session.Ban();
                    else session.ToggleMute();
                    return true;
                default:
                    try
                    {
                        if (!execute(name, args, out var result)) return Fail("Unknown command. Use /help, or /mod:command for a registered mod command.");
                        if (result == null || !result.Succeeded) return Fail("The command could not run. Check its arguments and try again.");
                        if (result.TryGetValue(out var answer)) chat.Feedback(answer);
                        return true;
                    }
                    catch (Exception) { return Fail("The command could not run. Check its arguments and try again."); }
            }
        }

        private bool Fail(string text) { chat.Feedback(text, true); return false; }

        private bool LocalDisconnected()
        {
            var snapshot = context.Status.Snapshot;
            if (snapshot == null) return false;
            for (var index = 0; index < snapshot.Participants.Count; index++)
                if (snapshot.Participants[index].IsLocal) return !snapshot.Participants[index].IsConnected;
            return true;
        }

        private void Membership()
        {
            var snapshot = context.Status.Snapshot;
            if (!IsAvailable || snapshot == null)
            {
                initialized = false;
                members.Clear();
                joined = left = 0;
                seen = null;
                return;
            }
            if (ReferenceEquals(seen, snapshot)) return;
            seen = snapshot;
            if (!snapshot.Id.Equals(sessionId)) { initialized = false; members.Clear(); joined = left = 0; sessionId = snapshot.Id; }
            nextMembers.Clear();
            for (var index = 0; index < snapshot.Participants.Count; index++)
            {
                var player = snapshot.Participants[index];
                // A reconnecting player keeps their place; only an actual membership removal says they left.
                if (player.IsLocal || !player.PlayerKey.IsValid) continue;
                var name = members.TryGetValue(player.PlayerKey, out var known) ? known : HostText.ForDisplay(player.DisplayName, 32);
                nextMembers[player.PlayerKey] = name;
                if (initialized && !members.ContainsKey(player.PlayerKey)) Notice(name, true);
            }
            if (initialized) foreach (var member in members) if (!nextMembers.ContainsKey(member.Key)) Notice(member.Value, false);
            members.Clear();
            foreach (var member in nextMembers) members.Add(member.Key, member.Value);
            initialized = true;
        }

        private void Notice(string name, bool arriving)
        {
            if (notices.Take(chat.Clock())) context.Notify(name + (arriving ? " joined the session." : " left the session."));
            else if (arriving) joined = Math.Min(999, joined + 1);
            else left = Math.Min(999, left + 1);
        }

        public void Dispose() { Close(); chat.Dispose(); members.Clear(); nextMembers.Clear(); }
    }
}
