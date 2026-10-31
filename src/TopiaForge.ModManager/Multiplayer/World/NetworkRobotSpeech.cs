using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // Ephemeral private events, deliberately absent from spawn descriptors and late-join baselines.
    internal sealed partial class NetworkRobotSpeech : IWorldMessageHandler, IMultiplayerFrameStage
    {
        private readonly NetworkWorld world;
        private readonly MultiplayerReplication services;
        private readonly Func<long> clock;
        private readonly WorldWireWriter writer = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
        private readonly Dictionary<ParticipantId, ReadyPeer> ready = new Dictionary<ParticipantId, ReadyPeer>();
        private readonly Dictionary<ParticipantId, HandshakePace> handshakePaces = new Dictionary<ParticipantId, HandshakePace>();
        private readonly Dictionary<IEntity, SpeechPace> speechPaces = new Dictionary<IEntity, SpeechPace>();
        private readonly List<IEntity> expiredPaces = new List<IEntity>();
        private readonly List<Playback> active = new List<Playback>();
        private readonly List<ParticipantId> removed = new List<ParticipantId>();
        private readonly TokenBucket outgoing, incoming;
        private ulong nonce, seenNonce, generation, instance;
        private string challenge = "";
        private bool readySent, resetting;
        private long nextReady;
        private NetRealm realm;
        private string? runningWorld;
        private int sceneVersion;
        internal int ActiveCount => active.Count;

        internal NetworkRobotSpeech(NetworkWorld world, MultiplayerReplication services, Func<long> clock)
        {
            this.world = world; this.services = services; this.clock = clock;
            outgoing = new TokenBucket(4, 2, clock()); incoming = new TokenBucket(8, 4, clock());
            world.Channel.Register(RobotSpeechWire.Ready, this);
            world.Channel.Register(RobotSpeechWire.Line, this);
            world.Channel.Register(RobotSpeechWire.Cancel, this);
            world.Channel.Register(RobotSpeechWire.Withdraw, this);
            world.Channel.Register(RobotSpeechWire.Finished, this);
        }
        public void Run(float deltaTime) => Tick();

        internal Task<OperationResult<bool>> SayAsync(RuntimeReplication context, IEntity entity, string kind,
            string text, Func<bool> owns, CancellationToken token)
        {
            if (resetting) return Unavailable();
            Tick();
            if (token.IsCancellationRequested) return Result(ModErrorCode.Cancelled, "Robot speech was cancelled.");
            if (!RobotSpeechWire.Text(text)) return Result(ModErrorCode.InvalidArgument, "Robot speech needs 1 to 512 plain-text characters.");
            if (speechPaces.ContainsKey(entity)) return Result(ModErrorCode.RateLimited, "This robot spoke too recently.");
            if (!services.TryGetReplicaFactory(kind, out var factory) || factory is not IInternalSpeechProvider provider
                || !provider.IsSpeechVisible(entity)) return Unavailable();
            var identity = provider.VoiceIdentity(entity);
            if (!identity.TryGetValue(out var voice) || !RobotSpeechWire.Hex(voice, 64)) return Unavailable();
            uint id = 0;
            var shared = MultiplayerSpawnLedger.Records(context.Profile) && realm == NetRealm.ListenHost;
            if (shared && (!world.Host.SpeechTarget(entity, context.OwnerModId, kind, out id) || instance == 0)) return Unavailable();
            if (active.Count >= 32 || !outgoing.TryTake(clock())) return Result(ModErrorCode.RateLimited, "Robot speech is busy; retry a fresh line shortly.");
            foreach (var previous in active)
                if (ReferenceEquals(previous.Entity, entity))
                {
                    previous.Cancel();
                }
            bool Current() => owns() && services.TryGetReplicaFactory(kind, out var registered) && ReferenceEquals(registered, provider);
            var operation = new Playback(entity, provider, ++nonce, clock(), CancellationTokenSource.CreateLinkedTokenSource(token, context.Lifetime.StoppingToken), Current) { WireId = id };
            active.Add(operation);
            speechPaces[entity] = new SpeechPace(clock(), Current);
            var presence = services.Players?.Presence;
            if (shared && presence != null)
            {
                foreach (var peer in presence.Peers)
                {
                    if (peer.IsLocal || !Eligible(peer.Id, entity) || !ready.TryGetValue(peer.Id, out var gate)
                        || gate.World != instance || gate.Generation != world.Channel.GetGeneration(peer.Id)
                        || clock() - gate.ReceivedAt > 2_000_000) continue;
                    RobotSpeechWire.Header(writer, RobotSpeechWire.Line, instance, gate.Challenge, operation.Nonce, id);
                    writer.WriteUInt64(gate.Stamp);
                    writer.WriteString(context.OwnerModId); writer.WriteString(kind); writer.WriteString(voice); writer.WriteString(text);
                    if (!world.Channel.TrySendTo(peer.Id, writer, writer.Length)) continue;
                    operation.Recipients.Add(new Recipient(peer.Id, gate, id));
                }
            }
            if (LocalEligible(entity))
            {
                operation.LocalCancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token);
                operation.Task = SafePlay(provider, entity, voice, text, operation.LocalCancellation.Token);
            }
            return operation.Completion.Task;
        }

        private bool Eligible(ParticipantId participant, IEntity entity)
        {
            var presence = services.Players?.Presence;
            if (presence == null || runningWorld == null || !presence.Host.IsCurrentWorld
                || !presence.TryGetPeer(participant, out var peer) || peer == null || !peer.IsConnected || peer.Removed
                || (!peer.IsLocal && peer.PlacedWorld != runningWorld)) return false;
            var position = presence.ValidatedPosition(participant);
            return position.HasValue && Near(entity.Position, new Vec3(position.Value.X, position.Value.Y, position.Value.Z));
        }

        private bool LocalEligible(IEntity entity)
        {
            var presence = services.Players?.Presence;
            if (presence == null) return false;
            if (realm == NetRealm.ListenHost)
                return presence.LocalParticipant is ParticipantId local && Eligible(local, entity);
            return (realm == NetRealm.Offline || runningWorld != null) && presence.Backend.TryReadLocalPlayer(out var sample)
                && Near(entity.Position, new Vec3(sample.Feet.X, sample.Feet.Y, sample.Feet.Z));
        }
        internal static bool Near(Vec3 a, Vec3 b)
        {
            if (!a.IsFinite || !b.IsFinite) return false;
            var x = (double)a.X - b.X; var y = (double)a.Y - b.Y; var z = (double)a.Z - b.Z;
            return x * x + y * y + z * z <= RobotSpeechWire.Earshot * RobotSpeechWire.Earshot;
        }

        private static async Task<OperationResult<bool>> SafePlay(IInternalSpeechProvider provider, IEntity entity, string voice, string text, CancellationToken token)
        {
            try { return await provider.PlaySpeechAsync(entity, voice, text, token); }
            catch (OperationCanceledException) { return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Robot speech was cancelled."); }
            catch (Exception) { return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Native robot speech is unavailable; retry after the provider recovers."); }
        }
        private static Task<OperationResult<bool>> Unavailable() => Result(ModErrorCode.Unavailable,
            "Robot speech needs a visible current-world body and matching native voice bindings; retry after recovery.");
        private static Task<OperationResult<bool>> Result(ModErrorCode code, string message) => Task.FromResult(OperationResult<bool>.Failure(code, message));

        private sealed class ReadyPeer
        {
            internal readonly ulong Generation, World;
            internal readonly string Challenge;
            internal readonly ulong Stamp;
            internal readonly long ReceivedAt;
            internal ReadyPeer(ulong generation, ulong world, string challenge, ulong stamp, long receivedAt)
            { Generation = generation; World = world; Challenge = challenge; Stamp = stamp; ReceivedAt = receivedAt; }
        }
        private sealed class SpeechPace
        {
            internal readonly long Started;
            internal readonly Func<bool> Current;
            internal SpeechPace(long started, Func<bool> current) { Started = started; Current = current; }
        }
        private sealed class HandshakePace
        {
            internal readonly ulong Generation;
            internal readonly TokenBucket Budget;
            internal HandshakePace(ulong generation, long now) { Generation = generation; Budget = new TokenBucket(4, 2, now); }
        }
        private sealed class Recipient
        {
            internal readonly ParticipantId Id;
            internal readonly ReadyPeer Gate;
            internal readonly uint Entity;
            internal bool Completed;
            internal Recipient(ParticipantId id, ReadyPeer gate, uint entity) { Id = id; Gate = gate; Entity = entity; }
        }
        private sealed class Playback
        {
            internal readonly IEntity Entity;
            internal readonly IInternalSpeechProvider Provider;
            internal readonly ulong Nonce;
            internal readonly long Started;
            internal readonly CancellationTokenSource Cancellation;
            internal readonly Func<bool> Current;
            internal readonly List<Recipient> Recipients = new List<Recipient>();
            internal readonly TaskCompletionSource<OperationResult<bool>> Completion = new TaskCompletionSource<OperationResult<bool>>();
            internal Task<OperationResult<bool>>? Task;
            internal OperationResult<bool>? Failure;
            internal CancellationTokenSource? LocalCancellation;
            internal uint WireId;
            internal Playback(IEntity entity, IInternalSpeechProvider provider, ulong nonce, long started, CancellationTokenSource cancellation, Func<bool> current)
            {
                Entity = entity; Provider = provider; Nonce = nonce; Started = started; Cancellation = cancellation; Current = current;
                cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            }
            internal void Cancel() { try { Cancellation.Cancel(); } catch (Exception) { } }
        }
    }
}
