using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PerPlayerStateTests
{
    internal static void CommitRegressions()
    {
        var failures = new List<Exception>();
        foreach (var check in new Action[] { MixedFlushPublishesAtCommit, () => LateBindingReset(false), () => LateBindingReset(true) })
        {
            try { check(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        Console.WriteLine("Private commit regressions: 3 groups passed.");
    }

    private static void MixedFlushPublishesAtCommit()
    {
        var raw = new RawHostClient();
        var player = new ParticipantId("client");
        using var shared = raw.Session.RegisterState(new ReplicatedStateDefinition<PrivateCard>(
            "shared", new PrivateCard(), new JsonTestCodec<PrivateCard>())).Value!;
        using var local = Register(raw.Session);
        using var command = raw.Session.RegisterCommand(new MultiplayerCommandDefinition<PrivateCard, PrivateCard>(
            new MultiplayerCommandType<PrivateCard, PrivateCard>("assign"), new JsonTestCodec<PrivateCard>(),
            new JsonTestCodec<PrivateCard>(), (_, value) => OperationResult<PrivateCard>.Success(value))).Value!;
        raw.Join();
        byte[] Encode(string text) => new JsonTestCodec<PrivateCard>().Encode(new PrivateCard { Text = text }).Value!;
        void Private(ProtocolWriter frame, ProtocolWriter body, byte operation, string text = "") =>
            SystemMessages.Write(frame, body, 0, 0, (byte)InternalSystemChannelKind.PrivateState,
                Envelope(operation, "client", "card", Encode(text)));
        raw.Host.Send((f, w) =>
        {
            Private(f, w, 0);
            Private(f, w, 1, "old");
            ReplicationMessages.WriteStateDelta(f, w, raw.Intern, EngineHarness.Owner, "shared", 1, Encode("old"));
            SessionMessages.WriteCommit(f, w, 1, 0);
        });
        raw.Pump();
        var seen = new List<string>();
        using var watch = local.SubscribeChanged(value => seen.Add(value.Text + "/" + shared.Value.Text));
        var submission = raw.Session.SubmitAsync(new MultiplayerCommandType<PrivateCard, PrivateCard>("assign"), new PrivateCard());
        raw.Pump();
        raw.Host.Send((f, w) =>
        {
            Private(f, w, 1, "new");
            ReplicationMessages.WriteStateDelta(f, w, raw.Intern, EngineHarness.Owner, "shared", 2, Encode("new"));
            ReplicationMessages.WriteResult(f, w, MessageType.CommandResult, 1, 2, WireOutcome.Success(Encode("new")));
        });
        raw.Pump();
        raw.Host.Send((f, w) => SystemMessages.Write(f, w, 0, 0, (byte)InternalSystemChannelKind.PrivateState,
            Envelope(1, "client", "cached", Encode("new"))));
        raw.Pump();
        using var cached = Register(raw.Session, "cached");
        That(local.Get(player).Value!.Text == "old" && shared.Value.Text == "old" && seen.Count == 0 && !submission.IsCompleted,
            "a mixed split flush keeps private reads, callbacks, shared reads and command completion at the previous commit");
        That(cached.Get(player).Value!.Text == "", "binding during a split flush cannot adopt uncommitted private bytes");
        raw.Host.Send((f, w) => SessionMessages.WriteCommit(f, w, 2, 1));
        raw.Pump();
        That(local.Get(player).Value!.Text == "new" && shared.Value.Text == "new" && cached.Get(player).Value!.Text == "new",
            "commit publishes the complete shared and private view");
        That(seen.SequenceEqual(new[] { "new/new" }) && submission.IsCompleted && submission.Result.Result.Succeeded,
            "private callback sees committed shared state and command completion follows");
        raw.Host.Send((f, w) => Private(f, w, 2));
        raw.Pump();
        That(local.Get(player).Value!.Text == "new" && seen.Count == 1, "field removal also waits for commit");
        raw.Host.Send((f, w) => SessionMessages.WriteCommit(f, w, 3, 1));
        raw.Pump();
        That(local.Get(player).Value!.Text == "" && seen.SequenceEqual(new[] { "new/new", "/new" }), "committed removal clears presentation once");
        raw.Host.Send((f, w) =>
        {
            Private(f, w, 1, "restored");
            SessionMessages.WriteCommit(f, w, 4, 1);
        });
        raw.Pump();
        That(local.Get(player).Value!.Text == "restored" && seen.Count == 3, "committed private value is visible before link loss");
        raw.Host.Send((f, w) => Private(f, w, 1, "uncommitted"));
        raw.Pump();
        raw.Network.Sever(raw.ClientEnd);
        raw.Pump();
        That(!local.Get(player).Succeeded && seen.SequenceEqual(new[] { "new/new", "/new", "restored/new", "/new" }),
            "disconnect resets presentation immediately and discards uncommitted private bytes");
    }

    private static void LateBindingReset(bool disconnect)
    {
        using var h = new EngineHarness();
        using var host = Register(h.HostSession);
        var client = h.AddClient("alpha");
        h.Flush();
        host.Set(Alpha, new PrivateCard { Text = "cached" });
        h.Flush();
        using var local = Register(client.Session);
        var visible = local.Get(Alpha).Value!.Text;
        That(visible == "cached", "late registration adopts the committed card");
        var seen = new List<string>();
        using var watch = local.SubscribeChanged(value => { visible = value.Text; seen.Add(value.Text); });
        if (disconnect) { h.Network.Sever(client.Link); h.Advance(); }
        else { host.Dispose(); h.Flush(); }
        That(visible == "" && seen.SequenceEqual(new[] { "" }),
            "late adoption initializes the notification baseline before " + (disconnect ? "disconnect" : "field removal"));
        That(disconnect ? !local.Get(Alpha).Succeeded : local.Get(Alpha).Value!.Text == "", "reset read agrees with presentation");
    }
}
