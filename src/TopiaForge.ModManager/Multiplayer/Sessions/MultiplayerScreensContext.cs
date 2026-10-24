using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What the Multiplayer tab's presenters share: the environment, the settings and the launcher's preferences, status,
    /// content readiness, prompts, and the way joins reach the launcher.
    /// </summary>
    internal sealed class MultiplayerScreensContext
    {
        private readonly Action<ScreenPrompt> ask;
        private readonly Action<string, ScreenTone> notify;

        internal MultiplayerScreensContext(
            IMultiplayerSessionEnvironment environment,
            MultiplayerSettingsService settings,
            MultiplayerPreferencesService preferences,
            MultiplayerStatusTracker status,
            ContentReadinessPresenter content,
            MultiplayerBackgroundQueue queue,
            Action<ScreenPrompt> ask,
            Action<string, ScreenTone> notify)
        {
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
            Status = status ?? throw new ArgumentNullException(nameof(status));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            this.ask = ask ?? throw new ArgumentNullException(nameof(ask));
            this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
            Privacy = new PrivacyGate(settings, ask);
            Handoffs = new LauncherHandoffs(environment, queue ?? throw new ArgumentNullException(nameof(queue)), notify);
            IsHosting = () => Status.Role == InternalSessionRole.ListenHost;
            IsJoined = () => Status.Role == InternalSessionRole.Client;
        }

        internal IMultiplayerSessionEnvironment Environment { get; }

        internal MultiplayerSettingsService Settings { get; }

        /// <summary>Gets the launcher's preferences this game shows and changes: the display name and the hosting defaults.</summary>
        internal MultiplayerPreferencesService Preferences { get; }

        /// <summary>Gets how joins reach the TopiaForge launcher: a handoff when it owns this game, a link otherwise.</summary>
        internal LauncherHandoffs Handoffs { get; }

        internal MultiplayerStatusTracker Status { get; }

        internal ContentReadinessPresenter Content { get; }

        internal PrivacyGate Privacy { get; }

        /// <summary>Gets whether this game hosts a session now: what a question about that session holds while. Made once, so asking allocates nothing.</summary>
        internal Func<bool> IsHosting { get; }

        /// <summary>Gets whether this game is joined to a session now: what a question about that session holds while.</summary>
        internal Func<bool> IsJoined { get; }

        /// <summary>
        /// Gets whether this game reaches a TopiaForge service: then Public and Invite sessions, invites and listed servers
        /// work, through the relay unless the player allows a direct connection. Without one, only LAN and direct play exist.
        /// </summary>
        internal bool HasService => Status.Status.Internet.ServiceConfigured;

        /// <summary>Gets whether the player may start anything: live sessions are on, and the settings and preferences have loaded.</summary>
        internal bool CanAct => Status.IsLiveAvailable && Settings.IsLoaded && Preferences.IsLoaded;

        /// <summary>Gets whether hosting or joining may start: as <see cref="CanAct"/>, with this game's mods identified.</summary>
        internal bool CanStartSession => CanAct && Content.IsReady;

        internal void Ask(ScreenPrompt prompt) => ask(prompt);

        internal void Notify(string text, ScreenTone tone = ScreenTone.Neutral) => notify(text, tone);

        /// <summary>
        /// Leaves through the runtime, which never refuses: it stops hosting, or a join, on its way. A provider that failed
        /// while leaving is said in this game's own words, after <paramref name="done"/>; the session is over either way.
        /// </summary>
        internal void LeaveOnline(string done)
        {
            var change = Environment.Leave();
            Status.RequestRefresh();
            if (change.Succeeded) Notify(done);
            else Notify(done + " " + HostText.ForDisplay(change.Result.ErrorMessage, HostText.LineChars), ScreenTone.Warning);
        }
    }
}
