extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's voice service end to end, on the provider's own copy of the engine, over the rig's virtual transport,
/// with the real Opus codec. Time is virtual and moves in 10 ms steps. Each step every talker submits the capture that
/// fell due, every engine and service pumps, every voice worker steps, and every listener's audio thread reads 10 ms
/// from each stream it plays. Workers never run a thread here, so every run is the same.
/// </summary>
internal sealed class VoiceServiceRig : IDisposable
{
    internal const long StepMicros = 10_000;
    internal const int ReadSamples = 480;

    // What the secure transport adds to every datagram: the protected packet's 17-byte header and 16-byte tag, and the
    // DATAGRAM frame's type, channel, sequence and length (at most 7 bytes).
    internal const int TransportOverheadBytes = 40;

    private readonly List<Member> members = new();
    private readonly float[] readBuffer = new float[ReadSamples];

    internal VoiceServiceRig(VirtualLinkConditions? conditions = null)
    {
        Codec = new OpusVoiceCodec();
        Options = new EngineOptions
        {
            StepRateHz = 60,
            DownstreamRateHz = 0,
            ClockSyncIntervalMicros = 0,
            ObjectIdPrefix = "voice-",
            CallbackFailures = EngineCallbackFailureMode.Propagate,
            ParticipantIdForIdentity = identity => new ParticipantId(identity),
            Voice = new VoiceOptions(Codec)
        };
        Network = new VirtualEngineTransport(conditions ?? new VirtualLinkConditions { LatencyTicks = 1, TicksPerSecond = 100 });
    }

    internal OpusVoiceCodec Codec { get; }
    internal EngineOptions Options { get; }
    internal VirtualEngineTransport Network { get; }
    internal long NowMicros { get; private set; } = 1_000_000;
    internal ulong Tick { get; private set; }
    internal IReadOnlyList<Member> Members => members;

    /// <summary>
    /// Starts a host. With <paramref name="withVoice"/> false the test drains its frames itself. The first host's links
    /// know it as "host"; a later one is known by its <paramref name="name"/> and runs its own <paramref name="session"/>.
    /// </summary>
    internal Member AddHost(string name = "host", bool withVoice = true, string session = "voice-service")
    {
        var engine = SessionEngine.CreateHost(
            Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId(session), new SessionSeed(11), new ParticipantId(name), "Host", NowMicros);
        var identity = members.Any(other => other.HostIdentity != null) ? name : "host";
        var member = new Member(this, name, engine, withVoice) { HostIdentity = identity };
        members.Add(member);
        return member;
    }

    /// <summary>
    /// Adds a client that joins <paramref name="host"/>, the first host unless named, and steps until it is ready; with
    /// <paramref name="untilReady"/> false it only connects, and the test steps it in.
    /// </summary>
    internal Member AddClient(string name, bool withVoice = true, Member? host = null, bool untilReady = true)
    {
        var engine = SessionEngine.CreateClient(Options, new MultiplayerSessionId("voice-service"), NowMicros);
        var member = new Member(this, name, engine, withVoice);
        members.Add(member);
        Connect(member, host);
        if (!untilReady) return member;
        Step(5);
        if (engine.Snapshot.State != MultiplayerSessionState.Ready) throw new InvalidOperationException(name + " did not join.");
        return member;
    }

    /// <summary>
    /// Connects <paramref name="member"/> to <paramref name="host"/>, the first host unless named, over a new link, as a
    /// join, a reconnect or a move to another host does.
    /// </summary>
    internal void Connect(Member member, Member? host = null)
    {
        host ??= members.First(candidate => candidate.HostIdentity != null);
        var pair = Network.Connect(member.Name, host.HostIdentity!);
        host.Engine.Accept(pair.HostEnd);
        member.Engine.Connect(pair.ClientEnd, member.Name);
        member.Link = pair.ClientEnd;
    }

