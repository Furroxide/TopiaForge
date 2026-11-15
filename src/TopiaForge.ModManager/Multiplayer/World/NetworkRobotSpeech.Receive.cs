using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkRobotSpeech
    {
        internal void Reset()
        {
            if (resetting) return;
            resetting = true;
            try
            {
                if (realm == NetRealm.Client && readySent)
                {
                    RobotSpeechWire.Header(writer, RobotSpeechWire.Withdraw, instance, challenge, 0, 0);
                    world.Channel.TrySendToHost(writer);
                }
                var retired = active.ToArray();
                active.Clear();
                foreach (var operation in retired)
                {
                    operation.RequestCancellation();
                    operation.CancelLocal();
                    foreach (var recipient in operation.Recipients) SendCancel(operation, recipient);
                    operation.DisposeLocal();
                }
                ready.Clear(); handshakePaces.Clear(); speechPaces.Clear(); expiredPaces.Clear(); removed.Clear();
                challenge = ""; readySent = false; seenNonce = 0; nextReady = 0;
                foreach (var operation in retired)
                    operation.Completion.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Robot speech was cancelled."));
            }
            finally { resetting = false; }
        }

        internal void Tick()
        {
            if (ticking || resetting) return;
            ticking = true;
            try { TickCore(); }
            finally { ticking = false; }
        }

        private void TickCore()
        {
            var nextRealm = services.Runtime.Realm;
            var presence = services.Players?.Presence;
            var nextWorld = presence?.RunningWorld?.Invoke();
            var nextScene = presence?.SceneVersion?.Invoke() ?? 0;
            var nextInstance = nextRealm == NetRealm.Client ? world.Client.SpeechWorld : world.Host.WorldInstance;
            var nextGeneration = world.Channel.Generation;
            if (realm != nextRealm || instance != nextInstance || runningWorld != nextWorld || sceneVersion != nextScene
                || (nextRealm == NetRealm.Client && generation != nextGeneration)) Reset();
            realm = nextRealm; instance = nextInstance; runningWorld = nextWorld; sceneVersion = nextScene; generation = nextGeneration;
            expiredPaces.Clear();
            foreach (var pair in speechPaces)
                if (clock() - pair.Value.Started >= 1_000_000 || !pair.Value.Current()) expiredPaces.Add(pair.Key);
            foreach (var entity in expiredPaces) speechPaces.Remove(entity);
            expiredPaces.Clear();
            // A runtime with no speech provider has nothing to admit and must keep its idle frame allocation-free.
            if (realm == NetRealm.Client && instance != 0 && runningWorld != null && services.HasSpeechProvider && clock() >= nextReady)
            {
                if (challenge.Length == 0) challenge = Guid.NewGuid().ToString("N");
                RobotSpeechWire.Header(writer, RobotSpeechWire.Ready, instance, challenge, (ulong)clock(), 0);
                writer.WriteString(runningWorld);
                if (world.Channel.TrySendToHost(writer)) { readySent = true; nextReady = clock() + 1_000_000; }
                else nextReady = clock() + 250_000;
            }
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var operation = active[i];
                var valid = operation.Current() && operation.Entity.IsAlive && operation.Provider.IsSpeechVisible(operation.Entity);
                if (!valid || clock() - operation.Started >= DeadlineMicros) operation.RequestCancellation();
                if (!operation.CancellationRequested && operation.Task != null && !operation.Task.IsCompleted && !LocalEligible(operation.Entity))
                {
                    if (realm == NetRealm.Client) operation.RequestCancellation();
                    else operation.CancelLocal();
                }
                if (i >= active.Count || !ReferenceEquals(active[i], operation)) { i = active.Count; continue; }
                var cancelled = operation.CancellationRequested;
                if (cancelled)
                {
                    active.RemoveAt(i);
                    operation.CancelLocal();
                }
                for (var p = operation.Recipients.Count - 1; p >= 0; p--)
                {
                    var recipient = operation.Recipients[p];
                    if (!cancelled && Eligible(recipient.Id, operation.Entity)
                        && recipient.Gate.Generation == world.Channel.GetGeneration(recipient.Id)
                        && ready.TryGetValue(recipient.Id, out var currentGate) && currentGate.Challenge == recipient.Gate.Challenge) continue;
                    // An admitted listener that departs cannot certify completion. Stop its copy, but let
                    // the remaining listeners finish. The caller receives failure, never partial success.
                    SendCancel(operation, recipient);
                    operation.Failure = OperationResult<bool>.Failure(ModErrorCode.Cancelled, "A robot speech listener became unavailable.");
                    operation.Recipients.RemoveAt(p);
                }
                var localDone = operation.Task == null || operation.Task.IsCompleted;
                if (operation.Task?.IsCompleted == true)
                {
                    var localResult = operation.Task.Result;
                    if (!localResult.Succeeded) operation.Failure = localResult;
                    else if (!localResult.Value) operation.Failure = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
                        "Native robot speech did not complete playback.");
                }
                var remoteDone = true;
                foreach (var recipient in operation.Recipients) if (!recipient.Completed) remoteDone = false;
                // A request racing this frame's poll stays pending for the next frame to cancel every copy.
                if (!cancelled && operation.CancellationRequested) continue;
                if (realm == NetRealm.Client)
                {
                    if (!cancelled && !localDone) continue;
                    // Only settled native playback can report success. Cancellation reports failure on this frame.
                    SendFinished(operation, !cancelled && operation.Task?.Result.Succeeded == true && operation.Task.Result.Value);
                }
                else if (!cancelled && (!localDone || !remoteDone || clock() - operation.Started < 1_000_000)) continue;
                if (!cancelled) active.RemoveAt(i);
                operation.DisposeLocal();
                var result = cancelled ? OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Robot speech was cancelled.")
                    : operation.Failure ?? OperationResult<bool>.Success(operation.Task != null || operation.Recipients.Count != 0);
                // Retire before waking a sequential caller, which may start another line synchronously.
                operation.Completion.TrySetResult(result);
                i = active.Count;
            }
            if (handshakePaces.Count != 0)
            {
                // Bounded by the transport's participant cap. No message content is retained here.
                removed.Clear();
                foreach (var pair in handshakePaces)
                    if (pair.Value.Generation != world.Channel.GetGeneration(pair.Key)) removed.Add(pair.Key);
                foreach (var id in removed) { ready.Remove(id); handshakePaces.Remove(id); }
                removed.Clear();
            }
        }

        private void SendCancel(Playback operation, Recipient recipient)
        {
            if (recipient.Gate.Generation != world.Channel.GetGeneration(recipient.Id)) return;
            RobotSpeechWire.Header(writer, RobotSpeechWire.Cancel, recipient.Gate.World, recipient.Gate.Challenge, operation.Nonce, recipient.Entity);
            world.Channel.TrySendTo(recipient.Id, writer, writer.Length);
        }

        public bool Handle(byte type, ParticipantId sender, WorldWireReader reader)
        {
            if (reader.Remaining > RobotSpeechWire.MaximumBytes - 2 || !reader.TryReadByte(out var version) || version != RobotSpeechWire.Version
                || !reader.TryReadUInt64(out var messageWorld) || messageWorld == 0
                || !reader.TryReadString(32, out var messageChallenge) || !RobotSpeechWire.Hex(messageChallenge, 32)
                || !reader.TryReadUInt64(out var lineNonce) || !reader.TryReadVarUInt(uint.MaxValue, out var entityId)) return false;
            if (type == RobotSpeechWire.Ready || type == RobotSpeechWire.Withdraw)
            {
                var currentWorld = "";
                if (type == RobotSpeechWire.Ready && (!reader.TryReadString(128, out currentWorld) || currentWorld.Length == 0)) return false;
                if (!reader.AtEnd || (type == RobotSpeechWire.Withdraw ? lineNonce != 0 : lineNonce == 0 || lineNonce > long.MaxValue)
                    || entityId != 0 || realm != NetRealm.ListenHost || messageWorld != instance
                    || services.Runtime.Control?.SessionSnapshot.TryGetParticipant(sender, out var peer) != true || peer == null || peer.IsLocal
                    || !peer.IsConnected || handshakePaces.Count >= 256 && !handshakePaces.ContainsKey(sender)) return false;
                var stream = world.Channel.GetGeneration(sender);
                if (stream == 0) return false;
                if (!handshakePaces.TryGetValue(sender, out var pace) || pace.Generation != stream)
                    handshakePaces[sender] = pace = new HandshakePace(stream, clock());
                if (!pace.Budget.TryTake(clock())) return false;
                if (type == RobotSpeechWire.Withdraw)
                {
                    if (ready.TryGetValue(sender, out var old) && old.Generation == stream && old.Challenge == messageChallenge) ready.Remove(sender);
                    return true;
                }
                if (currentWorld != runningWorld) return false;
                // A fresh challenge can recover a dropped withdrawal; no prior text is retained for it.
                if (ready.TryGetValue(sender, out var known) && known.Generation == stream
                    && (lineNonce <= known.Stamp || clock() - known.ReceivedAt < 200_000)) return false;
                ready[sender] = new ReadyPeer(stream, instance, messageChallenge, lineNonce, clock());
                return true;
            }
            if (type == RobotSpeechWire.Finished)
                return ReceiveFinished(sender, reader, messageWorld, messageChallenge, lineNonce, (uint)entityId);
            var mod = ""; var kind = ""; var voice = ""; var text = "";
            ulong stamp = 0;
            if (type == RobotSpeechWire.Line && (!reader.TryReadUInt64(out stamp) || stamp > long.MaxValue
                || !reader.TryReadString(128, out mod) || !InternalReplicationLimits.IsText(mod, 128)
                || !reader.TryReadString(128, out kind) || !InternalReplicationLimits.IsText(kind, 128)
                || !reader.TryReadString(64, out voice) || !RobotSpeechWire.Hex(voice, 64)
                || !reader.TryReadString(2048, out text) || !RobotSpeechWire.Text(text))) return false;
            if (!reader.AtEnd || entityId == 0 || lineNonce == 0 || realm != NetRealm.Client || messageWorld != instance
                || messageChallenge != challenge || !readySent || generation != world.Channel.Generation
                || !(services.Runtime.Control?.SessionSnapshot.HostParticipantId ?? InternalSystemMessage.HeadlessHostSender).Equals(sender)) return false;
            if (type == RobotSpeechWire.Cancel)
            {
                foreach (var operation in active) if (operation.Nonce == lineNonce && operation.WireId == entityId) operation.RequestCancellation();
                return true;
            }
            if (type != RobotSpeechWire.Line || lineNonce <= seenNonce || (long)stamp > clock()) return false;
            seenNonce = lineNonce;
            // Only a well-formed, authenticated, fresh-nonce line may receive a reason-free refusal.
            // The receiver's own monotonic clock was echoed, so no clock synchronization is needed.
            if (clock() - (long)stamp > 2_000_000) return Decline(lineNonce, (uint)entityId);
            if (!incoming.TryTake(clock()) || active.Count >= 32) return Decline(lineNonce, (uint)entityId);
            if (!world.Client.SpeechTarget((uint)entityId, mod, kind, out var entity) || entity == null || !LocalEligible(entity)
                || !services.TryGetReplicaFactory(kind, out var factory) || factory is not IInternalSpeechProvider provider
                || !provider.IsSpeechVisible(entity)) return Decline(lineNonce, (uint)entityId);
            foreach (var previous in active) if (ReferenceEquals(previous.Entity, entity)) previous.RequestCancellation();
            bool Current() => world.Client.SpeechTarget((uint)entityId, mod, kind, out var current) && ReferenceEquals(current, entity)
                && services.TryGetReplicaFactory(kind, out var registered) && ReferenceEquals(registered, provider);
            var playback = new Playback(entity, provider, lineNonce, clock(), Current) { WireId = (uint)entityId };
            active.Add(playback);
            playback.StartLocal(voice, text);
            return true;
        }
    }
}
