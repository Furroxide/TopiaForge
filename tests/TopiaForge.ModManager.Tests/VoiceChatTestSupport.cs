using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>A voice service that records what voice chat asked of it and answers what the test scripts. Steady frames allocate nothing.</summary>
    internal sealed class FakeVoiceService : IInternalVoiceService
    {
        private readonly List<ParticipantId> mutes = new List<ParticipantId>(16);
        private bool enabled;

        internal InternalVoiceTransmitState State { get; set; } = InternalVoiceTransmitState.Idle;

        internal Dictionary<ParticipantId, FakePlaybackStream> Streams { get; } = new Dictionary<ParticipantId, FakePlaybackStream>();

        internal HashSet<ParticipantId> Speaking { get; } = new HashSet<ParticipantId>();

        internal bool Transmitting { get; private set; }

        internal int TransmitChanges { get; private set; }

        internal int SubmitCalls { get; private set; }

        internal long Submitted { get; private set; }

        internal long SubmittedWhileTransmitting { get; private set; }

        internal int LastRate { get; private set; }

        internal int MuteCalls { get; private set; }

        internal bool HearsNobody { get; private set; }

        internal IReadOnlyList<ParticipantId> Mutes => mutes;

        internal int MadeStreams { get; private set; }

        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public InternalVoiceTransmitMode Mode { get; set; }

        public float ActivationThresholdDb { get; set; } = -40f;

        public float InputLevelDb { get; set; } = -96f;

        /// <summary>Gets or sets whether voice activation hears speech above its threshold now.</summary>
        internal bool Speech { get; set; }

        /// <summary>
        /// Gets the state as the service has it: Failed and NotOffered first, Idle while off, and Transmitting while frames go
        /// out, which push-to-talk sends whenever it transmits and voice activation only while it hears speech.
        /// </summary>
        public InternalVoiceTransmitState TransmitState =>
            State == InternalVoiceTransmitState.Failed || State == InternalVoiceTransmitState.NotOffered ? State
            : !enabled ? InternalVoiceTransmitState.Idle
            : State == InternalVoiceTransmitState.Idle && Transmitting && (Mode == InternalVoiceTransmitMode.PushToTalk || Speech) ? InternalVoiceTransmitState.Transmitting
            : State;

        public void SubmitCapture(float[] samples, int count, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            SubmitCalls++;
            Submitted += count;
            LastRate = sampleRate;
            if (Transmitting) SubmittedWhileTransmitting += count;
        }

        public void SetTransmitting(bool transmitting)
        {
            if (Transmitting != transmitting) TransmitChanges++;
            Transmitting = transmitting;
        }

        public IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker)
        {
            MadeStreams++;
            return new FakePlaybackStream(speaker) { IsEnded = true };
        }

        public bool TryGetPlaybackStream(ParticipantId speaker, out IVoicePlaybackStream? stream)
        {
            if (Streams.TryGetValue(speaker, out var found))
            {
                stream = found;
                return true;
            }

            stream = null;
            return false;
        }

        public bool IsSpeaking(ParticipantId speaker) => Speaking.Contains(speaker);

        public void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false)
        {
            MuteCalls++;
            mutes.Clear();
            foreach (var id in muted) mutes.Add(id);
            HearsNobody = muteAll;
        }

        public bool GetListenerMutes(ICollection<ParticipantId> into)
        {
            into.Clear();
            for (var index = 0; index < mutes.Count; index++) into.Add(mutes[index]);
            return HearsNobody;
        }

        public InternalVoiceStatistics GetStatistics() => new InternalVoiceStatistics();

        /// <summary>Forgets the names, as the engine does at a join that finds another host.</summary>
        internal void ForgetNames() => mutes.Clear();

        internal void Talks(ParticipantId speaker)
        {
            Speaking.Add(speaker);
            if (!Streams.ContainsKey(speaker)) Streams.Add(speaker, new FakePlaybackStream(speaker));
        }
    }

    internal sealed class FakePlaybackStream : IVoicePlaybackStream
    {
        internal FakePlaybackStream(ParticipantId speaker)
        {
            Speaker = speaker;
        }

        public ParticipantId Speaker { get; }

        public int SampleRate => 48_000;

        public bool IsEnded { get; set; }

        internal long SamplesRead { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            var sample = IsEnded ? 0f : 0.25f;
            for (var index = 0; index < count; index++) buffer[offset + index] = sample;
            SamplesRead += count;
            return count;
        }
    }

    /// <summary>A microphone that records nothing: each read hands over <see cref="PerRead"/> samples of a fixed level.</summary>
    internal sealed class ScriptedMicrophone : IVoiceMicrophone
    {
        private bool abandoned;

        internal List<string> Devices { get; } = new List<string> { "USB Microphone" };

        /// <summary>Gets or sets how an open answers while a device is there.</summary>
        internal VoiceMicrophoneOpen Answer { get; set; } = VoiceMicrophoneOpen.Opened;

        internal int Opens { get; private set; }

        internal int Closes { get; private set; }

        internal int Abandons { get; private set; }

        internal int EndedAbandoned { get; private set; }

        internal string? OpenedDevice { get; private set; }

        /// <summary>Gets whether a recording voice chat started still runs on the device: open, or abandoned and not ended.</summary>
        internal bool Recording { get; private set; }

        /// <summary>Gets or sets whether the next read finds the recording stopped, as an unplugged device leaves it.</summary>
        internal bool StopsUnderVoice { get; set; }

        internal int PerRead { get; set; } = 800;

        internal float Level { get; set; } = 0.5f;

        internal long SamplesRead { get; private set; }

        public bool IsOpen { get; private set; }

        public int SampleRate => 48_000;

        public IReadOnlyList<string> ListDevices() => Devices.ToArray();

        public VoiceMicrophoneOpen Open(string? device)
        {
            if (IsOpen) return VoiceMicrophoneOpen.Opened;
            if (Devices.Count == 0) return VoiceMicrophoneOpen.NoDevice;
            if (Answer != VoiceMicrophoneOpen.Opened) return Answer;
            Opens++;
            IsOpen = true;
            Recording = true;
            OpenedDevice = device;
            return VoiceMicrophoneOpen.Opened;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (!IsOpen) return 0;
            if (StopsUnderVoice)
            {
                StopsUnderVoice = false;
                IsOpen = false;
                Recording = false;
                return -1;
            }

            var read = Math.Min(count, PerRead);
            for (var index = 0; index < read; index++) buffer[offset + index] = Level;
            SamplesRead += read;
            return read;
        }

        public void Close()
        {
            if (!IsOpen) return;
            Closes++;
            IsOpen = false;
            Recording = false;
        }

        public void Abandon()
        {
            if (!IsOpen) return;
            Abandons++;
            IsOpen = false;
            abandoned = true;
        }

        public void EndAbandoned()
        {
            if (!abandoned) return;
            abandoned = false;
            EndedAbandoned++;
            Recording = false;
        }
    }

    /// <summary>The game's own robot recording, as the arbiter reads it: what the test sets, or a read that fails.</summary>
    internal sealed class FakeNativeRecording
    {
        internal bool Recording { get; set; }

        /// <summary>Gets or sets whether the game's state cannot be read, as after a game update moved it.</summary>
        internal bool Unreadable { get; set; }

        /// <summary>Reads the state, as the arbiter's source does; it throws while the state cannot be read.</summary>
        internal bool Read() => Unreadable ? throw new MissingFieldException("UIState", "Recording") : Recording;
    }

    /// <summary>
    /// The kit's Escape stack as the manager overlay has it: the overlay at the bottom, in the window band, and above it, in
    /// the modal band, whatever was pushed since, the latest on top: a binding capture while the presenter holds one, and a
    /// kit modal such as the consent. A press dismisses the top, as the kit's driver does in its own frame, before or after
    /// voice chat's; a capture leaves the stack before its cancel runs, as the Unity adapter does.
    /// </summary>
    internal sealed class FakeEscapeStack : IVoiceEscape
    {
        private readonly List<Action> modalBand = new List<Action>();
        private readonly Action dismissCapture;
        private Action? capture;

        internal FakeEscapeStack()
        {
            dismissCapture = () =>
            {
                var cancel = capture;
                Release();
                cancel?.Invoke();
            };
        }

        /// <summary>Gets whether a capture is on the stack.</summary>
        internal bool Held => capture != null;

        /// <summary>Gets how many times a capture took the top.</summary>
        internal int Holds { get; private set; }

        /// <summary>Gets how many presses closed the overlay.</summary>
        internal int OverlayCloses { get; private set; }

        public void Hold(Action cancel)
        {
            capture = cancel ?? throw new ArgumentNullException(nameof(cancel));
            Holds++;
            if (!modalBand.Contains(dismissCapture)) modalBand.Add(dismissCapture);
        }

        public void Release()
        {
            capture = null;
            modalBand.Remove(dismissCapture);
        }

        /// <summary>A kit modal shows, as the consent does: it joins the modal band on top, and the Esc that dismisses it runs <paramref name="dismiss"/>.</summary>
        internal void Show(Action dismiss) => modalBand.Add(dismiss ?? throw new ArgumentNullException(nameof(dismiss)));

        /// <summary>The kit's driver reads an Escape press and dismisses the top-most surface.</summary>
        internal void Press()
        {
            if (modalBand.Count == 0)
            {
                OverlayCloses++;
                return;
            }

            var top = modalBand[modalBand.Count - 1];
            modalBand.RemoveAt(modalBand.Count - 1);
            top();
        }
    }

    internal sealed class FakeVoiceInput : IVoiceInput
    {
        private readonly List<InputBinding> held = new List<InputBinding>();
        private readonly Queue<InputBinding> pressed = new Queue<InputBinding>();

        public bool IsTyping { get; set; }

        internal void Hold(InputBinding binding) => held.Add(binding);

        internal void Release(InputBinding binding) => held.RemoveAll(candidate => VoicePushToTalk.Same(candidate, binding));

        /// <summary>Presses <paramref name="binding"/> for the next frame that takes a press, and holds it.</summary>
        internal void Press(InputBinding binding)
        {
            pressed.Enqueue(binding);
            Hold(binding);
        }

        public bool IsHeld(InputBinding binding)
        {
            for (var index = 0; index < held.Count; index++)
            {
                if (VoicePushToTalk.Same(held[index], binding)) return true;
            }

            return false;
        }

        public bool TryTakePressed(out InputBinding binding)
        {
            if (pressed.Count == 0)
            {
                binding = default;
                return false;
            }

            binding = pressed.Dequeue();
            return true;
        }
    }

    internal sealed class FakeEmitter : IVoiceEmitter
    {
        internal IVoicePlaybackStream? Stream { get; private set; }

        internal float Volume { get; private set; }

        internal bool Playing { get; private set; }

        internal VoicePosition? Position { get; private set; }

        public void Play(IVoicePlaybackStream stream, float volume)
        {
            Stream = stream;
            Volume = volume;
            Playing = true;
        }

        public void SetVolume(float volume) => Volume = volume;

        public void Place(VoicePosition position) => Position = position;

        public void Unplace() => Position = null;

        public void Stop()
        {
            Playing = false;
            Stream = null;
            Position = null;
        }
    }

    internal sealed class FakeEmitters : IVoiceEmitters
    {
        private readonly Stack<FakeEmitter> free = new Stack<FakeEmitter>();

        internal List<FakeEmitter> Made { get; } = new List<FakeEmitter>();

        internal int Returned { get; private set; }

        internal int Playing
        {
            get
            {
                var playing = 0;
                foreach (var emitter in Made)
                {
                    if (emitter.Playing) playing++;
                }

                return playing;
            }
        }

        public IVoiceEmitter Rent()
        {
            if (free.Count > 0) return free.Pop();
            var emitter = new FakeEmitter();
            Made.Add(emitter);
            return emitter;
        }

        public void Return(IVoiceEmitter emitter)
        {
            Returned++;
            free.Push((FakeEmitter)emitter);
        }
    }

    internal sealed class FakeNativeBindings : IVoiceNativeBindings
    {
        /// <summary>Gets or sets the game's binding paths; null when they cannot be read, as before the game's player exists.</summary>
        internal List<string>? Paths { get; set; }

        internal int Reads { get; private set; }

        public bool TryRead(List<string> into)
        {
            Reads++;
            if (Paths == null) return false;
            into.AddRange(Paths);
            return true;
        }
    }

    internal sealed class MemoryVoiceSettingsStore : IVoiceSettingsStore
    {
        internal VoiceSettings? Stored { get; set; }

        internal bool FailLoad { get; set; }

        internal bool FailSave { get; set; }

        internal int Saves { get; private set; }

        public VoiceSettings Load() => FailLoad ? throw new IOException("unreadable") : Stored ?? VoiceSettings.Default;

        public void Save(VoiceSettings settings)
        {
            Saves++;
            if (FailSave) throw new IOException("unwritable");
            Stored = settings;
        }
    }

    internal sealed class FakeLauncherChannel : IVoiceLauncherChannel
    {
        internal VoiceSwitch Stored { get; set; } = VoiceSwitch.Off;

        internal bool FailRead { get; set; }

        internal bool FailRecord { get; set; }

        internal List<VoiceSwitch> Records { get; } = new List<VoiceSwitch>();

        public VoiceSwitch Read() => FailRead ? throw new IOException("unreadable") : Stored;

        public void Record(VoiceSwitch value)
        {
            if (FailRecord) throw new IOException("in use");
            Records.Add(value);
            Stored = value;
        }
    }

    /// <summary>
    /// Voice chat over fakes: the presenter and the runtime as the overlay ticks them, a control whose role and voice service
    /// the test sets, the runtime's real microphone arbiter reading the game's state the test sets, the kit's Escape stack,
    /// and the session the test lists. The background queue runs its work inline, so what it read or wrote arrives at the
    /// next frame.
    /// </summary>
    internal sealed class VoiceRig
    {
        internal const float FrameSeconds = 1f / 60f;

        internal VoiceRig(InternalSessionRole role = InternalSessionRole.Client, IVoiceInput? input = null, IVoiceEscape? escape = null)
        {
            Control = new RecordingSessionControl { Role = role, Voice = Voice };
            Arbiter.ObserveNativeRecording(Native.Read);
            Runtime = new VoiceChatRuntime(() => Control, () => Snapshot, Arbiter, Microphone, input ?? Input, Emitters);
            Queue = new MultiplayerBackgroundQueue(MultiplayerWorkSchedulers.Inline);
            Preferences = new VoicePreferences(Queue, Store, Channel);
            Presenter = new VoicePresenter(Runtime, Preferences, Queue, Microphone, input ?? Input, Bindings, escape ?? Escape, () => Control, () => Snapshot);
        }

        internal FakeVoiceService Voice { get; } = new FakeVoiceService();

        internal ScriptedMicrophone Microphone { get; } = new ScriptedMicrophone();

        internal FakeNativeRecording Native { get; } = new FakeNativeRecording();

        internal FakeVoiceInput Input { get; } = new FakeVoiceInput();

        internal FakeEmitters Emitters { get; } = new FakeEmitters();

        internal FakeNativeBindings Bindings { get; } = new FakeNativeBindings();

        internal FakeEscapeStack Escape { get; } = new FakeEscapeStack();

        internal MemoryVoiceSettingsStore Store { get; } = new MemoryVoiceSettingsStore();

        internal FakeLauncherChannel Channel { get; } = new FakeLauncherChannel();

        internal MultiplayerMicrophoneArbiter Arbiter { get; } = new MultiplayerMicrophoneArbiter();

        internal RecordingSessionControl Control { get; }

        internal VoiceChatRuntime Runtime { get; }

        internal MultiplayerBackgroundQueue Queue { get; }

        internal VoicePreferences Preferences { get; }

        internal VoicePresenter Presenter { get; }

        internal MultiplayerSessionSnapshot? Snapshot { get; set; }

        /// <summary>A rig whose player already agreed to the current wording and turned voice chat on, in a session with Sam.</summary>
        internal static VoiceRig On(InternalSessionRole role = InternalSessionRole.Client)
        {
            var rig = new VoiceRig(role);
            rig.Channel.Stored = VoiceSwitch.Agreed();
            rig.Snapshot = Session("s1", Player("p2", "Sam", "key-sam"));
            rig.Start();
            return rig;
        }

        /// <summary>Starts the presenter, as the overlay does, and runs the frame in which the choices arrive and the one after.</summary>
        internal void Start()
        {
            Presenter.Start();
            Frame(2);
        }

        /// <summary>Runs <paramref name="count"/> frames as the overlay does: the presenter, then the runtime.</summary>
        internal void Frame(int count = 1)
        {
            for (var index = 0; index < count; index++)
            {
                Presenter.Tick(FrameSeconds);
                Runtime.Tick(FrameSeconds);
            }
        }

        /// <summary>Gets a Ready session with this player, "me", and <paramref name="others"/>.</summary>
        internal static MultiplayerSessionSnapshot Session(string id, params MultiplayerParticipant[] others) =>
            Session(id, MultiplayerSessionState.Ready, others);

        internal static MultiplayerSessionSnapshot Session(string id, MultiplayerSessionState state, params MultiplayerParticipant[] others)
        {
            var me = new ParticipantId("me");
            var participants = new List<MultiplayerParticipant> { new MultiplayerParticipant(me, "Lee", true, true) };
            participants.AddRange(others);
            ParticipantId? host = null;
            foreach (var participant in others)
            {
                if (participant.IsHost) host = participant.Id;
            }

            return new MultiplayerSessionSnapshot(new MultiplayerSessionId(id), state, MultiplayerProcessKind.Interactive, MultiplayerExecutionSide.Client,
                me, participants, new NetworkTick(1), new SessionSeed(1), host, MultiplayerSessionEndReason.None);
        }

        /// <summary>Gets another player: a key for one the host gave a key, none for one without, as the host itself.</summary>
        internal static MultiplayerParticipant Player(string id, string name, string? key, bool isHost = false, bool connected = true) =>
            new MultiplayerParticipant(new ParticipantId(id), name, false, connected, isHost, MultiplayerConnectionQuality.Good, null,
                key == null ? default : new MultiplayerPlayerKey(key));
    }
}
