using TopiaForge.ModManager;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.ControlVoiceSteps;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The game's voice chat, its runtime as ModManager runs it, over real providers on the deterministic network, with real
/// cryptography, transport, engine and Opus codec: capture through push-to-talk reaching another player's stream and a
/// voice played back through its emitters, and mutes kept by player key across a move to another host and back.
/// </summary>
internal static class VoiceChatProviderTests
{
    private const double Heard = -35;
    private const double Silent = -80;
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void PushToTalkReachesAnotherPlayersStream()
    {
        using var rig = ControlRig.WithVoice(341, Sandbox);
        var hostEars = Host(rig);
        var sam = rig.AddPeer("sam", Sandbox);
        sam.Runtime.PreferredDisplayName = "Sam";
        rig.JoinAndWait(sam);
        var chat = new PeerVoiceChat(sam, rig.Network);
        chat.TurnOn();
        RunUntilListed(rig, rig.Host, "sam");
        hostEars.Listen("sam", IdOf(rig.Host, "sam"));

        // Push-to-talk up: the microphone never opens, though it would hear Sam.
        chat.Microphone.Play(Speech(1.2, 61));
        RunFor(rig, 1_000_000);
        That(chat.Microphone.Opens == 0 && hostEars.LevelSince("sam", 0) < Silent, "push-to-talk up, the microphone stays closed and the host hears nothing");

        // Held: the host hears Sam through the SPI.
        chat.Input.Hold(VoicePushToTalk.DefaultKey);
        var mark = hostEars.Heard("sam").Count;
        chat.Microphone.Play(Speech(1.5, 62));
        RunFor(rig, 1_700_000);
        That(chat.Microphone.Opens == 1 && hostEars.LevelSince("sam", mark) > Heard && chat.Service.GetStatistics().FramesSent > 40,
            "holding push-to-talk, the host hears Sam (" + hostEars.LevelSince("sam", mark) + " dBFS)");

        // Released: closed, and nothing more is heard.
        chat.Input.Release(VoicePushToTalk.DefaultKey);
        RunFor(rig, 300_000);
        mark = hostEars.Heard("sam").Count;
        chat.Microphone.Play(Speech(1, 63));
        RunFor(rig, 1_000_000);
        That(!chat.Microphone.IsOpen && hostEars.LevelSince("sam", mark) < Silent, "released, the microphone closes and the host hears nothing more");

        // The host talks, and Sam hears it through voice chat's own playback.
        var hoster = rig.Host.IdOf("Hoster");
        hostEars.Talk(Speech(1.5, 64));
        RunFor(rig, 1_700_000);
        That(chat.LevelFrom(hoster) > Heard && chat.Emitters.Rented == 1, "Sam hears the host through one emitter (" + chat.LevelFrom(hoster) + " dBFS)");
        RunFor(rig, 1_000_000);
        That(chat.Emitters.Playing == 1, "and keeps it while the host's stream lasts");
    }

