using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotSpeechCompletionTests
    {
        internal static void Run()
        {
            FarHostSequential(); SequentialAtPaceBoundary(); NoRecipients(); AllListeners(); Failures(); AcknowledgementValidation(); ResetContinuation();
            WorkerCancellation(); FrameDeadlines(); HeardBeforeDeadline();
            DeclinedAdmissions(); DeclineValidation(); SchemaCompatibility(); ActiveCapDecline();
            ListenerLoss(); CompletedListenerDeparture(); LocalProviderCancellation();
            Console.WriteLine("Robot speech completion: 17 focused groups passed.");
        }

        private static void HeardBeforeDeadline()
        {
            using var rig = new RobotSpeechRig(2);
            rig.Move(rig.Host, 60);
            var finished = rig.Guests[0];
            var silent = rig.Guests[1];
            var speech = rig.World.Host.World.Speech;
            var call = rig.Robot.SayAsync("One listener hears it all; the other never reports.");
            rig.World.Frames(5);
            Check(speech.ActiveCount == 1 && finished.Body.SpeechPlays == 1 && silent.Body.SpeechPlays == 1 && rig.Canonical.SpeechPlays == 0,
                "both guests play the line while the host is out of earshot");
            var started = speech.StartedAt(0);
            finished.Body.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
            rig.World.Frames(5);
            Check(!call.IsCompleted, "the host still waits for the other listener's report");
            // Keep presence current while the silent guest neither finishes nor reaches its own deadline.
            for (var now = started + 250_000; now <= started + 14_750_000; now += 250_000)
            {
                rig.World.Clock.Now = now;
                rig.World.Frame();
            }
            Check(!call.IsCompleted && speech.ActiveCount == 1 && !silent.Body.SpeechTokens.Single().IsCancellationRequested,
                "the silent listener stays admitted and its copy keeps playing");

            rig.Log.Clear();
            rig.World.Clock.Now = started + 14_999_999;
            speech.Tick();
            Check(!call.IsCompleted, "the line stays pending until the deadline");
            rig.World.Clock.Advance(1);
            speech.Tick();
            Check(call.IsCompletedSuccessfully && call.Result.Succeeded && call.Result.Value && speech.ActiveCount == 0,
                "a line one listener heard to the end succeeds at the deadline instead of timing out");
            Check(rig.Log.Any(packet => packet.Payload[1] == RobotSpeechWire.Cancel && packet.To.Equals(silent.Peer.Control.Id)),
                "the deadline still stops the copy that never reported");
        }

        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Robot speech completion: " + reason); }

        private static void FarHostSequential()
        {
            using var rig = new RobotSpeechRig();
            rig.Move(rig.Host, 60);
            var completed = 0;
            async Task Dialogue()
            {
                var first = await rig.Robot.SayAsync("First complete line.");
                Check(first.Succeeded && first.Value, "first remote playback completes successfully"); completed++;
                var second = await rig.Robot.SayAsync("Second complete line.");
                Check(second.Succeeded && second.Value, "second remote playback completes successfully"); completed++;
            }
            var dialogue = Dialogue(); rig.World.Frames(5);
            Check(completed == 0 && !dialogue.IsCompleted, "FarHostSequential: await must remain pending before remote completion");
            var body = rig.Guests[0].Body;
            Check(body.SpeechPlays == 1 && rig.Canonical.SpeechPlays == 0, "far host never synthesizes or bypasses earshot");
            rig.Later();
            Check(!dialogue.IsCompleted && body.SpeechPlays == 1 && !body.SpeechTokens[0].IsCancellationRequested,
                "elapsed pace alone cannot certify or truncate playback");
            body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(completed == 1 && body.SpeechPlays == 2 && !dialogue.IsCompleted,
                "second awaited line starts only after the first real completion report");
            Check(!body.SpeechTokens[0].IsCancellationRequested, "completed first line is never replaced or cancelled");
            rig.Later(); Check(completed == 1 && !body.SpeechTokens[1].IsCancellationRequested, "second stays intact until completion");
            body.SpeechCompletions[1].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            dialogue.GetAwaiter().GetResult();
            Check(completed == 2 && rig.World.Host.World.Speech.ActiveCount == 0, "two sequential awaited lines retire without pending work");
            Console.WriteLine("FarHostSequential: passed two controlled native completions, no truncation or refusal.");
        }

        private static void SequentialAtPaceBoundary()
        {
            using var rig = new RobotSpeechRig(0);
            var clock = rig.World.Clock;
            var speech = rig.World.Host.World.Speech;
            var body = rig.Canonical;
            async Task Dialogue()
            {
                var first = await rig.Robot.SayAsync("First line.");
                Check(first.Succeeded && first.Value, "first local playback completes successfully");
                var second = await rig.Robot.SayAsync("Second line.");
                Check(second.Succeeded && second.Value, "the next awaited line completes successfully");
            }

            // Every clock read moves time on, so a pace that read the clock again after admission would end
            // later than the admitted line. The boundary is measured from the start the line recorded.
            clock.ReadIncrement = 1;
            var dialogue = Dialogue();
            clock.ReadIncrement = 0;
            Check(!dialogue.IsCompleted && body.SpeechPlays == 1 && speech.ActiveCount == 1, "the first line begins local playback");
            var started = speech.StartedAt(0);
            body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));

            clock.Now = started + 999_999;
            var early = rig.Robot.SayAsync("Too soon.");
            Check(early.IsCompleted && early.Result.ErrorCode == ModErrorCode.RateLimited && !dialogue.IsCompleted,
                "one microsecond before the boundary, the first line waits and an early call is rate limited");

            clock.Advance(1);
            speech.Tick();
            Check(!dialogue.IsCompleted && body.SpeechPlays == 2 && !body.SpeechTokens[0].IsCancellationRequested,
                "at the one-second boundary, the first line retires and its continuation starts the next line");

            body.SpeechCompletions[1].SetResult(OperationResult<bool>.Success(true));
            clock.Advance(1_000_000);
            speech.Tick();
            Check(dialogue.IsCompleted && speech.ActiveCount == 0, "both sequential lines retire");
            dialogue.GetAwaiter().GetResult();
        }

        private static void NoRecipients()
        {
            using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60); rig.Move(rig.Guests[0], 60);
            var completed = 0;
            async Task Dialogue()
            {
                for (var i = 0; i < 6; i++)
                {
                    var result = await rig.Robot.SayAsync("Nobody nearby.");
                    Check(result.Succeeded && !result.Value, "empty audience returns false after pacing, without rate refusal"); completed++;
                }
            }
            var dialogue = Dialogue();
            for (var i = 0; i < 6; i++)
            {
                Check(completed == i && !dialogue.IsCompleted, "NoRecipients: each empty-audience line must wait its pace");
                rig.Later();
            }
            dialogue.GetAwaiter().GetResult();
            Check(completed == 6 && rig.Canonical.SpeechPlays == 0 && rig.Guests[0].Body.SpeechPlays == 0,
                "empty audience does not synthesize for an unauthorized listener");
            Check(rig.Log.All(p => p.Payload[1] != RobotSpeechWire.Line), "empty audience discloses no text");
            Console.WriteLine("NoRecipients: passed six paced sequential awaits without synthesis or rate refusal.");
        }

        private static void AllListeners()
        {
            using var rig = new RobotSpeechRig(2);
            var call = rig.Robot.SayAsync("Everyone finishes."); rig.World.Frames(4);
            rig.Canonical.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));
            rig.Guests[0].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));
            rig.Later(); Check(!call.IsCompleted, "near host and fast listener cannot finish for slower listener");
            rig.Guests[1].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(call.IsCompleted && call.Result.Succeeded && call.Result.Value, "all admitted listeners have completed");
            using var shortLine = new RobotSpeechRig(0);
            var shortCall = shortLine.Robot.SayAsync("Short.");
            shortLine.Canonical.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); shortLine.World.Frames(3);
            Check(!shortCall.IsCompleted, "even very short local playback observes unchanged minimum pace");
            shortLine.Later(); Check(shortCall.IsCompleted && shortCall.Result.Succeeded, "short playback completes after pace");
        }

        private static void Failures()
        {
            foreach (var loss in new[] { "caller", "cancelled-at-deadline", "world", "provider", "owner", "ownership", "disconnect", "hidden", "failure", "timeout" })
            {
                using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
                using var cancellation = new CancellationTokenSource();
                var call = rig.Robot.SayAsync("Must not report completion.", cancellation.Token); rig.World.Frames(5);
                var body = rig.Guests[0].Body;
                Check(!call.IsCompleted, loss + " begins with pending playback");
                switch (loss)
                {
                    case "caller": cancellation.Cancel(); break;
                    case "cancelled-at-deadline": cancellation.Cancel(); rig.World.Clock.Advance(16_000_000); break;
                    case "world": rig.Host.Scene++; break;
                    case "provider": rig.World.Host.Lifetime.Dispose(); break;
                    case "owner": rig.Owner.Lifetime.BeginStop(); break;
                    case "ownership": rig.World.Host.Runtime.Spawns.Remove(rig.World.Host.Runtime.Spawns.Find(rig.Canonical)!); break;
                    case "disconnect": rig.World.Network.LoseLink(rig.Guests[0].Peer.Control); break;
                    case "hidden": body.Visible = false; break;
                    case "failure": body.SpeechCompletions[0].SetResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Unavailable.")); break;
                    case "timeout": rig.World.Clock.Advance(16_000_000); break;
                }
                rig.Later();
                var expected = loss switch
                {
                    "disconnect" or "hidden" or "failure" => ModErrorCode.Unavailable,
                    "timeout" => ModErrorCode.TimedOut,
                    _ => ModErrorCode.Cancelled,
                };
                Check(call.IsCompleted && call.Result.ErrorCode == expected, loss + " settles with its own failure code");
                Check(rig.World.Host.World.Speech.ActiveCount == 0, loss + " releases retained operation");
            }
            // Silence from a still-connected peer is not an acknowledgement, including a full return window.
            using var stalled = new RobotSpeechRig(); stalled.Move(stalled.Host, 60);
            var pending = stalled.Robot.SayAsync("No completion report."); stalled.World.Frames(4);
            stalled.Guests[0].Peer.Paused = true; stalled.Later();
            Check(!pending.IsCompleted, "a silent connected listener cannot complete a line");
            stalled.World.Clock.Advance(16_000_000); stalled.World.Frames(3);
            Check(pending.IsCompleted && pending.Result.ErrorCode == ModErrorCode.TimedOut,
                "missing completion report expires at the existing deadline");
        }

        private static void ListenerLoss()
        {
            foreach (var local in new[] { true, false })
            {
                using var rig = new RobotSpeechRig(2);
                var call = rig.Robot.SayAsync("One listener leaves earshot.");
                rig.World.Frames(5);
                var departing = local ? rig.Canonical : rig.Guests[0].Body;
                var remaining = local ? rig.Guests[0].Body : rig.Canonical;
                var otherGuest = rig.Guests[1].Body;

                rig.Move(local ? rig.Host : rig.Guests[0], 60);
                rig.Later();
                Check(departing.SpeechTokens.Single().IsCancellationRequested && !call.IsCompleted,
                    "leaving earshot stops the lost copy and the host waits for the other listeners");
                Check(!remaining.SpeechTokens.Single().IsCancellationRequested && !otherGuest.SpeechTokens.Single().IsCancellationRequested,
                    "listener loss leaves the other admitted copies running");
                remaining.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
                otherGuest.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
                rig.World.Frames(5);
                Check(call.IsCompleted && call.Result.Succeeded && call.Result.Value && rig.World.Host.World.Speech.ActiveCount == 0,
                    "a line the remaining listeners heard succeeds although one listener left");
            }
        }

        private static void CompletedListenerDeparture()
        {
            foreach (var disconnect in new[] { false, true })
            {
                using var rig = new RobotSpeechRig(2);
                rig.Move(rig.Host, 60);
                var departed = rig.Guests[0];
                var remaining = rig.Guests[1].Body;
                var call = rig.Robot.SayAsync("A listener leaves after completing playback.");
                rig.World.Frames(5);
                var sender = departed.Peer.Control.Id;
                var line = rig.Log.Last(packet => packet.Payload[1] == RobotSpeechWire.Line && packet.To.Equals(sender)).Payload;
                departed.Body.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
                Check(Deliver(rig, Finished(line, RobotSpeechWire.Completed), sender) && !call.IsCompleted,
                    "the host accepts completion from the eligible guest while the other guest is pending");

                if (disconnect) rig.World.Network.LoseLink(departed.Peer.Control);
                else rig.Move(departed, 60);
                rig.Later();
                Check(!call.IsCompleted && !remaining.SpeechTokens.Single().IsCancellationRequested,
                    "the completed guest's departure leaves the remaining copy pending");

                remaining.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
                rig.World.Frames(5);
                Check(call.IsCompleted && call.Result.Succeeded && call.Result.Value,
                    "a guest leaving after an accepted completion does not undo overall success");
            }
        }

        private static void LocalProviderCancellation()
        {
            using var rig = new RobotSpeechRig(0);
            var call = rig.Robot.SayAsync("The native provider stops on its own.");
            var local = rig.Canonical;
            local.SpeechCompletions.Single().SetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Provider stopped."));
            rig.Later();
            Check(call.IsCompleted && call.Result.ErrorCode == ModErrorCode.Unavailable && !local.SpeechTokens.Single().IsCancellationRequested,
                "provider cancellation alone is a playback failure and does not report caller cancellation");
        }

        private static void ResetContinuation()
        {
            using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
            async Task Dialogue()
            {
                var result = await rig.Robot.SayAsync("World is ending.");
                Check(result.ErrorCode == ModErrorCode.Cancelled, "world reset cancels the old call");
                var retry = await rig.Robot.SayAsync("Reentrant call during reset.");
                Check(retry.ErrorCode == ModErrorCode.Unavailable, "reset continuation cannot readmit old-world work");
            }
            var dialogue = Dialogue(); rig.World.Frames(4); rig.Host.Scene++; rig.World.Frames(3);
            Check(dialogue.IsCompleted, "reset continuation settles without mutating the retiring list");
            dialogue.GetAwaiter().GetResult();
        }

        private static void WorkerCancellation()
        {
            var frameThread = Environment.CurrentManagedThreadId;
            foreach (var ownerStops in new[] { false, true })
            {
                using var rig = new RobotSpeechRig();
                using var caller = new CancellationTokenSource();
                var call = rig.Robot.SayAsync("Cancellation waits for the frame.", caller.Token);
                rig.World.Frames(5);
                var local = rig.Canonical.SpeechTokens.Single();
                var remote = rig.Guests[0].Body.SpeechTokens.Single();
                var localThread = 0;
                var remoteThread = 0;
                using var hostProbe = local.Register(() => localThread = Environment.CurrentManagedThreadId);
                using var guestProbe = remote.Register(() => remoteThread = Environment.CurrentManagedThreadId);
                var sent = rig.Log.Count;
                Task.Factory.StartNew(() =>
                {
                    Check(Environment.CurrentManagedThreadId != frameThread, "cancellation starts on a worker");
                    if (ownerStops) rig.Owner.Lifetime.BeginStop();
                    else caller.Cancel();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
                Check(!local.IsCancellationRequested && !remote.IsCancellationRequested && localThread == 0 && remoteThread == 0
                    && !call.IsCompleted && rig.Log.Count == sent, "worker cancellation touches neither playback nor transport before a frame");
                rig.World.Frames(6);
                Check(localThread == frameThread && remoteThread == frameThread && call.IsCompleted && call.Result.ErrorCode == ModErrorCode.Cancelled,
                    "frame maintenance cancels both copies and preserves the caller's cancellation result");
            }
        }

        private static void FrameDeadlines()
        {
            using var rig = new RobotSpeechRig();
            var guest = rig.Guests[0];
            var clock = rig.World.Clock;
            var hostStarted = clock.Now;
            using var caller = new CancellationTokenSource();
            var call = rig.Robot.SayAsync("Neither copy can hang forever.", caller.Token);
            long receivedAt = 0;
            for (var frame = 0; frame < 10 && guest.Body.SpeechPlays == 0; frame++)
            {
                receivedAt = clock.Now;
                rig.World.Frame();
            }
            Check(guest.Body.SpeechPlays == 1, "the receiver began real managed playback");
            var frameThread = Environment.CurrentManagedThreadId;
            var localThread = 0;
            var remoteThread = 0;
            using var hostProbe = rig.Canonical.SpeechTokens.Single().Register(() =>
            {
                localThread = Environment.CurrentManagedThreadId;
                caller.Cancel();
            });
            using var guestProbe = guest.Body.SpeechTokens.Single().Register(() => remoteThread = Environment.CurrentManagedThreadId);
            var host = rig.World.Host.World.Speech;
            var receiver = guest.Peer.World.Speech;
            clock.Now = hostStarted + 14_999_999;
            host.Tick(); receiver.Tick();
            Check(localThread == 0 && remoteThread == 0 && !call.IsCompleted, "neither copy expires before its deadline");
            clock.Advance(1);
            host.Tick();
            Check(localThread == frameThread && remoteThread == 0 && call.IsCompleted && call.Result.ErrorCode == ModErrorCode.TimedOut,
                "host expires exactly on its frame deadline even if native cancellation also requests caller cancellation");
            // No transport pump or flush here: the receiver must enforce its own deadline without a host cancel packet.
            if (receivedAt > hostStarted)
            {
                clock.Now = receivedAt + 14_999_999;
                receiver.Tick();
                Check(remoteThread == 0, "the later receiver remains pending one microsecond before its own deadline");
            }
            clock.Now = receivedAt + 15_000_000;
            receiver.Tick();
            Check(remoteThread == frameThread && receiver.ActiveCount == 0
                && guest.Channel.Log!.Any(item => item.Payload[1] == RobotSpeechWire.Finished && item.Payload.Last() == 0),
                "receiver expires and reports failure on its own frame deadline");
        }

        private static byte[] Finished(byte[] line, byte status = 1, int changed = 0)
        {
            var reader = new WorldWireReader(); reader.Reset(line); reader.TryReadHeader(out _); reader.TryReadByte(out _);
            reader.TryReadUInt64(out var world); reader.TryReadString(32, out var challenge);
            reader.TryReadUInt64(out var nonce); reader.TryReadVarUInt(uint.MaxValue, out var entity);
            var writer = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
            RobotSpeechWire.Header(writer, RobotSpeechWire.Finished, changed == 1 ? world + 1 : world,
                changed == 2 ? new string('f', 32) : challenge, changed == 3 ? nonce + 1 : nonce, changed == 4 ? (uint)entity + 1 : (uint)entity);
            writer.WriteByte(status); return writer.Segment.ToArray();
        }
        private static bool Deliver(RobotSpeechRig rig, byte[] bytes, ParticipantId sender)
        {
            var reader = new WorldWireReader(); reader.Reset(bytes);
            return reader.TryReadHeader(out var type) && rig.World.Host.World.Speech.Handle(type, sender, reader);
        }
        private static void AcknowledgementValidation()
        {
            using var rig = new RobotSpeechRig(2); rig.Move(rig.Host, 60); rig.Move(rig.Guests[1], 60);
            var call = rig.Robot.SayAsync("Bound to this playback."); rig.World.Frames(4);
            var ack = Finished(rig.LastLine); var sender = rig.Guests[0].Peer.Control.Id;
            Check(!Deliver(rig, ack, rig.Guests[1].Peer.Control.Id) && !Deliver(rig, ack, rig.World.Host.Control.Id),
                "only an admitted remote sender can report its own completion");
            for (var i = 1; i <= 4; i++) Check(!Deliver(rig, Finished(rig.LastLine, changed: i), sender), "world/challenge/nonce/entity must match exactly");
            for (var size = 0; size < ack.Length; size++) Check(!Deliver(rig, ack.Take(size).ToArray(), sender), "truncated acknowledgements fail closed");
            Check(!Deliver(rig, Finished(rig.LastLine, 3), sender) && !Deliver(rig, ack.Concat(new byte[] { 0 }).ToArray(), sender),
                "status and exact framing are validated");
            var oldVersion = (byte[])ack.Clone(); oldVersion[2] = 1;
            Check(!Deliver(rig, oldVersion, sender), "older schemas cannot certify current completion");
            rig.Later(); Check(!call.IsCompleted, "invalid acknowledgements cannot finish playback");
            rig.Guests[0].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(call.IsCompleted && call.Result.Succeeded && !Deliver(rig, ack, sender), "real completion accepted once, replay rejected");
            var next = rig.Robot.SayAsync("Different line."); rig.World.Frames(4);
            Check(!Deliver(rig, ack, sender) && !next.IsCompleted, "old completion cannot finish a later line");
            var priorStream = Finished(rig.LastLine);
            rig.World.Network.LoseLink(rig.Guests[0].Peer.Control); rig.World.Frames(4);
            rig.World.Network.Restore(rig.Guests[0].Peer.Control); rig.World.Frames(40);
            Check(!Deliver(rig, priorStream, sender), "retired connection completion cannot replay after reconnect");
            rig.Host.Scene++; rig.World.Frames(3);
            Check(!Deliver(rig, priorStream, sender), "old world completion cannot replay after reset");
        }
    }
}
