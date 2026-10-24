using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Voice chat in the game, every frame on the main thread: the microphone's gate, capture, other players' voices and the
    /// mutes. The microphone opens only after the player agreed to the current consent, while voice chat is on, and while
    /// the player transmits (push-to-talk held, or voice activation in a session that can hear them) or tests it; it goes
    /// through the runtime's microphone arbiter, where the game's own robot recording comes first and a robot conversation
    /// next, and voice chat yields to both. Everything the provider owns, resampling, voice activation, coding and the
    /// network, stays in the provider. A settled frame, idle or talking, allocates nothing. See docs/internal/MultiplayerVoice.md, part 4.
    /// </summary>
    internal sealed partial class VoiceChatRuntime : IDisposable
    {
        /// <summary>How long a microphone that would not open waits before it is tried again, in seconds.</summary>
        internal const float OpenRetrySeconds = 2f;

        /// <summary>The level the meter reads while the microphone is closed, in dBFS.</summary>
        internal const float SilenceDb = -96f;

        private readonly Func<IInternalMultiplayerControl?> control;
        private readonly Func<MultiplayerSessionSnapshot?> snapshot;
        private readonly MultiplayerMicrophoneArbiter arbiter;
        private readonly IVoiceInput input;
        private readonly VoiceCapture capture;
        private readonly InputBinding?[] bindings = { VoicePushToTalk.DefaultKey, VoicePushToTalk.DefaultMouseButton };
        private MultiplayerMicrophoneArbiter.Hold? hold;
        private IInternalVoiceService? applied;
        private bool appliedEnabled;
        private InternalVoiceTransmitMode appliedMode = (InternalVoiceTransmitMode)(-1);
        private float appliedThreshold = float.NaN;
        private bool? sentTransmitting;
        private long tick;
        private long enabledAtTick = long.MaxValue;
        private float sinceFailedOpen = OpenRetrySeconds;
        private float thresholdDb = -40f;
        private float micGain = 1f;
        private string? openedDevice;
        private bool openFailed;
        private bool disposed;

        internal VoiceChatRuntime(
            Func<IInternalMultiplayerControl?> control,
            Func<MultiplayerSessionSnapshot?> snapshot,
            MultiplayerMicrophoneArbiter arbiter,
            IVoiceMicrophone microphone,
            IVoiceInput input,
            IVoiceEmitters emitters)
        {
            this.control = control ?? throw new ArgumentNullException(nameof(control));
            this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            this.arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            capture = new VoiceCapture(microphone ?? throw new ArgumentNullException(nameof(microphone)));
            Playback = new VoicePlayback(emitters ?? throw new ArgumentNullException(nameof(emitters)));
        }

        /// <summary>Gets or sets whether the player agreed to the current wording of the voice consent.</summary>
        internal bool ConsentCurrent { get; set; }

        /// <summary>Gets or sets the player's voice chat switch, which counts only with consent.</summary>
        internal bool VoiceEnabled { get; set; }

        /// <summary>Gets whether voice chat is on: switched on, with consent to the current wording.</summary>
        internal bool IsOn => ConsentCurrent && VoiceEnabled;

        internal InternalVoiceTransmitMode Mode { get; set; } = InternalVoiceTransmitMode.PushToTalk;

        /// <summary>Gets or sets the voice-activation threshold, -96 to 0 dBFS; a value outside is clamped, and one that is no number ignored.</summary>
        internal float ThresholdDb
        {
            get => thresholdDb;
            set
            {
                if (!float.IsNaN(value)) thresholdDb = Math.Max(SilenceDb, Math.Min(0f, value));
            }
        }

        /// <summary>Gets or sets the microphone's gain, from 0 to 2; a value outside is clamped, and one that is no number ignored.</summary>
        internal float MicGain
        {
            get => micGain;
            set
            {
                if (!float.IsNaN(value)) micGain = Math.Max(0f, Math.Min(2f, value));
            }
        }

        /// <summary>Gets or sets the microphone to record from; null for the system's default.</summary>
        internal string? Device { get; set; }

        /// <summary>Gets or sets whether the microphone test runs: it opens the microphone while voice chat is on, and sends nothing.</summary>
        internal bool Testing { get; set; }

        /// <summary>Gets or sets whether push-to-talk is ignored, as while the player rebinds it.</summary>
        internal bool PushToTalkSuspended { get; set; }

        /// <summary>Gets other players' voices.</summary>
        internal VoicePlayback Playback { get; }

        /// <summary>Gets whom the player muted, by player key.</summary>
        internal VoiceMuteBook Mutes { get; } = new VoiceMuteBook();

        /// <summary>Gets what voice chat does now, as of the last frame.</summary>
        internal VoiceTalkStatus Status { get; private set; } = VoiceTalkStatus.Unavailable;

        /// <summary>Gets whether the microphone is open and read now.</summary>
        internal bool MicrophoneOpen => capture.IsCapturing;

        /// <summary>Gets whether the player transmits now, as last told to the voice service.</summary>
        internal bool Transmitting => sentTransmitting == true;

        /// <summary>Gets whether push-to-talk was held at the last frame.</summary>
        internal bool PushToTalkHeld { get; private set; }

        /// <summary>Gets the voice service as of the last frame, or null.</summary>
        internal IInternalVoiceService? Voice => applied;

        /// <summary>Gets the capture, for tests.</summary>
        internal VoiceCapture Capture => capture;

        /// <summary>Gets the microphone level the meter shows, in dBFS: the voice service's while the microphone is read, silence otherwise.</summary>
        internal float LevelDb
        {
            get
            {
                var voice = applied;
                if (voice == null || !capture.IsCapturing) return SilenceDb;
                try
                {
                    return voice.InputLevelDb;
                }
                catch (Exception)
                {
                    return SilenceDb;
                }
            }
        }

        /// <summary>Gets the bindings push-to-talk reads, slot by slot; null for a slot that has none.</summary>
        internal InputBinding? BindingOf(int slot) => bindings[slot];

        /// <summary>Sets the bindings push-to-talk reads, as <see cref="VoicePushToTalk.Resolve"/> made them.</summary>
        internal void SetBindings(InputBinding?[] resolved)
        {
            if (resolved == null || resolved.Length != VoicePushToTalk.SlotCount) throw new ArgumentException("Push-to-talk has two slots.", nameof(resolved));
            Array.Copy(resolved, bindings, bindings.Length);
        }

        /// <summary>Asks to try the microphone again at once, as after the player chose another device or refreshed the list.</summary>
        internal void RetryMicrophone()
        {
            sinceFailedOpen = OpenRetrySeconds;
            openFailed = false;
        }

        /// <summary>Main thread, every frame, after the provider's pump.</summary>
        internal void Tick(float deltaTime)
        {
            if (disposed) return;
            tick++;
            var current = ReadControl();
            var voice = ReadVoice(current);
            var role = ReadRole(current);
            var session = ReadSnapshot();
            if (voice == null)
            {
                Release();
                applied = null;
                Playback.StopAll();
                Status = VoiceTalkStatus.Unavailable;
                return;
            }

            if (!ReferenceEquals(voice, applied))
            {
                applied = voice;
                appliedEnabled = false;
                appliedMode = (InternalVoiceTransmitMode)(-1);
                appliedThreshold = float.NaN;
                sentTransmitting = null;
                enabledAtTick = long.MaxValue;
                Mutes.Invalidate();
            }

            var state = Apply(voice);

            // The game's own recording comes first, then a robot conversation. The arbiter reads the game as it is asked, here
            // as at every mod's claim, and counts a state it cannot read as recording, so a game update that moves it keeps
            // voice chat off rather than talking over the game.
            var busy = arbiter.IsHeldAbove(InternalMicrophoneUse.VoiceChat);
            var cannotCheck = arbiter.NativeUnreadable;

            // What the microphone recorded since the last frame goes first, under the state it was recorded in, so a release
            // keeps its last syllable. Nothing is read while another use holds the microphone: that is not voice chat's.
            if (!busy && capture.IsCapturing && capture.Pump(voice, MicGain) < 0)
            {
                // The recording stopped under voice chat, as when the device was unplugged; it is tried again shortly.
                openFailed = true;
                sinceFailedOpen = 0f;
            }

            // The service's state is a pump old: it counts only from the frame after voice went on, so a state read before
            // that, which says nothing of listeners, never opens the microphone.
            var settled = tick > enabledAtTick;
            var allows = IsOn && settled && role != InternalSessionRole.Solo &&
                         (state == InternalVoiceTransmitState.Idle || state == InternalVoiceTransmitState.Transmitting);
            PushToTalkHeld = !PushToTalkSuspended && Mode == InternalVoiceTransmitMode.PushToTalk && !input.IsTyping && AnyHeld();
            var talk = allows && (Mode == InternalVoiceTransmitMode.VoiceActivation || PushToTalkHeld);
            var want = IsOn && (Testing || talk);
            var transmit = !busy && !Testing && talk;
            if (sentTransmitting != transmit)
            {
                voice.SetTransmitting(transmit);
                sentTransmitting = transmit;
            }

            UpdateMicrophone(want, busy, deltaTime);
            Playback.Tick(voice, session);
            Mutes.Apply(voice, session, deltaTime);
            Status = StatusOf(state, role, want, busy, cannotCheck, transmit && capture.IsCapturing);
        }

        /// <summary>Closes the microphone, unless another use holds it, and stops every voice: the game is closing.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release();
            Playback.StopAll();
        }

        private InternalVoiceTransmitState Apply(IInternalVoiceService voice)
        {
            var on = IsOn;
            if (on != appliedEnabled || voice.Enabled != on)
            {
                voice.Enabled = on;
                appliedEnabled = on;
                enabledAtTick = on ? tick : long.MaxValue;
            }

            if (Mode != appliedMode)
            {
                voice.Mode = Mode;
                appliedMode = Mode;
            }

            if (!ThresholdDb.Equals(appliedThreshold))
            {
                voice.ActivationThresholdDb = ThresholdDb;
                appliedThreshold = ThresholdDb;
            }

            return voice.TransmitState;
        }

        private void UpdateMicrophone(bool want, bool busy, float deltaTime)
        {
            if (busy)
            {
                // Another use took the microphone: stop reading at once, and never end the device under it.
                ReleaseHold();
                capture.Abandon();
                return;
            }

            capture.EndAbandoned();
            if (!want)
            {
                ReleaseHold();
                capture.Close();
                return;
            }

            if (hold == null)
            {
                var acquired = arbiter.TryAcquire(InternalMicrophoneUse.VoiceChat, "Voice chat");
                if (!acquired.TryGetValue(out var taken)) return;
                hold = taken;
            }

            if (capture.IsCapturing)
            {
                if (string.Equals(Device, openedDevice, StringComparison.Ordinal)) return;

                // The player chose another microphone: the one open closes, and the chosen one opens now.
                capture.Close();
                openFailed = false;
            }

            sinceFailedOpen += float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : deltaTime;
            if (openFailed && sinceFailedOpen < OpenRetrySeconds) return;
            openedDevice = Device;
            openFailed = !capture.Open(Device);
            sinceFailedOpen = 0f;
        }

        private bool AnyHeld()
        {
            for (var slot = 0; slot < bindings.Length; slot++)
            {
                var binding = bindings[slot];
                if (binding.HasValue && input.IsHeld(binding.Value)) return true;
            }

            return false;
        }

        private void Release()
        {
            ReleaseHold();
            if (arbiter.IsHeldAbove(InternalMicrophoneUse.VoiceChat)) capture.Abandon();
            else
            {
                capture.EndAbandoned();
                capture.Close();
            }

            if (sentTransmitting == true && applied != null)
            {
                try
                {
                    applied.SetTransmitting(false);
                }
                catch (Exception)
                {
                    // A service that cannot answer, as one that stopped first, has nothing in flight to end.
                }
            }

            sentTransmitting = null;
        }

        private void ReleaseHold()
        {
            hold?.Release();
            hold = null;
        }
    }
}
