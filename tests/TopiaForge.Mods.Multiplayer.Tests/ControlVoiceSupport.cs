extern alias provider;

using TopiaForge.Mods.Internal;
using EngineChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using EngineLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;
using IEngineLink = provider::TopiaForge.Multiplayer.Engine.IEngineLink;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One peer's microphone and ears for voice through the SPI, over a voice control rig. The microphone hands capture to
/// <see cref="IInternalVoiceService.SubmitCapture"/> each frame where the mods' Update would run, holding the push-to-talk
/// binding while it plays; the ears read every stream they play once the frame and the voice worker's step are done, as
/// the audio thread would, 48 kHz for as long as the network's clock moved.
/// </summary>
internal sealed class ControlVoice
{
    private const int Rate = 48_000;

    private readonly ControlPeer peer;
    private readonly VirtualDatagramNetwork network;
    private readonly Dictionary<string, Listening> listening = new(StringComparer.Ordinal);
    private readonly long readStartMicros;
    private float[] chunk = new float[4_096];
    private float[] buffer = new float[4_096];
    private float[]? signal;
    private long talkStartMicros;
    private long submitted;
    private long read;

    internal ControlVoice(ControlPeer peer, VirtualDatagramNetwork network)
    {
        this.peer = peer;
        this.network = network;
        readStartMicros = network.Now;
        peer.ModsUpdate = Capture;
        peer.AfterFrame = ReadAudio;
    }

    internal IInternalVoiceService Service => peer.Control.Voice ?? throw new InvalidOperationException("Assertion failed: " + peer.Name + " has voice");

    /// <summary>Gets or sets whether the microphone holds the binding while it plays; without it, capture is only metered.</summary>
    internal bool HoldsBinding { get; set; } = true;

    /// <summary>Gets whether the microphone is still playing.</summary>
    internal bool Talking => signal != null;

    /// <summary>Starts the microphone playing <paramref name="samples"/>, 48 kHz in [-1, 1], from now.</summary>
    internal void Talk(float[] samples)
    {
        signal = samples;
        submitted = 0;
        talkStartMicros = network.Now;
    }

    /// <summary>Stops the microphone at once and releases the binding.</summary>
    internal void StopTalking()
    {
        if (signal == null) return;
        signal = null;
        Service.SetTransmitting(false);
    }

    /// <summary>Starts playing <paramref name="speaker"/>'s stream, as the SPI hands it out, under <paramref name="label"/>.</summary>
    internal IVoicePlaybackStream Listen(string label, ParticipantId speaker) => Listen(label, Service.GetPlaybackStream(speaker));

    /// <summary>Starts playing <paramref name="stream"/> under <paramref name="label"/>, from silence.</summary>
    internal IVoicePlaybackStream Listen(string label, IVoicePlaybackStream stream)
    {
        listening[label] = new Listening(stream);
        return stream;
    }

    internal List<float> Heard(string label) => listening[label].Heard;

    /// <summary>Gets the level, in dBFS, of what <paramref name="label"/> heard from sample <paramref name="from"/> on.</summary>
    internal double LevelSince(string label, int from)
    {
        var heard = Heard(label);
        return from >= heard.Count ? -120 : VoiceAudio.LevelDecibels(heard.ToArray(), from, heard.Count - from);
    }

    private void Capture()
    {
        var playing = signal;
        if (playing == null) return;
        var due = (int)Math.Min(playing.Length, (network.Now - talkStartMicros) * Rate / 1_000_000);
        var count = (int)(due - submitted);
        if (count > 0)
        {
            if (chunk.Length < count) chunk = new float[count];
            Array.Copy(playing, submitted, chunk, 0, count);
            submitted = due;
            Service.SetTransmitting(HoldsBinding);
            Service.SubmitCapture(chunk, count, Rate);
        }

        if (submitted >= playing.Length) StopTalking();
    }

    private void ReadAudio()
    {
        var due = (network.Now - readStartMicros) * Rate / 1_000_000;
        var count = (int)(due - read);
        if (count <= 0) return;
        read = due;
        if (buffer.Length < count) buffer = new float[count];
        foreach (var entry in listening.Values)
        {
            entry.Stream.Read(buffer, 0, count);
            for (var index = 0; index < count; index++) entry.Heard.Add(buffer[index]);
        }
    }

    private sealed class Listening
    {
        internal Listening(IVoicePlaybackStream stream)
        {
            Stream = stream;
        }

        internal IVoicePlaybackStream Stream { get; }

        internal List<float> Heard { get; } = new();
    }
}