    internal static void MutesByPlayerKeyHoldAcrossAMoveAndBack()
    {
        using var rig = ControlRig.WithVoice(343, Sandbox);
        Host(rig);
        var (_, samMic) = Join(rig, "sam", Sandbox);
        var (_, kaiMic) = Join(rig, "kai", Sandbox);
        var lee = rig.AddPeer("lee", Sandbox);
        lee.Runtime.PreferredDisplayName = "Lee";
        rig.JoinAndWait(lee);
        var chat = new PeerVoiceChat(lee, rig.Network);
        chat.TurnOn();
        RunUntilListed(rig, lee, "sam", "kai");
        var sam = lee.Session.Snapshot.Participants.Single(participant => participant.DisplayName == "Sam");
        That(sam.PlayerKey.IsValid, "the host gives Sam a key every player sees");
        That(chat.Runtime.Mutes.SetMuted(sam.PlayerKey, true), "Lee mutes Sam by his key");

        void HearsKaiNotSam(string when, uint seed)
        {
            var samId = IdOf(lee, "sam");
            var kaiId = IdOf(lee, "kai");
            var samMark = chat.HeardCount(samId);
            var kaiMark = chat.HeardCount(kaiId);
            samMic.Talk(Speech(1, seed));
            kaiMic.Talk(Speech(1, seed + 1));
            RunFor(rig, 1_500_000);
            That(chat.LevelFrom(samId, samMark) < Silent && chat.LevelFrom(kaiId, kaiMark) > Heard,
                when + ", Lee hears Kai and not Sam (" + chat.LevelFrom(samId, samMark) + ", " + chat.LevelFrom(kaiId, kaiMark) + " dBFS)");
        }

        HearsKaiNotSam("at first", 71);

        // Another host: Ana has Sam's old id there, and another key, so Lee hears her.
        var samIdAtFirst = IdOf(lee, "sam");
        var second = SecondHost(rig, "second", Sandbox);
        var (_, anaMic) = Join(rig, "ana", Sandbox);
        RunUntilListed(rig, second, "ana");
        lee.Control.Leave();
        That(rig.Run(() => lee.Control.Role == InternalSessionRole.Solo), "Lee leaves the first host");
        rig.JoinAndWait(lee);
        RunUntilListed(rig, lee, "ana");
        var anaId = IdOf(lee, "ana");
        That(anaId.Equals(samIdAtFirst), "at the second host Ana has the id Sam had at the first (" + anaId + ")");
        var anaMark = chat.HeardCount(anaId);
        anaMic.Talk(Speech(1, 73));
        RunFor(rig, 1_500_000);
        That(chat.LevelFrom(anaId, anaMark) > Heard, "Lee hears Ana: Sam's key names nobody at another host (" + chat.LevelFrom(anaId, anaMark) + " dBFS)");

        // Back at the first host the engine forgot the names; the key names Sam again once the host lists its players.
        lee.Control.Leave();
        That(rig.Run(() => lee.Control.Role == InternalSessionRole.Solo), "Lee leaves the second host");
        rig.Resolver.Host = () => rig.Host.Provider.Live!.Host?.Descriptor is { } descriptor ? (rig.Host.Factory.Networks[^1].Node, descriptor) : null;
        rig.JoinAndWait(lee);
        RunUntilListed(rig, lee, "sam", "kai");
        RunFor(rig, 100_000);
        HearsKaiNotSam("back at the first host", 74);
    }
}

/// <summary>
/// One peer's voice chat as the game runs it: the runtime over the peer's control and session, ticked where the mods'
/// Update would run, with a microphone that plays a signal at the network's clock, push-to-talk the test holds, and
/// emitters whose audio the peer's audio thread reads once each frame is done.
/// </summary>
internal sealed class PeerVoiceChat
{
    private readonly ControlPeer peer;
    private readonly VirtualDatagramNetwork network;
    private long lastMicros;

    internal PeerVoiceChat(ControlPeer peer, VirtualDatagramNetwork network)
    {
        this.peer = peer;
        this.network = network;
        Microphone = new SignalMicrophone(network);
        Emitters = new ListeningEmitters(network);
        // An arbiter pointed at no game state: the game never records on its own here.
        Runtime = new VoiceChatRuntime(() => peer.Control, () => peer.Session.Snapshot, new MultiplayerMicrophoneArbiter(), Microphone,
            Input, Emitters);
        lastMicros = network.Now;
        peer.ModsUpdate = Frame;
        peer.AfterFrame = Emitters.ReadAudio;
    }

    internal VoiceChatRuntime Runtime { get; }

    internal SignalMicrophone Microphone { get; }

    internal HeldKeys Input { get; } = new();

    internal ListeningEmitters Emitters { get; }

    internal IInternalVoiceService Service => peer.Control.Voice ?? throw new InvalidOperationException("Assertion failed: " + peer.Name + " has voice");

    /// <summary>Turns voice chat on with consent to the current wording, as the player who agreed does.</summary>
    internal void TurnOn()
    {
        Runtime.ConsentCurrent = true;
        Runtime.VoiceEnabled = true;
    }

    internal int HeardCount(ParticipantId speaker) => Emitters.Heard(speaker).Count;

    internal double LevelFrom(ParticipantId speaker, int from = 0)
    {
        var heard = Emitters.Heard(speaker);
        return from >= heard.Count ? -120 : VoiceAudio.LevelDecibels(heard.ToArray(), from, heard.Count - from);
    }

    private void Frame()
    {
        var now = network.Now;
        var seconds = (now - lastMicros) / 1_000_000f;
        lastMicros = now;
        Runtime.Tick(seconds);
    }
}

/// <summary>A microphone that plays a 48 kHz signal from the moment it is given, at the network's clock; silence after it ends.</summary>
internal sealed class SignalMicrophone : IVoiceMicrophone
{
    private const int Rate = 48_000;
    private readonly VirtualDatagramNetwork network;
    private float[] signal = Array.Empty<float>();
    private long startMicros;
    private long read;

