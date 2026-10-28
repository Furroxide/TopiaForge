using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.CommunicationRoutingTests;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class CommunicationChatTests
{
    internal static void Run()
    {
        Audience(false);
        Audience(true);
        Console.WriteLine("CommunicationChatTests: 2 groups passed (real provider routing with voice disabled and enabled).");
    }

    private static void Audience(bool voice)
    {
        using var rig = voice ? ControlRig.WithVoice(681) : new ControlRig(680);
        rig.StartHosting(new InternalHostSettings("Groups", 8) { Voice = voice });
        var alive = rig.AddPeer("alive");
        var dead = rig.AddPeer("dead");
        var otherDead = rig.AddPeer("other-dead");
        using var hostChat = Wire(rig, rig.Host);
        using var deadChat = Wire(rig, dead);
        rig.JoinAndWait(alive); rig.JoinAndWait(dead); rig.JoinAndWait(otherDead);
        using var aliveInbox = alive.Control.OpenSystemChannel(InternalSystemChannelKind.Chat).Value!;
        using var deadInbox = otherDead.Control.OpenSystemChannel(InternalSystemChannelKind.Chat).Value!;
        var hostId = rig.Host.Session.Snapshot.LocalParticipantId!.Value;
        var aliveId = alive.Session.Snapshot.LocalParticipantId!.Value;
        var deadId = dead.Session.Snapshot.LocalParticipantId!.Value;
        var otherId = otherDead.Session.Snapshot.LocalParticipantId!.Value;
        var members = new[] { new CommunicationMember(hostId, 1), new CommunicationMember(aliveId, 1),
            new CommunicationMember(deadId, 2), new CommunicationMember(otherId, 2) };
        var positions = new Positions();
        positions.Values[hostId.Value] = new Vec3(0, 0, 0);
        positions.Values[aliveId.Value] = new Vec3(50, 0, 0);
        using var source = rig.Host.Provider.Engine.SetAvatarSource(positions);
        using var lifetime = new FakeModLifetime();
        var control = (IInternalCommunicationControl)rig.Host.Control;
        using var routing = control.RouteCommunications(lifetime, new CommunicationPolicy(members,
            new[] { new CommunicationRoute(1, 1, 10), new CommunicationRoute(1, 2), new CommunicationRoute(2, 2) })).Value!;
        Advance(rig);
        That(deadChat.Send("private line").Length == 0, "dead player submits a request");
        Advance(rig);
        That(!hostChat.TryTakeLine(out _) && !aliveInbox.TryReceive(out _), "private text is absent from both host display and unauthorized raw client inbox");
        That(deadChat.TryTakeLine(out var echo) && echo.Text == "private line" && deadInbox.TryReceive(out var delivered)
            && ChatWire.TryRead(delivered.Payload, true, out var sender, out var text) && sender == deadId.Value && text == "private line", "sender echo and authorized peer receive only authenticated identity");
        That(hostChat.Send("nearby only").Length == 0, "host can send");
        Advance(rig);
        That(!aliveInbox.TryReceive(out _) && deadInbox.TryReceive(out _), "same proximity and one-way observer groups apply to chat");
        Drain(hostChat); Drain(deadChat);
        routing.SetPolicy(new CommunicationPolicy(members, new[] { new CommunicationRoute(1, 1), new CommunicationRoute(1, 2), new CommunicationRoute(2, 2) }));
        That(hostChat.Send("hearing").Length == 0, "hearing starts");
        Advance(rig);
        That(aliveInbox.TryReceive(out _) && deadInbox.TryReceive(out _), "hearing reaches distant living players and permitted observers");
        Drain(hostChat); Drain(deadChat);
        rig.Host.Control.SetServerMute(deadId, true);
        deadChat.Send("muted secret");
        Advance(rig);
        That(!deadInbox.TryReceive(out _) && !hostChat.TryTakeLine(out _) && !aliveInbox.TryReceive(out _), "moderation wins over group membership");
        rig.Host.Control.SetServerMute(deadId, false);
        // A modified client claims a host author in the chat body. The runtime still accepts requests only.
        aliveInbox.Send(new ArraySegment<byte>(ChatWire.Write("spoof", hostId.Value)), InternalSystemTarget.Host);
        Advance(rig);
        That(!hostChat.TryTakeLine(out _) && !deadInbox.TryReceive(out _), "claimed host identity cannot bypass the group decision");
        routing.SetPolicy(new CommunicationPolicy(new[] { new CommunicationMember(hostId, 1) }, new[] { new CommunicationRoute(1, 1) }));
        Advance(rig, 6_000_000);
        deadChat.Send("unassigned");
        Advance(rig);
        That(!hostChat.TryTakeLine(out _) && !deadChat.TryTakeLine(out _) && !aliveInbox.TryReceive(out _) && !deadInbox.TryReceive(out _), "removing membership takes effect at host routing");
        lifetime.Dispose();
        Advance(rig, 6_000_000);
        deadChat.Send("ordinary again");
        Advance(rig);
        That(hostChat.TryTakeLine(out _) && aliveInbox.TryReceive(out _) && deadInbox.TryReceive(out _), "scope cleanup restores ordinary chat");
        That(rig.Host.Log.Lines.All(line => !line.Contains("private line", StringComparison.Ordinal)), "private payload never enters logs");
    }

    private static SessionChat Wire(ControlRig rig, ControlPeer peer)
    {
        var chat = new SessionChat { Clock = () => rig.Network.Now };
        peer.ModsUpdate = () => chat.Tick(peer.Control, peer.Session.Snapshot);
        chat.Tick(peer.Control, peer.Session.Snapshot);
        return chat;
    }

    private static void Advance(ControlRig rig, long micros = 150_000)
    {
        var until = rig.Network.Now + micros;
        rig.Run(() => rig.Network.Now >= until);
    }

    private static void Drain(SessionChat chat) { while (chat.TryTakeLine(out _)) { } }
}
