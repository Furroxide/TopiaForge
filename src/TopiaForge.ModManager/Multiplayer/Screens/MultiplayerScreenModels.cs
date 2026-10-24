using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The Multiplayer tab's sub-tabs, in rail order.</summary>
    internal enum MultiplayerSubTab
    {
        Browse = 0,
        Host = 1,
        Connect = 2,
        Voice = 3,
        Session = 4
    }

    /// <summary>The lists BROWSE shows.</summary>
    internal enum BrowseList
    {
        Lan = 0,
        Internet = 1,
        Favorites = 2,
        History = 3
    }

    /// <summary>What VOICE says about the session, while the voice slice's settings are still to come.</summary>
    internal static class VoiceScreenText
    {
        /// <summary>The line VOICE shows while this game hosts, or has joined, a session that offers no voice chat.</summary>
        internal const string NotOffered = "This session offers no voice chat.";
    }

    /// <summary>A semantic tone the views turn into the kit's own; screens never name colors.</summary>
    internal enum ScreenTone
    {
        Neutral = 0,
        Muted = 1,
        Success = 2,
        Warning = 3,
        Danger = 4
    }

    /// <summary>Whether this game's mods fit a listed server.</summary>
    internal enum ServerReadiness
    {
        /// <summary>Not checked yet, or not reachable for a check.</summary>
        Unknown = 0,
        Checking = 1,
        Ready = 2,

        /// <summary>Some mods must be installed, changed or disabled first; the launcher can do that.</summary>
        Needs = 3,

        /// <summary>The server cannot be joined from this game at all, as with another game build.</summary>
        Incompatible = 4
    }

    /// <summary>Where identifying this game's mods for multiplayer stands, as HOST and BROWSE show it.</summary>
    internal enum ContentReadinessState
    {
        Pending = 0,
        Ready = 1,
        Unreadable = 2
    }

    /// <summary>What a pending prompt asks.</summary>
    internal enum ScreenPromptKind
    {
        /// <summary>The first-time address disclosure: one button, and closing it answers no.</summary>
        Disclosure = 0,

        /// <summary>The per-session prompt for a session that can only be reached directly.</summary>
        DirectConnection = 1,

        /// <summary>A destructive confirmation, shown through the kit's destructive modal.</summary>
        Destructive = 2,

        /// <summary>A session password typed in game, in a masked field; the answer carries it once and nothing keeps it.</summary>
        Password = 3
    }

    /// <summary>One row of a server list. Every text is display-ready: host-authored words are redacted and bounded.</summary>
    internal sealed class ServerRow
    {
        internal ServerRow(
            string key,
            bool locked,
            string name,
            string gamemode,
            string world,
            string players,
            int playerCount,
            int? pingMilliseconds,
            ServerReadiness readiness,
            string readinessText)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Locked = locked;
            Name = name ?? string.Empty;
            Gamemode = gamemode ?? string.Empty;
            World = world ?? string.Empty;
            Players = players ?? string.Empty;
            PlayerCount = playerCount;
            PingMilliseconds = pingMilliseconds;
            Readiness = readiness;
            ReadinessText = readinessText ?? string.Empty;
        }

        /// <summary>Gets the row's stable key: the server id, never an address.</summary>
        internal string Key { get; }

        internal bool Locked { get; }

        internal string LockText => Locked ? "Password" : "Open";

        internal string Name { get; }

        internal string Gamemode { get; }

        internal string World { get; }

        /// <summary>Gets the player count as shown, such as "3/8".</summary>
        internal string Players { get; }

        internal int PlayerCount { get; }

        /// <summary>Gets the round trip, rounded to 10 ms, or null when unknown.</summary>
        internal int? PingMilliseconds { get; }

        internal ServerReadiness Readiness { get; }

        /// <summary>Gets the mods column: "Ready", "Needs 2", "Incompatible" or "Checking".</summary>
        internal string ReadinessText { get; }
    }

    /// <summary>One player in the SESSION list. Only a session that is Ready shows any.</summary>
    internal sealed class PlayerRow
    {
        internal PlayerRow(string key, string name, bool isHost, bool isLocal, int? pingMilliseconds, bool muted, bool canModerate)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Name = name ?? string.Empty;
            IsHost = isHost;
            IsLocal = isLocal;
            PingMilliseconds = pingMilliseconds;
            Muted = muted;
            CanModerate = canModerate;
        }

        /// <summary>Gets the participant's key, for actions; it is never shown.</summary>
        internal string Key { get; }

        /// <summary>Gets the player's name, redacted and bounded, shown only as a name.</summary>
        internal string Name { get; }

        internal bool IsHost { get; }

        internal bool IsLocal { get; }

        internal string RoleText => IsHost ? "Host" : IsLocal ? "You" : string.Empty;

        internal int? PingMilliseconds { get; }

        internal bool Muted { get; }

        /// <summary>Gets whether this player's host may kick, ban or mute them: a Ready session, the host, and not themselves.</summary>
        internal bool CanModerate { get; }
    }

    /// <summary>A toast a screen asks for: an action's result.</summary>
    internal readonly struct ScreenNotice
    {
        internal ScreenNotice(string text, ScreenTone tone)
        {
            Text = text ?? string.Empty;
            Tone = tone;
        }

        internal string Text { get; }

        internal ScreenTone Tone { get; }
    }

    /// <summary>A host-policy control that a later slice provides: named here so the HOST view shows it as not yet available.</summary>
    internal sealed class HostPolicySeam
    {
        internal HostPolicySeam(string label, string note)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Note = note ?? string.Empty;
        }

        internal string Label { get; }

        internal string Note { get; }
    }

    /// <summary>
    /// A question a screen asks before it acts. The view shows it as a modal and calls <see cref="Answer(bool)"/> once: yes for
    /// the confirming button, no for cancel, closing or dismissing. Nothing the question guards has happened before the answer.
    /// A question about a game or a session holds only while that game or session does: once it does not, the screens
    /// withdraw it, which answers no, and the view closes its modal. Prompts persist across scene loads, so without that a
    /// question would outlive its game and act on the next one.
    /// </summary>
    internal sealed class ScreenPrompt
    {
        private readonly Action<bool> answer;
        private readonly Func<bool>? holds;
        private bool answered;
        private bool withdrawalTaken;
        private string value = string.Empty;

        /// <param name="holds">Whether the question still applies, asked every frame it waits; null for one that always does. It must not allocate.</param>
        internal ScreenPrompt(ScreenPromptKind kind, string title, IReadOnlyList<string> paragraphs, string confirmLabel, string cancelLabel, Action<bool> answer, Func<bool>? holds = null)
        {
            if (!Enum.IsDefined(typeof(ScreenPromptKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Kind = kind;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Paragraphs = paragraphs ?? throw new ArgumentNullException(nameof(paragraphs));
            ConfirmLabel = confirmLabel ?? throw new ArgumentNullException(nameof(confirmLabel));
            CancelLabel = cancelLabel ?? string.Empty;
            this.answer = answer ?? throw new ArgumentNullException(nameof(answer));
            this.holds = holds;
        }

        internal ScreenPromptKind Kind { get; }

        internal string Title { get; }

        internal IReadOnlyList<string> Paragraphs { get; }

        internal string ConfirmLabel { get; }

        /// <summary>Gets the cancel button's label; empty when the prompt has one button, as the disclosure does.</summary>
        internal string CancelLabel { get; }

        internal bool IsAnswered => answered;

        /// <summary>
        /// Gets whether the question still applies: the game or session it asks about is still the one it asked about. One
        /// that cannot tell counts as not applying, so nothing acts on a guess.
        /// </summary>
        internal bool StillHolds
        {
            get
            {
                if (holds == null) return true;
                try
                {
                    return holds();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Gets whether the question was withdrawn because it stopped applying, rather than answered.</summary>
        internal bool IsWithdrawn { get; private set; }

        /// <summary>
        /// Answers the prompt; only the first answer counts. A yes to a question that stopped applying counts as no, and as
        /// a withdrawal: a click can land in the frame its game ended, before the screens withdraw the question.
        /// </summary>
        internal void Answer(bool confirmed)
        {
            if (answered) return;
            if (confirmed && !StillHolds)
            {
                IsWithdrawn = true;
                confirmed = false;
            }

            answered = true;
            answer(confirmed);
        }

        /// <summary>
        /// Answers a prompt that asks for text, as <see cref="ScreenPromptKind.Password"/> does, with what the player typed. The
        /// text reaches the question once, through <see cref="TakeValue"/>; a no carries none.
        /// </summary>
        internal void Answer(bool confirmed, string typed)
        {
            if (answered) return;
            value = confirmed ? typed ?? string.Empty : string.Empty;
            Answer(confirmed);
            value = string.Empty;
        }

        /// <summary>Takes what the player typed, once, while the answer runs; nothing keeps it after.</summary>
        internal string TakeValue()
        {
            var typed = value;
            value = string.Empty;
            return typed;
        }

        /// <summary>Withdraws the question unanswered: it counts as no, so nothing it guards happens. False when it was answered.</summary>
        internal bool Withdraw()
        {
            if (answered) return false;
            IsWithdrawn = true;
            answered = true;
            answer(false);
            return true;
        }

        /// <summary>Takes the news that the question was withdrawn, once, so the screens tell the player that nothing was done.</summary>
        internal bool TakeWithdrawal()
        {
            if (!IsWithdrawn || withdrawalTaken) return false;
            withdrawalTaken = true;
            return true;
        }
    }
}
