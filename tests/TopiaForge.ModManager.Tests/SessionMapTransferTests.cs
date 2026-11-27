using TopiaForge.ModManager.Sessions;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class SessionMapTransferTests
    {
        internal static void Run()
        {
            Wire();
            using (var rig = new Rig())
            {
                var pending = rig.Read();
                rig.Frame();
                var sent = rig.HostChannel.SentMessages;
                rig.Host.Pump(rig.HostChannel, rig.State);
                Assert(rig.HostChannel.SentMessages == sent, "one-message credit window blocks further chunks until consumed");
                rig.Until(pending);
                Assert(pending.Result.Succeeded && pending.Result.Value!.SequenceEqual(rig.Bytes) && rig.Progress == 3, "exact transfer progresses once per chunk over a bounded link");
                var duplicate = rig.Read();
                rig.Frame(); rig.Frame();
                Assert(!duplicate.IsCompleted && rig.Accepted == 1, "host serves once per authenticated stream");
                rig.Receiver.Pump(InternalReadinessLimits.MaximumSeconds + 1);
                rig.Until(duplicate);
                Assert(duplicate.Result.ErrorCode == ModErrorCode.TimedOut, "withheld transfer ends within the deadline");
            }
            using (var rig = new Rig())
            {
                using var cancellation = new CancellationTokenSource();
                var pending = rig.Read(cancellation.Token);
                rig.Frame(); cancellation.Cancel(); rig.Dispatcher.Drain(); rig.Until(pending);
                Assert(pending.Result.ErrorCode == ModErrorCode.Cancelled, "leaving cancels partial download");
                rig.Net.Leave(rig.Guest); rig.Net.Join(rig.Guest);
                var reconnect = rig.Read(); rig.Until(reconnect);
                Assert(reconnect.Result.Succeeded, "reconnect gets a new stream and no cancelled bytes survive");
            }
            using (var rig = new Rig())
            {
                var pending = rig.Read(); rig.Frame();
                rig.Net.LoseLink(rig.Guest); rig.Net.Join(rig.Guest);
                rig.Until(pending);
                Assert(pending.Result.Succeeded && rig.Accepted == 2, "stream replacement resets offset and requests the full revision again");
            }
            using (var rig = new Rig())
            {
                rig.Joining = false;
                var pending = rig.Read(); rig.Frame(); rig.Frame();
                Assert(rig.Accepted == 0 && rig.HostChannel.SentMessages == 0, "ready peers cannot fetch map copies");
                rig.Receiver.Clear(); rig.Until(pending);
            }
            using (var rig = new Rig())
            {
                rig.Bytes[0] ^= 1;
                var pending = rig.Read(); rig.Until(pending);
                Assert(pending.Result.ErrorCode == ModErrorCode.Conflict, "wrong host bytes fail receiver SHA-256 before storage");
            }
            using (var rig = new Rig())
            {
                var pending = rig.Read(); rig.Frame();
                rig.State = HostWorldState.Menu;
                rig.Frame(); rig.Receiver.Clear(); rig.Until(pending);
                Assert(pending.Result.ErrorCode == ModErrorCode.Cancelled, "superseding world cancels old work");
            }
            using (var rig = new Rig())
            {
                var pending = rig.Read(); rig.Frame();
                Assert(rig.GuestChannel.TryReceive(out var first), "first chunk arrives");
                rig.Receiver.Receive(first.Payload);
                rig.Receiver.Receive(first.Payload);
                rig.Until(pending);
                Assert(pending.Result.ErrorCode == ModErrorCode.Conflict, "duplicate or reordered chunks refuse rather than append");
            }
            using (var rig = new Rig())
            {
                var pending = rig.Read(); rig.Frame();
                Assert(rig.GuestChannel.TryReceive(out var first), "chunk available for truncation");
                rig.Receiver.Receive(first.Payload.Take(first.Payload.Length - 1).ToArray());
                rig.Until(pending);
                Assert(pending.Result.ErrorCode == ModErrorCode.Conflict, "truncated chunk fails closed");
            }
            using (var rig = new Rig())
            {
                var current = rig.Read();
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var stale = rig.Read(cancellation.Token);
                rig.Until(stale); rig.Until(current);
                Assert(stale.Result.ErrorCode == ModErrorCode.Cancelled && current.Result.Succeeded,
                    "a cancelled store callback cannot erase a newer transfer");
            }
            foreach (var code in new[] { ModErrorCode.Conflict, ModErrorCode.Unavailable, ModErrorCode.TimedOut })
            {
                var words = FollowFailureWords.Map(code);
                Assert(words.Message.Contains("host") && !words.Message.Contains("://"), "map failures carry safe actionable wording");
            }
            Console.WriteLine("SessionMapTransferTests passed (wire roundtrip/truncation; 13 transfer lifecycle/security checks).");
        }

        private static void Wire()
        {
            var state = State(SessionMapStoreTests.Identity(Project()));
            var writer = new WorldWireWriter(SessionFollowWire.MaximumMessageBytes);
            SessionFollowWire.Write(writer, state);
            var bytes = writer.Segment.ToArray();
            Assert(SessionFollowWire.TryRead(new WorldWireReader(), bytes, out var read)
                && read!.Map!.Sha256 == state.Map!.Sha256 && read.Map.SceneId == "scene", "map identity roundtrips in follow");
            for (var size = 0; size < bytes.Length; size++)
                Assert(!SessionFollowWire.TryRead(new WorldWireReader(), bytes.Take(size).ToArray(), out _), "every truncated identity is refused");
            Assert(!SessionFollowWire.TryRead(new WorldWireReader(), bytes.Concat(new byte[] { 0 }).ToArray(), out _), "trailing bytes refused");
            SessionFollowWireTests.Run();
        }

        private static HostWorldState State(InternalMapIdentity map) => HostWorldState.World("0123456789abcdef0123456789abcdef",
            new LaunchRequest("example.target"), "example.mode", "example.world", null, ModTransitions.SceneReplacement, null, null, map);

        private sealed class Rig : IDisposable
        {
            internal readonly LoopbackNetPair Net = new LoopbackNetPair { HostWindowMessages = 1 };
            internal readonly LoopbackControl Guest;
            internal readonly LoopbackChannel HostChannel, GuestChannel;
            internal readonly HostDispatcher Dispatcher = new HostDispatcher(error => throw error);
            internal readonly SessionMapHost Host;
            internal readonly SessionMapReceiver Receiver;
            internal readonly byte[] Bytes = new byte[SessionMapHost.ChunkBytes * 2 + 17];
            internal HostWorldState State;
            internal bool Joining = true;
            internal int Progress, Accepted;
            internal Rig()
            {
                new Random(17).NextBytes(Bytes);
                State = SessionMapTransferTests.State(SessionMapStoreTests.Identity(Bytes));
                Guest = Net.AddPlayer("guest");
                HostChannel = (LoopbackChannel)Net.Host.OpenSystemChannel(InternalSystemChannelKind.SceneFollow).Value!;
                GuestChannel = (LoopbackChannel)Guest.OpenSystemChannel(InternalSystemChannelKind.SceneFollow).Value!;
                Net.StartHosting(); Net.Join(Guest); Net.Host.Pump(); Guest.Pump();
                Host = new SessionMapHost(_ => Bytes, _ => Joining);
                Receiver = new SessionMapReceiver(() => GuestChannel, Dispatcher, _ => Progress++);
            }
            internal Task<OperationResult<byte[]>> Read(CancellationToken token = default) => Receiver.ReadAsync(State, token, null);
            internal void Frame()
            {
                Dispatcher.Drain();
                Receiver.Pump(0.02f);
                Guest.Flush(); Net.Host.Pump();
                while (HostChannel.TryReceive(out var request)) if (Host.Receive(HostChannel, State, request)) Accepted++;
                Host.Pump(HostChannel, State); Net.Host.Flush(); Guest.Pump();
                // Delivery waits one frame so backpressure is observable.
                Dispatcher.Drain();
            }
            internal void Until(Task task)
            {
                var limit = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < limit)
                {
                    Frame();
                    while (GuestChannel.TryReceive(out var message)) Receiver.Receive(message.Payload);
                    Thread.Yield();
                }
                Assert(task.IsCompleted, "bounded in-memory transfer completed");
            }
            public void Dispose() { Receiver.Clear(); Dispatcher.Drain(); HostChannel.Dispose(); GuestChannel.Dispose(); Dispatcher.Dispose(); }
        }
    }
}
