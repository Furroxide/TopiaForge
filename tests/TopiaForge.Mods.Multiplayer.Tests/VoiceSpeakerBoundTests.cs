extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Every speaker costs a playback ring, a jitter buffer and a decoder, so how many there are is bounded by the session,
/// never by what the host names: a hostile roster cannot grow them, voice that comes just before its speaker is listed
/// is kept, and a speaker nobody lists loses the stream after a grace.
/// </summary>
internal static class VoiceSpeakerBoundTests
{
    internal static void AHostileRosterCannotGrowTheStreams()
    {
        // The host names 2,000 speakers who are nobody in the session, a hundred to a roster, each with one relayed
        // datagram. At most the session's bound of streams exists at any moment, a player who joins meanwhile still gets
        // one, every stranger's ends after the grace, and what did not fit is dropped and counted.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var b = rig.AddClient("b");
        var limit = rig.Options.MaximumParticipants;
        var payload = SpeechPacket();
        var datagram = new byte[VoicePacket.MaximumBytes(OpusVoiceCodec.MaximumPacketLength)];
        ulong next = 1_000;
        var most = 0;
        for (var batch = 0; batch < 20; batch++)
        {
            var entries = new List<VoiceRosterEntry>();
            for (var index = 0; index < 100; index++)
            {
                var speaker = next++;
                entries.Add(new VoiceRosterEntry(speaker, "ghost" + speaker, false));
            }

            // Links hand datagrams over before session frames, so the roster goes a step ahead of the voice it explains.
            var frame = new ProtocolWriter();
            VoiceMessages.WriteRoster(frame, new ProtocolWriter(), entries);
            b.Link!.Deliver(EngineChannel.Session, frame.ToArray());
            rig.Step();
            foreach (var entry in entries)
            {
                var length = VoicePacket.WriteRelayed(datagram, entry.Speaker, 1, 1, 0, VoiceFrameFlags.None, payload, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
                b.Link.Deliver(EngineChannel.Voice, datagram.AsSpan(0, length).ToArray());
            }

            rig.Step();
            most = Math.Max(most, b.Voice!.GetStatistics().Speakers.Count);
        }

        var flooded = b.Voice!.GetStatistics();
        Assert(flooded.FramesReceived == 2_000, "b's engine took all 2,000 frames the roster explained");
        Assert(most <= limit && b.Voice.Worker.SpeakerCount <= limit,
            "but b never held more than " + limit + " streams (" + most + ") or playing speakers (" + b.Voice.Worker.SpeakerCount + ")");
        Assert(flooded.UnlistedFramesDropped >= 2_000 - limit, "the frames that found no room were dropped and counted (" + flooded.UnlistedFramesDropped + ")");

        // A player who joins while the strangers fill the bound takes the place of the one unlisted longest.
        var c = rig.AddClient("c");
        b.Listen("c");
        c.Source = VoiceSource.Speech(1, seed: 31);
        rig.StepFor(1_000_000);
        var heard = b.Heard("c").ToArray();
        Assert(VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35, "b hears c, a listed player, whatever the strangers took");

        rig.StepFor(3_000_000);
        var after = b.Voice.GetStatistics();
        Assert(after.Speakers.Count == 1 && after.Speakers[0].Speaker.Value == "c" && b.Voice.Worker.SpeakerCount == 1,
            "after the grace only c's stream is left (" + after.Speakers.Count + " streams, " + b.Voice.Worker.SpeakerCount + " playing)");
        Assert(after.UnlistedStreamsEnded >= limit - 1, "every stranger's stream ended (" + after.UnlistedStreamsEnded + ")");
    }

    internal static void VoiceBeforeItsSpeakerIsListedIsKeptForAGrace()
    {
        // b asks for c's stream before c has joined, as an emitter made early would, and for someone who never joins.
        // c joins within the grace and keeps the stream; the other one ends after it, playing silence.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var b = rig.AddClient("b");
        var early = b.Listen("c");
        var stranger = b.Voice!.GetPlaybackStream(new ParticipantId("nobody"));
        rig.StepFor(500_000);
        var c = rig.AddClient("c");
        c.Source = VoiceSource.Speech(2.5, seed: 32);
        rig.StepFor(2_500_000);
        var heard = b.Heard("c").ToArray();
        Assert(!early.IsEnded && VoiceAudio.LevelDecibels(heard, heard.Length - 48_000, 48_000) > -35,
            "c, listed within the grace, keeps the stream b asked for early, and b hears c on it");
        Assert(stranger.IsEnded && b.Voice.GetStatistics().UnlistedStreamsEnded == 1, "the stream of someone never listed ends after the grace");
        Assert(b.Voice.GetStatistics().Speakers.Count == 1, "leaving c's alone");
    }

    private static byte[] SpeechPacket()
    {
        var encoder = new OpusVoiceCodec().CreateEncoder();
        var signal = VoiceTestSignal.Voiced(960 * 5, 3);
        var packet = new byte[OpusVoiceCodec.MaximumPacketLength];
        var length = 0;
        for (var frame = 0; frame < 5; frame++) length = encoder.Encode(signal.AsSpan(frame * 960, 960), packet);
        return packet.AsSpan(0, length).ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
