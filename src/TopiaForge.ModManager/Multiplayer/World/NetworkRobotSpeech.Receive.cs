using System;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkRobotSpeech
    {
        internal void Reset()
        {
            if (realm == NetRealm.Client && readySent)
            {
                RobotSpeechWire.Header(writer, RobotSpeechWire.Withdraw, instance, challenge, 0, 0);
                world.Channel.TrySendToHost(writer);
            }
            foreach (var operation in active)
            {
                operation.Cancel();
                foreach (var recipient in operation.Recipients) SendCancel(operation, recipient);
                operation.LocalCancellation?.Dispose(); operation.Cancellation.Dispose();
            }
            active.Clear(); ready.Clear(); handshakePaces.Clear(); speechPaces.Clear(); expiredPaces.Clear(); removed.Clear();
            challenge = ""; readySent = false; seenNonce = 0; nextReady = 0;
        }

        internal void Tick()
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
            if (realm == NetRealm.Client && instance != 0 && runningWorld != null && clock() >= nextReady)
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
                if (!valid || clock() - operation.Started >= 15_000_000
                    || (realm == NetRealm.Client && (!LocalEligible(operation.Entity) || operation.Task?.IsCompleted == true))) operation.Cancel();
                for (var p = operation.Recipients.Count - 1; p >= 0; p--)
                {
                    var recipient = operation.Recipients[p];
                    if (!operation.Cancellation.IsCancellationRequested && Eligible(recipient.Id, operation.Entity)
                        && recipient.Gate.Generation == world.Channel.GetGeneration(recipient.Id)
                        && ready.TryGetValue(recipient.Id, out var currentGate) && currentGate.Challenge == recipient.Gate.Challenge) continue;
                    // Cancellation carries no speech text; a blocked stream still expires locally after 15 seconds.
                    SendCancel(operation, recipient);
                    operation.Recipients.RemoveAt(p);
                }
                if (operation.Task != null && !operation.Task.IsCompleted && !LocalEligible(operation.Entity))
                {
                    try { operation.LocalCancellation?.Cancel(); } catch (Exception) { }
                }
                if (!operation.Cancellation.IsCancellationRequested) continue;
                active.RemoveAt(i); operation.LocalCancellation?.Dispose(); operation.Cancellation.Dispose();
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
            if (reader.Remaining > RobotSpeechWire.MaximumBytes - 2 || !reader.TryReadByte(out var version) || version != 1
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
                foreach (var operation in active) if (operation.Nonce == lineNonce && operation.WireId == entityId) operation.Cancel();
                return true;
            }
            if (type != RobotSpeechWire.Line || lineNonce <= seenNonce) return false;
            seenNonce = lineNonce;
            // The receiver's own monotonic clock was echoed, so no clock synchronization or queue-age guess is needed.
            if ((long)stamp > clock() || clock() - (long)stamp > 2_000_000) return false;
            if (!incoming.TryTake(clock()) || active.Count >= 32) return false;
            if (!world.Client.SpeechTarget((uint)entityId, mod, kind, out var entity) || entity == null || !LocalEligible(entity)
                || !services.TryGetReplicaFactory(kind, out var factory) || factory is not IInternalSpeechProvider provider
                || !provider.IsSpeechVisible(entity)) return false;
            foreach (var previous in active) if (ReferenceEquals(previous.Entity, entity)) previous.Cancel();
            bool Current() => world.Client.SpeechTarget((uint)entityId, mod, kind, out var current) && ReferenceEquals(current, entity)
                && services.TryGetReplicaFactory(kind, out var registered) && ReferenceEquals(registered, provider);
            var playback = new Playback(entity, provider, lineNonce, clock(), new CancellationTokenSource(), Current) { WireId = (uint)entityId };
            active.Add(playback);
            playback.Task = SafePlay(provider, entity, voice, text, playback.Cancellation.Token);
            return true;
        }
    }
}
