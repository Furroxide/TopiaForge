using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Production chat over real providers, authenticated links, host moderation and the virtual network.</summary>
internal static class InSessionChatProviderTests
{
    internal static void Run()
    {
        ConversationAndAbuse(voice: false);
        ConversationAndAbuse(voice: true);
        HostileHost();
        HostBackpressureKeepsTheDraftAndAllowance();
        Console.WriteLine("InSessionChatProviderTests passed: real host and two clients, with and without voice, plus hostile host relays and host backpressure.");
    }

    private static void ConversationAndAbuse(bool voice)
    {
        using var rig = voice ? ControlRig.WithVoice(509) : new ControlRig(508);
        rig.Host.Runtime.PreferredDisplayName = "Sam";
        rig.StartHosting(new InternalHostSettings("Chat", 8) { Voice = voice });
        var first = rig.AddPeer("first"); first.Runtime.PreferredDisplayName = "Lee";
        var second = rig.AddPeer("second"); second.Runtime.PreferredDisplayName = "Kim";
        using var hostChat = Wire(rig, rig.Host);
        using var firstChat = Wire(rig, first);
        using var secondChat = Wire(rig, second);
        rig.JoinAndWait(first); rig.JoinAndWait(second);
        rig.Run(() => firstChat.IsAvailable && secondChat.IsAvailable);
        That(hostChat.Send("hello everyone").Length == 0, "host sends");
        var firstWelcome = false; var secondWelcome = false;
        That(rig.Run(() => (firstWelcome |= Heard(firstChat, "hello everyone")) & (secondWelcome |= Heard(secondChat, "hello everyone"))), "host text reaches both clients");
        Drain(hostChat);
        That(firstChat.Send("hello host").Length == 0, "client sends to host");
        var hostHeard = false; var firstHeard = false; var secondHeard = false;
        That(rig.Run(() => (hostHeard |= Heard(hostChat, "hello host")) & (firstHeard |= Heard(firstChat, "hello host")) & (secondHeard |= Heard(secondChat, "hello host"))), "host relays once to itself, sender and other player");
        That(!hostChat.TryTakeLine(out _) && !firstChat.TryTakeLine(out _) && !secondChat.TryTakeLine(out _), "no duplicate echo");
        var firstId = rig.Host.IdOf("Lee");
        That(first.Control.SetServerMute(rig.Host.IdOf("Kim"), true).ErrorCode == ModErrorCode.NotAuthoritative, "client cannot moderate");
        That(rig.Host.Control.SetServerMute(firstId, true).Succeeded, "host mutes chat with voice " + voice);
        That(firstChat.Send("muted line").Length == 0, "muted client attempts to send");
        Advance(rig, 200_000);
        That(rig.Host.Control.GetStatistics().SystemChannels.Single(channel => channel.Kind == InternalSystemChannelKind.Chat).Dropped > 0,
            "the provider itself drops muted text at ingress, before the host's relay");
        That(!hostChat.TryTakeLine(out _) && !secondChat.TryTakeLine(out _), "muted line never reaches host or other players");
        first.Control.Leave();
        That(rig.Run(() => first.Control.Role == InternalSessionRole.Solo), "muted player leaves");
        rig.JoinAndWait(first);
        firstId = rig.Host.IdOf("Lee");
        That(rig.Host.Control.IsServerMuted(firstId), "mute follows authenticated identity across rejoin");
        That(firstChat.Send("still muted").Length == 0, "rejoined muted client attempts to send");
        Advance(rig, 100_000);
        That(!hostChat.TryTakeLine(out _) && !secondChat.TryTakeLine(out _), "rejoining does not evade a server mute");
        That(rig.Host.Control.SetServerMute(firstId, false).Succeeded, "host unmutes");

        // A modified client bypasses SessionChat's sender rules but still passes through its authenticated provider.
        first.ModsUpdate = null; firstChat.Dispose();
        using var raw = Open(first.Control);
        Advance(rig, 6_000_000);
        for (var index = 0; index < 10; index++) raw.Send(new ArraySegment<byte>(ChatWire.Write("flood " + index)), InternalSystemTarget.Host);
        Advance(rig, 100_000);
        That(Drain(hostChat) == 3 && Drain(secondChat) == 3, "host independently limits a malicious sender to three messages");
        raw.Send(new ArraySegment<byte>(ChatWire.Write("forged host", rig.Host.Session.Snapshot.LocalParticipantId!.Value.Value)), InternalSystemTarget.Host);
        var oversized = new byte[261]; oversized[0] = 1; oversized[2] = 1; oversized[3] = 1; Array.Fill(oversized, (byte)'a', 4, 257);
        raw.Send(new ArraySegment<byte>(oversized), InternalSystemTarget.Host);
        Advance(rig, 100_000);
        That(Drain(hostChat) == 0 && Drain(secondChat) == 0, "oversize text and sender claims are rejected");
        That(rig.Host.Control.Ban(firstId).Succeeded, "host bans offender");
        That(rig.Run(() => first.Control.Role == InternalSessionRole.Solo), "ban closes authenticated connection");
        first.Control.Join(first.Target(), new InternalJoinSettings());
        That(rig.Run(() => first.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) && first.Control.GetStatus().JoinFailure == InternalLiveFailure.Banned, "banned identity cannot rejoin to chat");
        That(secondChat.Send("still here").Length == 0, "other player remains able to chat");
        That(rig.Run(() => Heard(hostChat, "still here")), "moderation affects only its target");
        rig.Host.Control.StopHosting();
        That(rig.Host.Control.GetStatistics().ServerMutes == 0, "hosting end clears mute state");
        That(rig.Host.Log.Lines.Concat(first.Log.Lines).Concat(second.Log.Lines).All(line => !line.Contains("hello host", StringComparison.Ordinal) && !line.Contains("flood ", StringComparison.Ordinal)), "chat content never reaches runtime logs");
    }

