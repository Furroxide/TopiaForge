using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PerPlayerStateTests
{
    private static void UnreadPrivateMessagesEndWithTheirStream()
    {
        foreach (var ending in new[] { "disconnect", "host end", "client shutdown" })
        {
            using var h = new EngineHarness(MultiplayerProcessKind.Interactive);
            using var state = Register(h.HostSession);
            var a = h.AddClient("alpha");
            var b = h.AddClient("beta");
            using var local = Register(a.Session);
            h.Flush();
            using var host = h.Host.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
            using var alpha = a.Engine.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
            using var beta = b.Engine.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
            using var chatHost = h.Host.OpenSystemChannel(InternalSystemChannelKind.Chat).Value!;
            using var chat = a.Engine.OpenSystemChannel(InternalSystemChannelKind.Chat).Value!;
            var bytes = new ArraySegment<byte>(new byte[] { 7, 9 });
            That(host.Send(bytes, InternalSystemTarget.Only(Alpha)).Value == 1, "one private recipient before " + ending);
            That(chatHost.Send(bytes, InternalSystemTarget.Only(Alpha)).Value == 1, "unrelated channel queued before " + ending);
            state.Set(Alpha, new PrivateCard { Text = "retained baseline" });
            h.Flush();
            That(alpha.GetStatistics().Pending == 1, "private payload is deliberately unread before " + ending);
            That(!beta.TryReceive(out _), "another recipient never receives the private payload");
            var generation = alpha.Generation;
            if (ending == "disconnect") { h.Network.Sever(a.Link); h.Advance(); }
            else if (ending == "host end") { h.Host.Shutdown(MultiplayerSessionEndReason.HostEnded); h.Flush(); }
            else a.Engine.Shutdown(MultiplayerSessionEndReason.HostEnded);
            That(alpha.Generation != generation, "stream end advances generation: " + ending);
            That(!alpha.TryReceive(out _), "private inbox is empty immediately after " + ending);
            That(alpha.GetStatistics().Pending == 0 && alpha.GetStatistics().Dropped == 1,
                "unread private payload is counted as dropped: " + ending);
            That(chat.TryReceive(out var unrelated) && unrelated.Payload.SequenceEqual(bytes.Array!),
                "stream end preserves unrelated channel messages: " + ending);
            if (ending != "disconnect") continue;
            h.Join(a.Engine, "alpha"); h.Flush();
            That(local.Get(Alpha).Value!.Text == "retained baseline", "reconnect restores the valid retained contract baseline");
            That(!alpha.TryReceive(out _), "reconnect cannot revive an old runtime-private payload");
            That(host.Send(new ArraySegment<byte>(new byte[] { 11 }), InternalSystemTarget.Only(Alpha)).Value == 1,
                "reconnect opens fresh private credits");
            h.Flush();
            That(alpha.TryReceive(out var fresh) && fresh.Sender.Value == "host" && fresh.Payload.SequenceEqual(new byte[] { 11 }),
                "fresh reconnect payload retains authenticated sender and content");
            That(!alpha.TryReceive(out _) && !beta.TryReceive(out _), "fresh private delivery stays recipient scoped and bounded");
        }
    }
}
