using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Where a joined player's game stands in following its host's world (WS3.8), as the connecting overlay shows it.</summary>
    internal enum FollowStage
    {
        /// <summary>Not joined, so nothing is followed.</summary>
        None = 0,

        /// <summary>Joined; waiting for the host to say which world it is in.</summary>
        AwaitingHost = 1,

        /// <summary>The follow's scopes are made: the session's mods get ready for the host's world.</summary>
        Preparing = 2,

        /// <summary>The host's world loads here, through the same provider as on the host.</summary>
        LoadingWorld = 3,

        /// <summary>The host's world arrives: the network world's baseline and the replicas it builds.</summary>
        Synchronizing = 4,

        /// <summary>This game's own player's participant controller starts.</summary>
        Participant = 5,

        /// <summary>This game runs the host's world session.</summary>
        Running = 6,

        /// <summary>The host is in a level without a world session; this game loads the same level.</summary>
        LoadingLevel = 7,

        /// <summary>This game is in the level the host is in, outside a world session, as the host is.</summary>
        InLevel = 8,

        /// <summary>The host is at the main menu; this game waits there, joined.</summary>
        AtMenu = 9,

        /// <summary>The player left the session, and this game returns to the main menu.</summary>
        ReturningToMenu = 10,

        /// <summary>Following the host's world failed, so this game left the session.</summary>
        Failed = 11
    }

    /// <summary>Why following the host's world failed. Each has its own words (<see cref="FollowFailureWords"/>).</summary>
    internal enum FollowFailureKind
    {
        None = 0,
        StartedGameWhileJoining,
        NotInstalled,
        WorldNotFound,
        Differs,
        ModFailed,
        Unreadable,
        PrepareFailed,
        BaseLevelFailed,
        WorldLoadFailed,
        SynchronizeFailed,
        ParticipantFailed,
        KeepsLeaving,
        CleanupFailed
    }

    /// <summary>A failure as the player reads it: this game's own words, never a provider's, a mod's or the host's text.</summary>
    internal sealed class FollowFailure
    {
        internal FollowFailure(FollowFailureKind kind, ModErrorCode code, string title, string message, bool retryable, ConnectingStep step)
        {
            Kind = kind;
            Code = code;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Retryable = retryable;
            Step = step;
        }

        internal FollowFailureKind Kind { get; }

        internal ModErrorCode Code { get; }

        internal string Title { get; }

        internal string Message { get; }

        /// <summary>Gets whether joining again can help, so the failure view offers RETRY.</summary>
        internal bool Retryable { get; }

        /// <summary>Gets the connecting overlay's step the failure stops at.</summary>
        internal ConnectingStep Step { get; }

        internal FailureMessage ToFailureMessage() => new FailureMessage(Title, Message, Step, Retryable);
    }

    /// <summary>
    /// The words of every way following the host's world can fail. Each is a fixed sentence that says what happened and what
    /// to do next, chosen by the failure and, where it helps the player, by its error code. The reason a provider, a mod or
    /// the native loader gave is never shown: it goes to the manager log, redacted.
    /// </summary>
    internal static class FollowFailureWords
    {
        internal const string LevelNotLoaded = "Your game could not load the level the host is in. You stay in the session and join the host's next world.";
        internal const string MenuLeft = "You returned to the main menu, so you left the session.";
        internal const string Rejoined = "Back in the host's world.";

        internal static FollowFailure For(FollowFailureKind kind, ModErrorCode code)
        {
            switch (kind)
            {
                case FollowFailureKind.StartedGameWhileJoining:
                    return Make(kind, code, "You started a game while joining",
                        "Your game kept running, so the join stopped. Choose RETRY to leave your game and join.", true);
                case FollowFailureKind.NotInstalled:
                    return Make(kind, code, "The host's world is not available",
                        "This game does not have the host's game or world. Install the same mods as the host, then join again.", false);
                case FollowFailureKind.WorldNotFound:
                    return Make(kind, code, "The host's world was not found",
                        "This game could not find the host's world among its own worlds. Check that your game is the same build as the host's, then join again.", true);
                case FollowFailureKind.Differs:
                    return Make(kind, code, "Your game differs from the host's",
                        "Your mods would start the host's game differently. Use the same mods as the host, then join again.", false);
                case FollowFailureKind.ModFailed:
                    return Make(kind, code, "A mod did not start in this game",
                        "A mod the host's game needs did not start in this game. Check the TopiaForge log, fix that mod, then join again.", false);
                case FollowFailureKind.Unreadable:
                    return Make(kind, code, "The host's world could not be read",
                        "The host sent a world this game cannot read. Make sure you and the host run the same version of TopiaForge, then join again.", false);
                case FollowFailureKind.PrepareFailed:
                    return Make(kind, code, "The host's world could not be prepared",
                        "This game could not get ready for the host's world. Join again; if it keeps failing, check your mods.", true);
                case FollowFailureKind.BaseLevelFailed:
                    return code == ModErrorCode.NotFound
                        ? Make(kind, code, "The host's level is not in this game",
                            "This game does not have the level the host's world is built on. Check that your game is the same build as the host's, then join again.", false)
                        : code == ModErrorCode.TimedOut
                            ? Make(kind, code, "The host's level took too long to load",
                                "Your game took too long to load the level the host's world is built on. Join again.", true)
                            : Make(kind, code, "The host's level could not be loaded",
                                "Your game could not load the level the host's world is built on. Join again; if it keeps failing, check your mods.", true);
                case FollowFailureKind.WorldLoadFailed:
                    return code == ModErrorCode.NotFound
                        ? Make(kind, code, "Part of the host's world is missing",
                            "This game is missing part of the host's world. Check that your game is the same build as the host's, then join again.", false)
                        : code == ModErrorCode.TimedOut
                            ? Make(kind, code, "The host's world took too long to load",
                                "Your game took too long to load the host's world. Join again.", true)
                            : code == ModErrorCode.Conflict
                                ? Make(kind, code, "The host's world could not be loaded",
                                    "Your game could not tell which level the host's world uses. Check that your game is the same build as the host's, then join again.", false)
                                : Make(kind, code, "The host's world could not be loaded",
                                    "Your game could not load the host's world. Join again; if it keeps failing, check your mods.", true);
                case FollowFailureKind.SynchronizeFailed:
                    return Make(kind, code, "The host's world did not arrive",
                        "The host's world did not finish arriving. Join again.", true);
                case FollowFailureKind.ParticipantFailed:
                    return Make(kind, code, "Your controls could not start",
                        "This game could not start your part of the host's game. Join again; if it keeps failing, tell the gamemode's author.", true);
                case FollowFailureKind.KeepsLeaving:
                    return Make(kind, code, "Your game keeps leaving the host's world",
                        "Something in this game keeps loading another level, so it cannot stay in the host's world. Leave the session, check your mods, then join again.", false);
                case FollowFailureKind.CleanupFailed:
                    return Make(kind, code, "Your previous world did not close",
                        "Your game could not close the world it was in. Restart the game, then join again.", false);
                default:
                    return Make(FollowFailureKind.PrepareFailed, code, "The host's world could not be prepared",
                        "This game could not get ready for the host's world. Join again; if it keeps failing, check your mods.", true);
            }
        }

        private static FollowFailure Make(FollowFailureKind kind, ModErrorCode code, string title, string message, bool retryable) =>
            new FollowFailure(kind, code, title, message, retryable, kind == FollowFailureKind.SynchronizeFailed ? ConnectingStep.Synchronizing
                : kind == FollowFailureKind.ParticipantFailed ? ConnectingStep.Spawning : ConnectingStep.LoadingWorld);
    }

    /// <summary>
    /// Where following the host's world stands, for the connecting overlay. The session follower owns it and changes it only
    /// when the follow moves, and <see cref="Version"/> moves with every change, so a view that reads it each frame
    /// allocates nothing. Main thread only.
    /// </summary>
    internal sealed class SessionFollowStatus
    {
        /// <summary>Gets a status that never follows anything, for a game without a session follower.</summary>
        internal static SessionFollowStatus None { get; } = new SessionFollowStatus();

        internal int Version { get; private set; }

        internal FollowStage Stage { get; private set; }

        /// <summary>Gets whether this follow came after the join had completed: the host changed worlds, or this game left the host's world.</summary>
        internal bool IsRefollow { get; private set; }

        /// <summary>Gets the line the connecting overlay shows under its step: this game's own words.</summary>
        internal string Detail { get; private set; } = string.Empty;

        /// <summary>Gets the step of a two-step load, the host's level then its world, or zero.</summary>
        internal int Step { get; private set; }

        internal int Steps { get; private set; }

        /// <summary>Gets which join this status speaks for: it moves each time this game links up to a host.</summary>
        internal int JoinSerial { get; private set; }

        /// <summary>Gets the failure that ended the join of <see cref="JoinSerial"/>, or null.</summary>
        internal FollowFailure? Failure { get; private set; }

        /// <summary>Gets the notice the follow gave last, such as returning to the main menu by itself, for the player to read once.</summary>
        internal string Notice { get; private set; } = string.Empty;

        /// <summary>Gets a number that moves with each notice, so a view shows each once.</summary>
        internal int NoticeVersion { get; private set; }

        internal void Joined()
        {
            JoinSerial++;
            Failure = null;
            IsRefollow = false;
            Move(FollowStage.AwaitingHost, "Asking the host for its world", 0, 0);
        }

        internal void Refollowing() => IsRefollow = true;

        /// <summary>Raised with each stage the follow moves to, even several in one frame, for diagnostics and tests.</summary>
        internal event Action<FollowStage>? Moved;

        internal void Move(FollowStage stage, string detail, int step = 0, int steps = 0)
        {
            var changed = stage != Stage;
            Stage = stage;
            Detail = detail ?? string.Empty;
            Step = step;
            Steps = steps;
            Version++;
            if (changed) Moved?.Invoke(stage);
        }

        internal void Fail(FollowFailure failure)
        {
            Failure = failure ?? throw new ArgumentNullException(nameof(failure));
            Move(FollowStage.Failed, failure.Title);
        }

        internal void Notify(string notice)
        {
            Notice = notice ?? string.Empty;
            NoticeVersion++;
            Version++;
        }

        internal void Ended()
        {
            IsRefollow = false;
            Move(FollowStage.None, string.Empty);
        }
    }
}