    private static void HostBackpressureKeepsTheDraftAndAllowance()
    {
        using var rig = new ControlRig(511);
        rig.StartHosting(new InternalHostSettings("Chat", 8));
        using var hostChat = Wire(rig, rig.Host);
        That(hostChat.Send("alone").Length == 0 && Heard(hostChat, "alone"), "a host without guests can still chat locally");
        Advance(rig, 6_000_000);
        var guest = rig.AddPeer("guest");
        using var guestChat = Wire(rig, guest);
        rig.JoinAndWait(guest);
        That(rig.Run(() => guestChat.IsAvailable), "the guest's chat is ready");
        var channel = rig.Host.Provider.Engine.FindSystemChannel(InternalSystemChannelKind.Chat)!;
        // Fill the real provider's window without spending SessionChat's allowance. Invalid chat frames are ignored by
        // the receiver, so draining this setup cannot spend its display allowance and hide the recovery messages.
        var filler = new ArraySegment<byte>(new byte[] { 0 });
        for (var index = 0; index < InternalSystemLimits.HostWindowMessages; index++)
            That(channel.Send(filler, InternalSystemTarget.Everyone).Value == 1, "fill the real host window");
        That(!channel.CanSend(InternalSystemTarget.Everyone, filler.Count), "the host window is full");
        for (var index = 0; index < 3; index++)
            That(hostChat.Send("not delivered").StartsWith("The message could not be sent", StringComparison.Ordinal), "all skipped recipients report refusal");
        That(!hostChat.TryTakeLine(out _), "an all-skipped broadcast produces no false local echo");
        var started = rig.Network.Now;
        That(rig.Run(() => channel.CanSend(InternalSystemTarget.Everyone, filler.Count)), "consuming the pending frames reopens the window");
        That(rig.Network.Now - started < 2_000_000, "the window recovers before a sender token could refill");
        for (var index = 0; index < 3; index++)
            That(hostChat.Send("recovered " + index).Length == 0, "three refusals leave the host's full burst intact");
        That(hostChat.Send("fourth").Contains("Wait two seconds", StringComparison.Ordinal), "successful host sends spend the allowance");
        That(rig.Run(() => Heard(guestChat, "recovered 2")), "the recovered send reaches the guest");

        var second = rig.AddPeer("second");
        using var secondChat = Wire(rig, second);
        rig.JoinAndWait(second);
        Advance(rig, 6_000_000);
        var firstId = guest.Session.Snapshot.LocalParticipantId!.Value;
        var onlyFirst = InternalSystemTarget.Only(firstId);
        for (var index = 0; index < InternalSystemLimits.HostWindowMessages && channel.CanSend(onlyFirst, filler.Count); index++)
            channel.Send(filler, onlyFirst);
        That(!channel.CanSend(onlyFirst, filler.Count), "one peer's window is full again");
        That(hostChat.Send("healthy peer").Length == 0 && Heard(hostChat, "healthy peer"), "one full peer does not refuse a partial broadcast");
        That(rig.Run(() => Heard(secondChat, "healthy peer")), "the healthy peer still receives the broadcast");
    }


