using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A talker whose game restarts and rejoins while its participant record is kept: the new stream's sequences start at 1
/// again, so a stream of a single datagram before it leaves nothing to tell the two apart by number. The host must not
/// lose the new stream's first datagram, and listeners must notice the restart even when the path loses it.
/// </summary>
internal static class VoiceRestartTests
{
    internal static void ARestartIsHeardFromTheDatagramBesideItsAcknowledgement()
    {
        // a says two frames, one datagram. Then a's game restarts, and the new engine talks the moment it is ready, so its
        // first datagram leaves with the acknowledgement of the session snapshot. Links hand datagrams over before session
        // frames, so the host reads that datagram before it learns the connection is ready.
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var hostEars = rig.Listen(rig.Host);
        rig.Talk(a, 2000, 2);
        rig.Advance(5);
        rig.Flush();
        Assert(hostEars.From("a").Count == 2, "the host hears a's first stream");
        rig.Network.Sever(aLink);
        rig.Flush();

        var restarted = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("voice-session"), rig.NowMicros));
        rig.Connect(restarted, "a");
        for (var tick = 0; tick < 60 && restarted.Snapshot.State != MultiplayerSessionState.Ready; tick++) rig.Advance();
        Assert(restarted.Snapshot.State == MultiplayerSessionState.Ready, "the restarted engine rejoins");
        TalkInPairs(rig, restarted, 3000, 10);
        rig.Flush();

        var second = hostEars.From("a").Skip(2).ToList();
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.NotReady) == 0, "the host drops nothing for arriving before the acknowledgement");
        Assert(second.Select(frame => frame.Id).SequenceEqual(Enumerable.Range(3000, 10).Select(id => (short)id)) && second[0].Sequence == 1,
            "it hears the new stream from its first frame, sequence 1");
        var (_, decoded, statistics) = hostEars.Play("a");
        Assert(statistics.Restarts == 1 && rig.Codec.LastDecoder!.Resets >= 1, "the restart is noticed, and the decoder starts again");
        Assert(decoded.SequenceEqual(new short[] { 2000, 2001 }.Concat(Enumerable.Range(3000, 10).Select(id => (short)id))), "both streams play in full");
    }

    internal static void ARestartIsNoticedWhenItsFirstDatagramIsLost()
    {
        // The same restart, two seconds after a's link went, but now the path loses the new stream's first datagram.
        // Nothing in the numbers then says a new stream began: the old one ended at sequence 2, and what arrives carries on
        // from 3. The host stamps every relayed frame with the generation of its speaker's stream, so listeners notice.
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var hostEars = rig.Listen(rig.Host);
        var bEars = rig.Listen(b);
        rig.Talk(a, 2000, 2);
        rig.Advance(5);
        rig.Flush();
        rig.Network.Sever(aLink);
        rig.Advance(2 * 60);

        var (restarted, link) = rig.AddClient("a");
        rig.Talk(restarted, 3000, 100);
        link.Outbound!.Held = true;
        rig.Advance(3);
        link.Outbound.Held = false;
        Assert(link.Outbound.DatagramsLost[(int)EngineChannel.Voice] == 1, "the path lost the new stream's first datagram");
        rig.Advance((2 * 60) + 30);
        rig.Flush();

        foreach (var (name, ears) in new[] { ("the host", hostEars), ("b", bEars) })
        {
            var heard = ears.From("a");
            Assert(heard.Count == 2 + 98 && heard[2].Sequence == 3 && heard[2].Stream != heard[1].Stream,
                name + " hears the new stream from sequence 3, stamped with a new generation");
            var (_, decoded, statistics) = ears.Play("a");
            Assert(statistics.Restarts == 1 && rig.Codec.LastDecoder!.Resets >= 1, name + " notices the restart, and its decoder starts again");
            Assert(ears.LastBuffer!.TargetDelayMicros <= 60_000,
                name + "'s delay target forgets the old stream's timing (" + (ears.LastBuffer.TargetDelayMicros / 1_000) + " ms)");
            Assert(decoded.Skip(2).SequenceEqual(Enumerable.Range(3002, 98).Select(id => (short)id)), name + " plays the new stream in full from there");
        }
    }

    /// <summary>
    /// Submits a talker's first <paramref name="frames"/> frames two at a time, so each pair leaves in one datagram at
    /// once, starting now, and moves the rig on a tick after each pair.
    /// </summary>
    private static void TalkInPairs(VoiceSessionHarness rig, SessionEngine talker, short idBase, int frames)
    {
        for (var index = 0; index < frames; index++)
        {
            var timestamp = (ulong)index;
            var id = (short)(idBase + index);
            var flags = index == frames - 1 ? VoiceFrameFlags.EndOfTalk : VoiceFrameFlags.None;
            var result = talker.SubmitVoiceFrame(timestamp, flags, rig.Codec.Packet(id, index == 0 ? (short)0 : (short)(id - 1)));
            Assert(result is VoiceSubmitResult.Sent or VoiceSubmitResult.Held, "frame " + timestamp + " is taken (" + result + ")");
            if (index % 2 == 1) rig.Advance();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
