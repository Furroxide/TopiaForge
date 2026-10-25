using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class PlayersApiTests
    {
        internal static void Run()
        {
            HandlesNeverRetarget();
            AuthorsUseOneCodePath();
            InputEdgesLastOneTick();
            UiRoundTripsAndBounds();
            UiKitMultiplayerConventionTests.RemoteUiIsPlainText();
            ReadsAllocateNothing();
            Console.WriteLine("PlayersApiTests: 5 groups passed.");
        }

        private static void HandlesNeverRetarget()
        {
            using var fake = new FakePlayerDirectory();
            var left = new List<IPlayer>(); var joined = new List<IPlayer>();
            using var leave = fake.SubscribeLeft(player => { Assert(!player.IsPresent, "left callback sees a retired handle"); left.Add(player); });
            using var join = fake.SubscribeJoined(joined.Add);
            var id = new ParticipantId("p1");
            var old = fake.Add(id, "Sam", new MultiplayerPlayerKey("first"));
            var input = old.Input; var ui = old.Ui;
            fake.Remove(id);
            var next = fake.Add(id, "Sam", new MultiplayerPlayerKey("second"));
            Assert(!old.IsPresent && !ReferenceEquals(next, old) && old.Key.Value == "first", "a reused id cannot retarget a handle");
            Assert(old.Damage(new PlayerDamageRequest(10, "test")).ErrorCode == ModErrorCode.NotFound && ui.ShowToast("old").ErrorCode == ModErrorCode.NotFound && !input.IsHeld("fire"), "retained actions stop with their player");
            fake.ReplaceGeneration();
            Assert(!next.IsPresent && fake.TryGet(id, out var replacement) && !ReferenceEquals(next, replacement), "same id AND key cannot survive a generation move");
            Assert(left.Count == 3 && joined.Count == 4, "join/leave reports each incarnation once, including the local player");
            var offThread = Task.Run(() => { try { _ = fake.Players; return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
            Assert(offThread, "directory refuses background-thread use");
        }

        private static void AuthorsUseOneCodePath()
        {
            using var fake = new FakePlayerDirectory();
            Assert(fake.Players.Count == 1 && fake.Local!.IsLocal && fake.Local.IsHost, "solo has exactly one local host");
            var other = fake.Add(new ParticipantId("p1"), "Sam");
            foreach (var player in fake.Players)
            {
                Assert(player.Damage(new PlayerDamageRequest(20, "enemy")).Value!.Current == 80, "same damage path");
                Assert(player.Heal(10, "shop").Value!.Current == 90, "same heal path");
                Assert(player.Teleport(new Vec3(2, 0, 3)).Succeeded && player.TryGetSnapshot(out var pose) && pose!.Position.X == 2, "same teleport path");
            }
            var clicked = 0;
            var result = other.Ui.CreateSurface(new UiSurfaceRequest("shop", "Shop", "", content: new UiButton("buy", "Buy", () => clicked++)));
            Assert(result.Succeeded, "fake captures targeted UI");
            var state = fake.State(other.Id);
            ((UiButton)state.Ui.Surfaces[0].Content!).Activated();
            Assert(clicked == 1 && fake.State(fake.Local!.Id).Ui.Surfaces.Count == 0, "only target receives it");
            using var column = fake.RegisterScoreboardColumn(new ScoreboardColumnDefinition("score", "Score")).Value!;
            Assert(column.SetValue(other.Id, "12", 12).Succeeded && fake.TryGetScore("score", other.Id, out var value, out var sort) && value == "12" && sort == 12, "score values preserve sorting");
            column.Dispose();
            using var replacementColumn = fake.RegisterScoreboardColumn(new ScoreboardColumnDefinition("score", "Score")).Value!;
            column.Dispose();
            Assert(replacementColumn.SetValue(other.Id, "13", 13).Succeeded && fake.TryGetScore("score", other.Id, out value, out sort) && value == "13", "disposing an old column twice cannot remove its replacement");
            fake.IsAuthoritative = false;
            Assert(other.Heal(10, "cheat").ErrorCode == ModErrorCode.NotAuthoritative && column.SetValue(other.Id, "99").ErrorCode == ModErrorCode.NotAuthoritative, "fake enforces authority");
            fake.Remove(other.Id);
            Assert(!fake.TryGetScore("score", other.Id, out _, out _), "departed score disappears");
        }

        private static void InputEdgesLastOneTick()
        {
            var input = new RemotePlayerInput();
            input.Take("fire", true, true, false, 0);
            input.Take("fire", false, false, true, 100);
            input.Advance(1, 1000);
            Assert(!input.IsHeld("fire") && input.WasPressed("fire") && input.WasReleased("fire"), "a short tap retains both edges");
            input.Advance(1, 2000);
            Assert(input.WasPressed("fire"), "same tick retains edges");
            input.Advance(2, 3000);
            Assert(!input.WasPressed("fire") && !input.WasReleased("fire"), "next tick clears edges");
            input.Take("fire", true, true, false, 4000); input.Advance(3, 5000); input.Advance(4, 3_000_000);
            Assert(!input.IsHeld("fire") && input.WasReleased("fire"), "silence releases held input");
            input.Take("fire", true, true, false, 3_000_000); input.Advance(5, 3_000_000);
            input.Take("other", true, true, false, 6_000_000); input.Advance(6, 6_000_000);
            Assert(!input.IsHeld("fire") && input.IsHeld("other"), "another action's heartbeat cannot keep a stale held input alive");
            for (var i = 0; i < 100; i++) input.Take("action" + i, true, true, false, 3_000_000);
            input.Advance(7, 3_000_000);
            Assert(!input.IsHeld("action99"), "action names cannot grow without bound");
        }

        private static void UiRoundTripsAndBounds()
        {
            var clicks = 0;
            var original = new UiColumn(new UiText("<size=999>literal</size>"), new UiButton("buy", "Buy", () => clicks++),
                new UiSlider("amount", "Amount", 1, 5, 2, _ => clicks++), new UiButton("disabled", "No", () => clicks++, enabled: false));
            var writer = new WorldWireWriter(32768); UiNodeWireCodec.Write(writer, original);
            var bytes = new byte[writer.Length]; Array.Copy(writer.Segment.Array!, bytes, bytes.Length);
            var reader = new WorldWireReader(); reader.Reset(bytes);
            string answer = "";
            var decoded = (UiColumn)UiNodeWireCodec.Read(reader, (id, value) => answer = id + value)!;
            Assert(reader.AtEnd && ((UiText)decoded.Children[0]).Text == "<size=999>literal</size>", "wire preserves literal text for the plain renderer");
            ((UiButton)decoded.Children[1]).Activated(); Assert(answer == "buy", "client callback sends only a control id");
            Assert(UiNodeWireCodec.Invoke(original, "buy", "") && clicks == 1, "host finds a declared callback");
            Assert(!UiNodeWireCodec.Invoke(original, "disabled", "") && !UiNodeWireCodec.Invoke(original, "amount", "NaN") && !UiNodeWireCodec.Invoke(original, "amount", "900"), "disabled and out-of-range answers refused");
            for (var cut = 0; cut < bytes.Length; cut++)
            {
                reader.Reset(new ArraySegment<byte>(bytes, 0, cut).ToArray());
                var failed = false;
                try { UiNodeWireCodec.Read(reader, (_, _) => { }); } catch (ArgumentException) { failed = true; }
                Assert(failed, "every truncated composition is refused");
            }
            var deep = new byte[80]; for (var i = 0; i < 40; i++) { deep[i * 2] = 2; deep[i * 2 + 1] = 1; }
            reader.Reset(deep); var bounded = false;
            try { UiNodeWireCodec.Read(reader, (_, _) => { }); } catch (ArgumentException) { bounded = true; }
            Assert(bounded, "depth is bounded before descending into hostile input");
            var graphWriter = new WorldWireWriter(32768);
            UiNodeWireCodec.Write(graphWriter, new UiGraphCanvas("graph", Array.Empty<UiGraphNode>(), Array.Empty<UiGraphEdge>(), _ => clicks++));
            reader.Reset(graphWriter.Segment.ToArray());
            Assert(UiNodeWireCodec.Read(reader, (_, _) => clicks++) is UiText placeholder && placeholder.Text.Contains("host only", StringComparison.Ordinal), "graph canvases become a noninteractive placeholder");
            var oversized = false;
            try { UiNodeWireCodec.Write(new WorldWireWriter(32768), new UiText(new string('x', 4097))); }
            catch (ArgumentException) { oversized = true; }
            Assert(oversized, "host text is bounded before sending");
            var duplicate = new WorldWireWriter(32768);
            duplicate.WriteByte(2); duplicate.WriteVarUInt(2);
            UiNodeWireCodec.Write(duplicate, new UiButton("same", "First", () => { }));
            UiNodeWireCodec.Write(duplicate, new UiButton("same", "Second", () => { }));
            reader.Reset(duplicate.Segment.ToArray()); var duplicateRejected = false;
            try { UiNodeWireCodec.Read(reader, (_, _) => { }); } catch (ArgumentException) { duplicateRejected = true; }
            Assert(duplicateRejected, "decoded compositions run UiComposition validation and reject ambiguous ids");
        }

        private static void ReadsAllocateNothing()
        {
            using var fake = new FakePlayerDirectory();
            fake.Add(new ParticipantId("p1"), "Sam");
            var snapshot = new MultiplayerSessionSnapshot(new MultiplayerSessionId("allocation"), MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Interactive, MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
                new ParticipantId("local"), new[] { new MultiplayerParticipant(new ParticipantId("local"), "Sam", true, true, true, default, null, default), new MultiplayerParticipant(new ParticipantId("p1"), "Guest", false, true) },
                new NetworkTick(1), new SessionSeed(1), new ParticipantId("local"), MultiplayerSessionEndReason.None);
            void Read()
            {
                var players = fake.Players;
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    snapshot.TryGetParticipant(player.Id, out _);
                    _ = player.DisplayName; _ = player.IsLocal; _ = player.IsHost; _ = player.IsPresent;
                    fake.TryGet(player.Id, out _); player.TryGetSnapshot(out _); player.TryGetHealth(out _);
                }
            }
            for (var i = 0; i < 1000; i++) Read();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) Read();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(bytes == 0, "steady directory reads allocate zero, got " + bytes);
            Console.WriteLine("Players directory: 0 B over 10,000 reads of two players.");
        }
        internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Players API: " + message); }
    }
}
