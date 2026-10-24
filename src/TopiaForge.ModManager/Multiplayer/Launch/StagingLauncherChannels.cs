using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The launcher's channels in this game's manager staging (<see cref="ILauncherChannels"/>), over the store #216's readers
    /// and writers guard: immediate children of staging only, regular files only, bounded strict UTF-8, atomic replacement,
    /// takes that claim before they read, and stale claims swept. Every method touches the disk, and a refused write or a
    /// locked journal sleeps between tries, so the screens call them from the background queue or the live-join poll only.
    /// </summary>
    internal sealed class StagingLauncherChannels : ILauncherChannels
    {
        private readonly LaunchStagingStore store;

        /// <param name="requestId">The launch request this process was started with; null when no launcher started it.</param>
        /// <param name="packageSetDigest">The package-set digest of the profile this process runs; null without a request.</param>
        /// <param name="serviceOrigin">The TopiaForge service this game reaches, if any.</param>
        internal StagingLauncherChannels(LaunchStagingStore store, string? requestId, string? packageSetDigest, string? serviceOrigin)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (requestId != null) _ = LaunchStorageKeys.Request(requestId);
            RequestId = requestId;
            PackageSetDigest = packageSetDigest;
            ServiceOrigin = serviceOrigin;
        }

        public string? RequestId { get; }

        public string? PackageSetDigest { get; }

        public string? ServiceOrigin { get; }

        public MultiplayerPreferencesProjection? ReadPreferences() => store.ReadPreferences();

        public MultiplayerPreferencesJournal? ReadPreferencesJournal() => store.ReadPreferencesJournal();

        public MultiplayerPreferencesJournal RecordPreferenceChange(Func<int, MultiplayerPreferenceChange> change) => store.RecordPreferenceChange(change);

        public void WriteHandoff(SessionHandoff handoff) => store.WriteSessionHandoff(handoff);

        public SessionJoinRequest? TakeJoin() => RequestId == null ? null : store.TakeSessionJoin(RequestId);

        public void WriteProgress(LaunchProgress progress) => store.WriteProgress(progress);

        public void WriteOutcome(LaunchOutcome outcome) => store.WriteOutcome(outcome);
    }
}