    internal SignalMicrophone(VirtualDatagramNetwork network)
    {
        this.network = network;
    }

    internal int Opens { get; private set; }

    public bool IsOpen { get; private set; }

    public int SampleRate => Rate;

    internal void Play(float[] samples)
    {
        signal = samples;
        startMicros = network.Now;
        read = 0;
    }

    public IReadOnlyList<string> ListDevices() => new[] { "Test microphone" };

    public VoiceMicrophoneOpen Open(string? device)
    {
        if (IsOpen) return VoiceMicrophoneOpen.Opened;
        IsOpen = true;
        Opens++;

        // A recording starts now: what was played before it opened was never heard.
        read = Due();
        return VoiceMicrophoneOpen.Opened;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (!IsOpen) return 0;
        var due = Due();
        var available = (int)Math.Min(count, due - read);
        for (var index = 0; index < available; index++)
        {
            var position = read + index;
            buffer[offset + index] = position < signal.Length ? signal[position] : 0f;
        }

        read += available;
        return available;
    }

    public void Close() => IsOpen = false;

    public void Abandon() => IsOpen = false;

    public void EndAbandoned()
    {
    }

    private long Due() => (network.Now - startMicros) * Rate / 1_000_000;
}

/// <summary>Emitters whose streams the audio thread reads once each frame is done, 48 kHz for as long as the clock moved.</summary>
internal sealed class ListeningEmitters : IVoiceEmitters
{
    private const int Rate = 48_000;
    private readonly VirtualDatagramNetwork network;
    private readonly List<Emitter> made = new();
    private readonly Dictionary<ParticipantId, List<float>> heard = new();
    private readonly long startMicros;
    private float[] buffer = new float[4_096];
    private long read;

    internal ListeningEmitters(VirtualDatagramNetwork network)
    {
        this.network = network;
        startMicros = network.Now;
    }

    internal int Rented { get; private set; }

    internal int Playing => made.Count(emitter => emitter.Reader.Stream != null);

    internal List<float> Heard(ParticipantId speaker) => heard.TryGetValue(speaker, out var samples) ? samples : new List<float>();

    public IVoiceEmitter Rent()
    {
        Rented++;
        var free = made.FirstOrDefault(emitter => !emitter.Rented);
        if (free == null)
        {
            free = new Emitter();
            made.Add(free);
        }

        free.Rented = true;
        return free;
    }

    public void Return(IVoiceEmitter emitter) => ((Emitter)emitter).Rented = false;

    /// <summary>What the audio thread does between frames: every emitter fills its buffer through its reader.</summary>
    internal void ReadAudio()
    {
        var due = (network.Now - startMicros) * Rate / 1_000_000;
        var count = (int)(due - read);
        if (count <= 0) return;
        read = due;
        if (buffer.Length < count) buffer = new float[count];
        foreach (var emitter in made)
        {
            var stream = emitter.Reader.Stream;
            if (stream == null) continue;
            var samples = buffer.Length == count ? buffer : new float[count];
            emitter.Reader.Read(samples);
            if (!heard.TryGetValue(stream.Speaker, out var list)) heard.Add(stream.Speaker, list = new List<float>());
            for (var index = 0; index < count; index++) list.Add(samples[index] * emitter.Volume);
        }
    }

    private sealed class Emitter : IVoiceEmitter
    {
        internal VoiceEmitterReader Reader { get; } = new();

        internal bool Rented { get; set; }

        internal float Volume { get; private set; }

        public void Play(IVoicePlaybackStream stream, float volume)
        {
            Reader.Attach(stream);
            Volume = volume;
        }

        public void SetVolume(float volume) => Volume = volume;

        public void Place(VoicePosition position)
        {
        }

        public void Unplace()
        {
        }

        public void Stop() => Reader.Attach(null);
    }
}

/// <summary>Push-to-talk's keys as the test holds them.</summary>
internal sealed class HeldKeys : IVoiceInput
{
    private readonly List<InputBinding> held = new();

    public bool IsTyping => false;

    internal void Hold(InputBinding binding) => held.Add(binding);

    internal void Release(InputBinding binding) => held.RemoveAll(candidate => VoicePushToTalk.Same(candidate, binding));

    public bool IsHeld(InputBinding binding) => held.Any(candidate => VoicePushToTalk.Same(candidate, binding));

    public bool TryTakePressed(out InputBinding pressed)
    {
        pressed = default;
        return false;
    }
}
