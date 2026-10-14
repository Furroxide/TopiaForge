using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Voice-enabled engines over the rig's virtual transport: an interactive host ("host") and clients that join it, on the
/// rig's 60 Hz clock. Talkers submit their 20 ms frames as they fall due; listeners drain what their engine receives.
/// </summary>
internal sealed class VoiceSessionHarness : IDisposable
{
    internal const long FrameMicros = 20_000;
    private readonly FixedStepClock clock = new(60);
    private readonly List<SessionEngine> engines = new();
    private readonly List<Talker> talkers = new();
    private readonly List<Listener> listeners = new();

    internal VoiceSessionHarness(VirtualLinkConditions? conditions = null, Action<VoiceOptions>? tune = null, int packetBytes = 50)
    {
        Codec = new FakeVoiceCodec(packetBytes);
        Voice = new VoiceOptions(Codec);
        tune?.Invoke(Voice);
        Options = new EngineOptions
        {
            StepRateHz = 60,
            DownstreamRateHz = 0,
            ClockSyncIntervalMicros = 0,
            ObjectIdPrefix = "voice-",
            CallbackFailures = EngineCallbackFailureMode.Propagate,
            Diagnostics = Diagnostics,
            ParticipantIdForIdentity = identity => new ParticipantId(identity),
            Voice = Voice
        };
        Network = new VirtualEngineTransport(conditions ?? new VirtualLinkConditions { LatencyTicks = 1 });
        Host = SessionEngine.CreateHost(
            Options,
            MultiplayerProcessKind.Interactive,
            new MultiplayerSessionId("voice-session"),
            new SessionSeed(7),
            new ParticipantId("host"),
            "Host",
            0);
        engines.Add(Host);
    }

    internal FakeVoiceCodec Codec { get; }
    internal VoiceOptions Voice { get; }
    internal EngineOptions Options { get; }
    internal RecordingDiagnostics Diagnostics { get; } = new();
    internal VirtualEngineTransport Network { get; }
    internal SessionEngine Host { get; }
    internal ulong Tick { get; private set; }
    internal long NowMicros => clock.StepStartMicros(Tick);

    /// <summary>Gets the engines whose main thread is stalled: the network still delivers to them, but they never pump.</summary>
    internal HashSet<SessionEngine> Stalled { get; } = new();

    /// <summary>Adds a client that joins the host, and waits until it is ready.</summary>
    internal (SessionEngine Engine, VirtualLinkEnd Link) AddClient(string identity)
    {
        var engine = SessionEngine.CreateClient(Options, new MultiplayerSessionId("voice-session"), NowMicros);
        engines.Add(engine);
        var link = Connect(engine, identity);
        Flush();
        return (engine, link);
    }

    /// <summary>Pumps an engine the test created itself, as it pumps the host and the clients.</summary>
    internal SessionEngine Track(SessionEngine engine)
    {
        engines.Add(engine);
        return engine;
    }

    /// <summary>Connects <paramref name="engine"/> to the host over a new link, as a join or a reconnect does.</summary>
    internal VirtualLinkEnd Connect(SessionEngine engine, string identity)
    {
        var pair = Network.Connect(identity);
        Host.Accept(pair.HostEnd);
        engine.Connect(pair.ClientEnd, identity);
        return pair.ClientEnd;
    }

    internal Talker Talk(SessionEngine engine, short idBase, int frames = int.MaxValue)
    {
        var talker = new Talker(engine, Codec, idBase, NowMicros, frames);
        talkers.Add(talker);
        return talker;
    }

    internal Listener Listen(SessionEngine engine)
    {
        var listener = new Listener(engine, this);
        listeners.Add(listener);
        return listener;
    }

    /// <summary>Moves one tick at a time: talkers submit what is due, every engine pumps, frames move, engines pump again.</summary>
    internal void Advance(int ticks = 1)
    {
        for (var index = 0; index < ticks; index++)
        {
            foreach (var talker in talkers) talker.SubmitDue(NowMicros);
            PumpAll();
            Tick++;
            Network.Now = Tick;
            Network.DeliverDue(Tick);
            PumpAll();
            foreach (var listener in listeners) listener.Drain();
        }
    }

    internal void Flush(int maximumTicks = 600)
    {
        var count = 0;
        while ((Network.HasInFlight || engines.Any(engine => !engine.IsEnded && engine.HasPendingWork)) && count++ < maximumTicks) Advance();
    }

    internal void PumpAll()
    {
        Network.Now = Tick;
        foreach (var engine in engines.ToArray())
        {
            if (!engine.IsEnded && !Stalled.Contains(engine)) engine.Pump(NowMicros);
        }
    }

    /// <summary>Connects <paramref name="engine"/> to <paramref name="host"/>, a host other than the rig's own.</summary>
    internal VirtualLinkEnd ConnectTo(SessionEngine host, SessionEngine engine, string identity)
    {
        var pair = Network.Connect(identity);
        host.Accept(pair.HostEnd);
        engine.Connect(pair.ClientEnd, identity);
        return pair.ClientEnd;
    }