    private static void HostileHost()
    {
        using var rig = new ControlRig(510);
        rig.StartHosting(new InternalHostSettings("Chat", 8));
        var client = rig.AddPeer("client");
        using var received = Wire(rig, client);
        using var rawHost = Open(rig.Host.Control);
        rig.JoinAndWait(client);
        var host = rig.Host.Session.Snapshot.LocalParticipantId!.Value.Value;
        var text = "<size=999>plain</size> 192.0.2.8 [2001:db8::1] host.test:27250";
        for (var index = 0; index < 20; index++) rawHost.Send(new ArraySegment<byte>(ChatWire.Write(text, host)), InternalSystemTarget.Everyone);
        Advance(rig, 100_000);
        var count = 0;
        while (received.TryTakeLine(out var line))
        {
            count++;
            That(line.Text.Length <= 256 && !line.Text.Contains("192.0.2.8", StringComparison.Ordinal) && !line.Text.Contains("2001:db8", StringComparison.Ordinal) && !line.Text.Contains("host.test", StringComparison.Ordinal), "host relays are bounded and private");
        }
        That(count == 4, "host flood is independently rate-limited by receiver: the burst of three and one message of slack for the second link");
        rawHost.Send(new ArraySegment<byte>(ChatWire.Write("fake player", "unknown")), InternalSystemTarget.Everyone);
        Advance(rig, 100_000);
        That(!received.TryTakeLine(out _), "unknown relayed player cannot impersonate a participant");
    }

    private static SessionChat Wire(ControlRig rig, ControlPeer peer)
    {
        var chat = new SessionChat { Clock = () => rig.Network.Now };
        peer.ModsUpdate = () => chat.Tick(peer.Control, peer.Session.Snapshot);
        chat.Tick(peer.Control, peer.Session.Snapshot);
        return chat;
    }

    private static IInternalSystemChannel Open(IInternalMultiplayerControl control)
    {
        var result = control.OpenSystemChannel(InternalSystemChannelKind.Chat);
        if (!result.TryGetValue(out var channel)) throw new InvalidOperationException("The test chat channel could not open.");
        return channel;
    }

    private static void Advance(ControlRig rig, long micros)
    {
        var until = rig.Network.Now + micros;
        That(rig.Run(() => rig.Network.Now >= until, micros + 20_000), "virtual clock advances");
    }
    private static bool Heard(SessionChat chat, string text)
    {
        var heard = false;
        while (chat.TryTakeLine(out var line)) heard |= line.Text == text;
        return heard;
    }
    private static int Drain(SessionChat chat) { var count = 0; while (chat.TryTakeLine(out _)) count++; return count; }
}
