using System;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Voice chat in the game, put together over Unity and the plugin: the VOICE page's presenter and the runtime behind it,
    /// the microphone, push-to-talk's keyboard and mouse, the game's bindings, the kit's Escape stack, and the emitters. It
    /// ticks every frame, overlay open or not, in the overlay's guarded frame after the sessions presenter, so push-to-talk
    /// works while the player plays. The game's own recording reaches it through the runtime's microphone arbiter, which the
    /// plugin points at the game before any mod loads. The switch and the consent are the launcher's preferences, read from
    /// manager staging and changed through its journal; the other settings are kept in manager data. Main thread only.
    /// </summary>
    internal sealed class MultiplayerVoiceChat : IDisposable
    {
        private readonly TopiaForgeModManagerPlugin plugin;
        private readonly UnityVoiceEmitters emitters = new UnityVoiceEmitters();
        private readonly VoiceChatRuntime runtime;
        private readonly VoicePresenter presenter;
        private IInternalMultiplayerControl? sessionOwner;
        private IMultiplayerSession? session;
        private bool disposed;

        internal MultiplayerVoiceChat(TopiaForgeModManagerPlugin plugin)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            var arbiter = plugin.MicrophoneArbiter ?? throw new InvalidOperationException("The runtime's microphone arbiter is not ready.");
            var microphone = new UnityVoiceMicrophone();
            var input = new UnityVoiceInput();
            runtime = new VoiceChatRuntime(Control, Snapshot, arbiter, microphone, input, emitters);
            var queue = new MultiplayerBackgroundQueue(MultiplayerWorkSchedulers.Background);
            var staging = plugin.LaunchStaging;
            var preferences = new VoicePreferences(queue, new FileVoiceSettingsStore(plugin.Paths.Data), staging == null ? null : new StagingVoiceChannel(staging));
            presenter = new VoicePresenter(runtime, preferences, queue, microphone, input, new UnityVoiceNativeBindings(), new UnityVoiceEscape(), Control,
                Snapshot);
        }

        /// <summary>Gets what the VOICE page shows.</summary>
        internal IVoiceScreen Screen => presenter;

        /// <summary>Reads the player's choices, so voice chat works before VOICE ever opens.</summary>
        internal void Start() => presenter.Start();

        /// <summary>Main thread, every frame: the player's choices reach the runtime, which then runs the microphone and the voices.</summary>
        internal void Tick(float deltaTime)
        {
            if (disposed) return;
            presenter.Tick(deltaTime);
            runtime.Tick(deltaTime);

            // A result can name a player by the name they chose, so toasts render as plain text.
            while (presenter.TryTakeNotice(out var notice))
            {
                TopiaForgeToasts.ShowPlain(notice.Text, MultiplayerViewKit.Tone(notice.Tone), notice.Tone == ScreenTone.Warning ? 5f : 3.5f);
            }
        }

        /// <summary>Closes the microphone, unless another use holds it, and stops every voice: the game is closing.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                presenter.Stop();
            }
            finally
            {
                try
                {
                    runtime.Dispose();
                }
                finally
                {
                    emitters.Dispose();
                }
            }
        }

        private IInternalMultiplayerControl? Control() => plugin.Multiplayer?.Control;

        private MultiplayerSessionSnapshot? Snapshot()
        {
            var control = Control();
            if (!ReferenceEquals(control, sessionOwner))
            {
                sessionOwner = control;
                session = control == null ? null : plugin.GetMultiplayerSession();
            }

            return session?.Snapshot;
        }
    }
}