    /// <summary>Replaces <paramref name="member"/>'s process with a new one under the same identity, as a game restart does.</summary>
    internal Member Restart(Member member)
    {
        // The old process dies without saying goodbye: its link is cut and its engine is never pumped again.
        member.Abandon();
        Network.Sever(member.Link!);
        var index = members.IndexOf(member);
        var engine = SessionEngine.CreateClient(Options, new MultiplayerSessionId("voice-service"), NowMicros);
        var restarted = new Member(this, member.Name, engine, withVoice: true) { Source = member.Source };
        members[index] = restarted;
        Connect(restarted);
        return restarted;
    }

    internal void Step(int steps = 1)
    {
        for (var index = 0; index < steps; index++)
        {
            foreach (var member in members) member.Capture();
            foreach (var member in members) member.PumpMain();
            foreach (var member in members)
            {
                if (!member.WorkerStalled) member.Voice?.Worker.Step(NowMicros);
            }

            foreach (var member in members) member.ReadAudio(readBuffer);
            NowMicros += StepMicros;
            Tick++;
            Network.Now = Tick;
            Network.DeliverDue(Tick);
        }
    }

    internal void StepFor(long micros) => Step((int)(micros / StepMicros));

    public void Dispose()
    {
        foreach (var member in members) member.Dispose();
    }

    /// <summary>One process in the session: its engine, its voice service, its microphone and its ears.</summary>
    internal sealed class Member : IDisposable
    {
        private readonly VoiceServiceRig rig;
        private readonly Dictionary<string, List<float>> heard = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VoicePlaybackStream> listening = new(StringComparer.Ordinal);
        private bool disposed;

        internal Member(VoiceServiceRig rig, string name, SessionEngine engine, bool withVoice)
        {
            this.rig = rig;
            Name = name;
            Engine = engine;
            if (!withVoice) return;

            // Every player in the rig has turned voice on; a test that needs it off turns it off.
            Voice = new VoiceService(engine, rig.Codec, () => rig.NowMicros, startWorker: false) { Enabled = true };
        }

        internal string Name { get; }
        internal SessionEngine Engine { get; }
        internal VoiceService? Voice { get; }
        internal VirtualLinkEnd? Link { get; set; }

        /// <summary>Gets the identity a host's links know it by, or null for a client.</summary>
        internal string? HostIdentity { get; init; }

        /// <summary>
        /// Gets or sets what the microphone hears. The player transmits while it plays, as a held push-to-talk key or an
        /// open voice-activation microphone, and stops when it ends. Without one, a test drives capture itself.
        /// </summary>
        internal VoiceSource? Source { get; set; }

        /// <summary>Gets or sets whether the main thread is stalled: no capture, no pumps. The worker keeps going.</summary>
        internal bool Stalled { get; set; }

        /// <summary>
        /// Gets or sets whether the voice worker is stalled, as when its thread is not scheduled: it takes nothing and plays
        /// nothing, while the main thread keeps queuing for it.
        /// </summary>
        internal bool WorkerStalled { get; set; }

        /// <summary>Gets the frames a member without a voice service received, for tests that inspect the wire.</summary>
        internal List<(string Speaker, ulong Sequence, ulong Timestamp, provider::TopiaForge.Multiplayer.Engine.VoiceFrameFlags Flags, int Length, long Arrival)> Frames { get; } = new();

        internal ParticipantId Id => new(Name);

        /// <summary>Starts playing <paramref name="speaker"/>: from now on this member's audio thread reads their stream.</summary>
        internal VoicePlaybackStream Listen(string speaker)
        {
            var stream = Voice!.GetPlaybackStream(new ParticipantId(speaker));
            listening[speaker] = stream;
            heard[speaker] = new List<float>();
            return stream;
        }

        /// <summary>Stops reading <paramref name="speaker"/>'s stream, as when their emitter goes away.</summary>
        internal void StopListening(string speaker) => listening.Remove(speaker);

        internal List<float> Heard(string speaker) => heard[speaker];

        internal void Capture()
        {
            if (disposed || Stalled || Source == null || Voice == null) return;
            Source.Submit(Voice, rig.NowMicros, StepMicros);
        }

