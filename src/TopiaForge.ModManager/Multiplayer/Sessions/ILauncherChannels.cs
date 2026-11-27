using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The files this game shares with the TopiaForge launcher in manager staging (docs/internal/MultiplayerLaunchContract.md,
    /// sections 4 to 7): the preferences projection and the journal of what the player changed in game, the session handoff,
    /// live joins, and a live join's progress and outcomes. Every method touches the disk, and a refused write or a locked
    /// journal sleeps between tries, for about 310 ms in all, so the screens call them only from the background queue or the
    /// live-join poll, never from a frame. The properties are plain values a frame may read.
    /// </summary>
    internal interface ILauncherChannels
    {
        /// <summary>
        /// Gets the launch request this process was started with, which the launcher that owns the process keys its channels
        /// by; null when no launcher started this game, so it takes no live join and hands joins over by link instead.
        /// </summary>
        string? RequestId { get; }

        /// <summary>Gets the package-set digest of the profile this process runs, from its launch request; null without one.</summary>
        string? PackageSetDigest { get; }

        /// <summary>Gets the TopiaForge service this game reaches, if any: a live join for another service is refused.</summary>
        string? ServiceOrigin { get; }

        /// <summary>Reads the launcher's preferences projection; null when there is none or it cannot be read.</summary>
        MultiplayerPreferencesProjection? ReadPreferences();

        /// <summary>Reads the journal of preferences changed in game; null when there is none or it cannot be read.</summary>
        MultiplayerPreferencesJournal? ReadPreferencesJournal();

        /// <summary>Records one preference changed in game, for the launcher to take in, and returns the journal written.</summary>
        MultiplayerPreferencesJournal RecordPreferenceChange(Func<int, MultiplayerPreferenceChange> change);

        /// <summary>Hands the launcher that owns this process a join this game cannot make as it is.</summary>
        void WriteHandoff(SessionHandoff handoff);

        /// <summary>Takes the live join the launcher staged for this process, once; null when there is none, or it was foreign or malformed.</summary>
        SessionJoinRequest? TakeJoin();

        /// <summary>Writes a live join's progress, under its join id.</summary>
        void WriteProgress(LaunchProgress progress);

        /// <summary>Writes a live join's outcome, under its join id.</summary>
        void WriteOutcome(LaunchOutcome outcome);
    }
}