/// <summary>A voice control rig's own steps: players who join and talk through the SPI, and a second host to move to.</summary>
internal static class ControlVoiceSteps
{
    /// <summary>Speech at a level a listener hears clearly, for <paramref name="seconds"/>.</summary>
    internal static float[] Speech(double seconds, uint seed) => VoiceSource.Speech(seconds, seed).Signal;

    /// <summary>Adds a player who joins the rig's host, turns voice on, and has a microphone and ears.</summary>
    internal static (ControlPeer Peer, ControlVoice Voice) Join(ControlRig rig, string name, params PackageSpec[] packages)
    {
        var peer = rig.AddPeer(name, packages);
        peer.Runtime.PreferredDisplayName = Display(name);
        rig.JoinAndWait(peer);
        var voice = new ControlVoice(peer, rig.Network);
        voice.Service.Enabled = true;
        return (peer, voice);
    }

    /// <summary>Opens the rig's host to players with voice on, and gives it a microphone and ears.</summary>
    internal static ControlVoice Host(ControlRig rig, string name = "Hoster")
    {
        rig.Host.Runtime.PreferredDisplayName = name;
        rig.StartHosting(new InternalHostSettings("Voice host", 8) { Voice = true });
        var voice = new ControlVoice(rig.Host, rig.Network);
        voice.Service.Enabled = true;
        return voice;
    }

    /// <summary>Runs the rig for <paramref name="micros"/> of virtual time.</summary>
    internal static void RunFor(ControlRig rig, long micros) => rig.Run(() => false, micros);

    /// <summary>Runs until <paramref name="peer"/>'s session lists every name in <paramref name="names"/>, connected.</summary>
    internal static void RunUntilListed(ControlRig rig, ControlPeer peer, params string[] names)
    {
        bool Listed() => names.All(name => peer.Session.Snapshot.Participants.Any(participant => participant.IsConnected && participant.DisplayName == Display(name)));
        TransportAssert.That(rig.Run(Listed), peer.Name + " lists " + string.Join(", ", names));
    }

    /// <summary>Gets the participant id <paramref name="peer"/>'s session gives the player called <paramref name="name"/>.</summary>
    internal static ParticipantId IdOf(ControlPeer peer, string name) => peer.IdOf(Display(name));

    /// <summary>Starts a second host on the rig's network, with voice on, and points every later join at it.</summary>
    internal static ControlPeer SecondHost(ControlRig rig, string name, params PackageSpec[] packages)
    {
        var second = rig.AddPeer(name, packages);
        second.Runtime.PreferredDisplayName = Display(name);
        var started = second.Control.StartHosting(new InternalHostSettings("Second host", 8) { Voice = true });
        TransportAssert.That(started.Succeeded, "the second host starts (" + started.ErrorMessage + ")");
        TransportAssert.That(rig.Run(() => second.Control.GetStatus().HostPhase == InternalHostPhase.Hosting), "the second host serves");
        rig.Resolver.Host = () => second.Provider.Live!.Host?.Descriptor is { } descriptor && second.Factory.Networks.Count > 0
            ? (second.Factory.Networks[^1].Node, descriptor)
            : null;
        return second;
    }

    /// <summary>Gets the display name a peer is given: its name, capitalised.</summary>
    internal static string Display(string name) => char.ToUpperInvariant(name[0]) + name.Substring(1);

    /// <summary>
    /// Gets the link <paramref name="peer"/>'s join runs over: the newest connection its network made that is still up,
    /// the one the join handed its engine. A modified client writes on it past its own engine.
    /// </summary>
    internal static IEngineLink LinkOf(ControlPeer peer)
    {
        var network = peer.Factory.Networks.LastOrDefault() ?? throw new InvalidOperationException("Assertion failed: " + peer.Name + " has a network");
        var attempt = network.Attempts.LastOrDefault(candidate => candidate.Link is { } link && link.State != EngineLinkState.Closed);
        return attempt?.Link ?? throw new InvalidOperationException("Assertion failed: " + peer.Name + " has a link that is up");
    }

    /// <summary>
    /// Sends 25 malformed voice datagrams from <paramref name="peer"/> straight onto its link, past its own engine, as a
    /// modified client would. Twenty strikes within ten seconds mute it automatically for 30 s.
    /// </summary>
    internal static void SendMalformedVoice(ControlPeer peer)
    {
        var link = LinkOf(peer);
        for (var index = 0; index < 25; index++) link.Send(EngineChannel.Voice, new ArraySegment<byte>(new byte[] { 0x01, (byte)index }));
    }
}