        internal void PumpMain()
        {
            if (disposed || Stalled || Engine.IsEnded) return;
            Engine.Pump(rig.NowMicros);
            if (Voice != null)
            {
                Voice.Pump(rig.NowMicros);
                return;
            }

            while (Engine.TryReceiveVoiceFrame(out var frame))
            {
                Frames.Add((frame.Speaker.Value, frame.Sequence, frame.Timestamp, frame.Flags, frame.Payload.Count, frame.ArrivalMicros));
            }
        }

        internal void ReadAudio(float[] buffer)
        {
            if (disposed) return;
            foreach (var (speaker, stream) in listening)
            {
                stream.Read(buffer, 0, buffer.Length);
                heard[speaker].AddRange(buffer);
            }
        }

        internal void Abandon()
        {
            disposed = true;
            Voice?.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Voice?.Dispose();
            if (!Engine.IsEnded) Engine.Shutdown(MultiplayerSessionEndReason.Left);
        }
    }
}

/// <summary>
/// A microphone for the rig: it plays a 48 kHz signal, resampled on the fly to <see cref="SampleRate"/>, and submits
/// whatever falls due in each step. It goes quiet when the signal ends, or between <see cref="Gaps"/>.
/// </summary>
internal sealed class VoiceSource
{
    private readonly float[] signal;
    private float[] chunk = new float[8_192];
    private long startMicros = long.MinValue;
    private long submitted;

    internal VoiceSource(float[] signal, int sampleRate = 48_000)
    {
        this.signal = signal;
        SampleRate = sampleRate;
    }

    internal int SampleRate { get; }

    /// <summary>Gets the whole signal, at <see cref="SampleRate"/>.</summary>
    internal float[] Signal => signal;

    /// <summary>Gets the stretches, in signal samples at <see cref="SampleRate"/>, during which nothing is submitted.</summary>
    internal List<(long From, long To)> Gaps { get; } = new();

    /// <summary>
    /// Gets or sets whether the player keeps transmitting through gaps, as a held push-to-talk key whose microphone
    /// briefly yields nothing; otherwise a gap is a released key.
    /// </summary>
    internal bool HoldThroughGaps { get; set; }

    internal long SamplesSubmitted => submitted;

    internal bool Finished => submitted >= signal.Length;

    internal static VoiceSource Speech(double seconds, uint seed = 1, double gain = 1.0)
    {
        var shorts = VoiceTestSignal.Voiced((int)(48_000 * seconds), seed);
        return new VoiceSource(shorts.Select(sample => (float)(sample * gain / 32_768.0)).ToArray());
    }

    internal void Submit(VoiceService voice, long nowMicros, long stepMicros)
    {
        if (startMicros == long.MinValue) startMicros = nowMicros;
        var due = Math.Min(signal.Length, (nowMicros + stepMicros - startMicros) * SampleRate / 1_000_000);
        var count = (int)Math.Max(0, due - submitted);
        if (Finished)
        {
            // The signal is over: the key is released.
            voice.SetTransmitting(false);
            return;
        }

        if (count == 0) return;
        var from = submitted;
        submitted = due;
        if (Gaps.Any(gap => from >= gap.From && from < gap.To))
        {
            voice.SetTransmitting(HoldThroughGaps);
            return;
        }

        if (chunk.Length < count) chunk = new float[count];
        Array.Copy(signal, from, chunk, 0, count);
        voice.SetTransmitting(true);
        voice.SubmitCapture(chunk, count, SampleRate);
    }

    /// <summary>Resamples a 48 kHz signal to <paramref name="rate"/> by linear interpolation, good enough for tones.</summary>
    internal static float[] AtRate(float[] signal48, int rate)
    {
        var length = (int)((long)signal48.Length * rate / 48_000);
        var output = new float[length];
        for (var index = 0; index < length; index++)
        {
            var position = index * 48_000.0 / rate;
            var left = (int)position;
            var right = Math.Min(signal48.Length - 1, left + 1);
            var fraction = position - left;
            output[index] = (float)((signal48[left] * (1 - fraction)) + (signal48[right] * fraction));
        }

        return output;
    }
}
