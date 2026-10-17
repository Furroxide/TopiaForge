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
        Destructive = 2
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
    /// A question a screen asks before it acts. The view shows it as a modal and calls <see cref="Answer"/> once: yes for the
    /// confirming button, no for cancel, closing or dismissing. Nothing the question guards has happened before the answer.
    /// </summary>
    internal sealed class ScreenPrompt
    {
        private readonly Action<bool> answer;
        private bool answered;

        internal ScreenPrompt(ScreenPromptKind kind, string title, IReadOnlyList<string> paragraphs, string confirmLabel, string cancelLabel, Action<bool> answer)
        {
            if (!Enum.IsDefined(typeof(ScreenPromptKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Kind = kind;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Paragraphs = paragraphs ?? throw new ArgumentNullException(nameof(paragraphs));
            ConfirmLabel = confirmLabel ?? throw new ArgumentNullException(nameof(confirmLabel));
            CancelLabel = cancelLabel ?? string.Empty;
            this.answer = answer ?? throw new ArgumentNullException(nameof(answer));
        }

        internal ScreenPromptKind Kind { get; }

        internal string Title { get; }

        internal IReadOnlyList<string> Paragraphs { get; }

        internal string ConfirmLabel { get; }

        /// <summary>Gets the cancel button's label; empty when the prompt has one button, as the disclosure does.</summary>
        internal string CancelLabel { get; }

        internal bool IsAnswered => answered;

        /// <summary>Answers the prompt; only the first answer counts.</summary>
        internal void Answer(bool confirmed)
        {
            if (answered) return;
            answered = true;
            answer(confirmed);
        }
    }
}