    /// <summary>
    /// Floods the host from a modified client's <paramref name="link"/>, <paramref name="perTick"/> uplink datagrams a
    /// tick, until the host mutes it automatically. Returns the ticks that took, or -1 if it never did.
    /// </summary>
    internal int FloodUntilMuted(VirtualLinkEnd link, ref ulong next, int perTick = 4, int maximumTicks = 600)
    {
        var mutes = Host.GetVoiceStatistics().AutoMutes;
        for (var tick = 1; tick <= maximumTicks; tick++)
        {
            for (var index = 0; index < perTick; index++, next++)
            {
                link.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(Codec, 0, next + 1, next, 1)));
            }

            Advance();
            if (Host.GetVoiceStatistics().AutoMutes > mutes) return tick;
        }

        return -1;
    }

    public void Dispose()
    {
        foreach (var engine in engines)
        {
            if (!engine.IsEnded) engine.Shutdown(MultiplayerSessionEndReason.HostEnded);
        }
    }

    /// <summary>One talker: frame <c>k</c>, carrying id <c>idBase + k</c>, falls due 20 ms after frame <c>k - 1</c>.</summary>
    internal sealed class Talker
    {
        private readonly SessionEngine engine;
        private readonly FakeVoiceCodec codec;
        private readonly short idBase;
        private readonly long startMicros;
        private readonly int frames;

        internal Talker(SessionEngine engine, FakeVoiceCodec codec, short idBase, long startMicros, int frames)
        {
            this.engine = engine;
            this.codec = codec;
            this.idBase = idBase;
            this.startMicros = startMicros;
            this.frames = frames;
        }

        internal ulong Next { get; private set; }
        internal List<VoiceSubmitResult> Results { get; } = new();

        internal void SubmitDue(long nowMicros)
        {
            while ((long)Next < frames && startMicros + ((long)Next * FrameMicros) <= nowMicros)
            {
                var id = (short)(idBase + (short)Next);
                var flags = (long)Next == frames - 1 ? VoiceFrameFlags.EndOfTalk : VoiceFrameFlags.None;
                Results.Add(engine.SubmitVoiceFrame(Next, flags, codec.Packet(id, Next == 0 ? (short)0 : (short)(id - 1))));
                Next++;
            }
        }
    }

    internal readonly record struct Heard(string Speaker, ulong Sequence, ulong Timestamp, VoiceFrameFlags Flags, short Id, long ArrivalMicros, byte[] Payload);

    /// <summary>Drains one engine's received frames after every tick.</summary>
    internal sealed class Listener
    {
        private readonly SessionEngine engine;
        private readonly VoiceSessionHarness harness;

        internal Listener(SessionEngine engine, VoiceSessionHarness harness)
        {
            this.engine = engine;
            this.harness = harness;
        }

        internal List<Heard> Frames { get; } = new();

        internal void Drain()
        {
            if (engine.IsEnded) return;
            while (engine.TryReceiveVoiceFrame(out var frame))
            {
                var payload = frame.Payload.ToArray();
                var id = payload.Length > 2 ? BitConverter.ToInt16(payload, 1) : (short)0;
                Frames.Add(new Heard(frame.Speaker.Value, frame.Sequence, frame.Timestamp, frame.Flags, id, frame.ArrivalMicros, payload));
            }
        }

        internal List<Heard> From(string speaker) => Frames.Where(frame => frame.Speaker == speaker).ToList();

        /// <summary>Plays one speaker's frames through a jitter buffer with a pull every 20 ms from their first arrival.</summary>
        internal (List<VoicePlayout> Kinds, List<short> Decoded, VoiceJitterStatistics Statistics) Play(string speaker, int extraPulls = 20)
        {
            var frames = From(speaker);
            var buffer = new VoiceJitterBuffer(harness.Codec);
            var kinds = new List<VoicePlayout>();
            var decoded = new List<short>();
            if (frames.Count == 0) return (kinds, decoded, buffer.Statistics);
            var start = frames[0].ArrivalMicros;
            var end = frames[^1].ArrivalMicros + (extraPulls * FrameMicros);
            var next = 0;
            var output = new short[harness.Codec.FrameSamples];
            for (var now = start; now <= end; now += FrameMicros)
            {
                while (next < frames.Count && frames[next].ArrivalMicros <= now)
                {
                    var frame = frames[next++];
                    buffer.Push(frame.Sequence, frame.Timestamp, frame.Flags, frame.Payload, frame.ArrivalMicros);
                }

                var kind = buffer.Pull(output);
                kinds.Add(kind);
                if (kind == VoicePlayout.Decoded) decoded.Add(output[0]);
            }

            return (kinds, decoded, buffer.Statistics);
        }
    }
}
