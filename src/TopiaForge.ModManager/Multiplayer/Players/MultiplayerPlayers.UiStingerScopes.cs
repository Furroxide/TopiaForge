using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private ConditionalWeakTable<IModLifetime, PublishedStingerScope> stingerScopes =
            new ConditionalWeakTable<IModLifetime, PublishedStingerScope>();
        private readonly List<PublishedStingerScope> publishedStingerScopes = new List<PublishedStingerScope>();
        private MultiplayerSessionId stingerSession;
        private ulong lastStingerScopeToken;

        private PublishedStingerScope? FindOrReserveStingerScope(IModLifetime lifetime, UiWireEventEnvelope envelope)
        {
            SynchronizeStingerSession();
            if (lifetime.IsStopping || !stingerSession.IsValid) return null;
            if (stingerScopes.TryGetValue(lifetime, out var scope))
                return scope.Owner == envelope.OwnerPackageId && scope.Digest.Span.SequenceEqual(envelope.PackageDigest.Span)
                    ? scope : null;
            if (lastStingerScopeToken == ulong.MaxValue) return null;

            scope = new PublishedStingerScope(lifetime, stingerSession, envelope, ++lastStingerScopeToken);
            stingerScopes.Add(lifetime, scope);
            return scope;
        }

        private OperationResult<bool> DeliverStinger(PublishedStingerScope scope, ParticipantId peer, UiStingerRequest request,
            double exitSeconds, ArraySegment<byte> payload)
        {
            var channel = Channel;
            var generation = channel?.GetGeneration(peer) ?? 0;
            if (scope.IsStopping || generation == 0 || !scope.Session.Equals(Snapshot?.Id ?? default))
                return PlayerDirectory.Unavailable<bool>();
            var now = Now / 1_000_000d;
            var current = scope.Recipient(peer, generation);
            if (current != null)
            {
                current.Advance(now);
                var decision = current.Queue.Classify(request);
                if (decision == UiStingerQueueResult.Coalesced) return UiPresentationTheme.Allowed;
                if (decision == UiStingerQueueResult.RateLimited) return StingerQueueFull;
            }

            var sent = Presentation!.SendEvent(peer, payload);
            if (!sent.Succeeded) return sent;
            if (current == null)
            {
                current = new PlayerStingerTime(peer, generation);
                scope.Recipients.Add(current);
            }
            if (!scope.Published)
            {
                publishedStingerScopes.Add(scope);
                scope.Published = true;
            }

            // Timing is only local admission. Success marks this recipient for eventual closure even after expiry.
            current.Queue.EnqueueRetained(request, exitSeconds, now);
            return sent;
        }

        private void ReconcileStingerScopes(IInternalSystemChannel channel)
        {
            if (!IsHost || publishedStingerScopes.Count == 0) return;
            SynchronizeStingerSession();
            for (var index = publishedStingerScopes.Count - 1; index >= 0; index--)
            {
                var scope = publishedStingerScopes[index];
                var retiring = scope.IsStopping;
                if (retiring) scope.DetachStoppedAuthor();

                for (var peerIndex = scope.Recipients.Count - 1; peerIndex >= 0; peerIndex--)
                {
                    var recipient = scope.Recipients[peerIndex];
                    if (channel.GetGeneration(recipient.Peer) != recipient.Stream ||
                        (retiring && SendStingerRetirement(channel, scope, recipient.Peer)))
                        scope.Recipients.RemoveAt(peerIndex);
                }

                if (scope.Recipients.Count != 0) continue;
                scope.Published = false;
                publishedStingerScopes.RemoveAt(index);
            }
        }

        private bool SendStingerRetirement(IInternalSystemChannel channel, PublishedStingerScope scope, ParticipantId peer)
        {
            Begin(UiStingerDelivery.RetireKind, string.Empty);
            Writer.WriteString(peer.Value);
            UiWireCodec.WriteStingerRetirement(Writer, scope.Owner, scope.Digest.Span, scope.Token);
            var target = InternalSystemTarget.Only(peer);
            // Once this ordered send succeeds its receipt is removed. A full window keeps the closure on this stream.
            return channel.CanSend(target, Writer.Length) &&
                channel.Send(Writer.Segment, target).TryGetValue(out var recipients) && recipients == 1;
        }

        private void SynchronizeStingerSession()
        {
            var current = Snapshot?.Id ?? default;
            if (stingerSession.Equals(current)) return;
            ResetStingerSends();
            stingerSession = current;
        }

        private void ResetStingerSends()
        {
            publishedStingerScopes.Clear();
            stingerScopes = new ConditionalWeakTable<IModLifetime, PublishedStingerScope>();
            stingerSession = default;
        }

        private sealed class PublishedStingerScope
        {
            private IModLifetime? lifetime;
            internal readonly MultiplayerSessionId Session;
            internal readonly string Owner;
            internal readonly ReadOnlyMemory<byte> Digest;
            internal readonly ulong Token;
            internal readonly List<PlayerStingerTime> Recipients = new List<PlayerStingerTime>();
            internal bool Published;
            internal bool IsStopping => lifetime?.IsStopping != false;

            internal PublishedStingerScope(IModLifetime lifetime, MultiplayerSessionId session, UiWireEventEnvelope envelope, ulong token)
            {
                this.lifetime = lifetime;
                Session = session;
                Owner = envelope.OwnerPackageId;
                Digest = envelope.PackageDigest;
                Token = token;
            }

            internal PlayerStingerTime? Recipient(ParticipantId peer, ulong stream)
            {
                for (var index = 0; index < Recipients.Count; index++)
                {
                    var recipient = Recipients[index];
                    if (!recipient.Peer.Equals(peer)) continue;
                    if (recipient.Stream == stream) return recipient;
                    Recipients.RemoveAt(index);
                    break;
                }
                return null;
            }

            internal void DetachStoppedAuthor()
            {
                if (lifetime == null) return;
                lifetime = null;
                foreach (var recipient in Recipients)
                    while (recipient.Queue.TryDrain(out _)) { }
            }
        }

        private sealed class PlayerStingerTime
        {
            internal readonly ParticipantId Peer;
            internal readonly ulong Stream;
            internal readonly UiStingerQueue<double> Queue = new UiStingerQueue<double>();

            internal PlayerStingerTime(ParticipantId peer, ulong stream)
            {
                Peer = peer;
                Stream = stream;
            }

            internal void Advance(double now)
            {
                while (Queue.Active is { } active)
                {
                    var expires = Queue.ShownAt + active.Request.DurationSeconds + active.Presentation;
                    if (now < expires || !Queue.Advance(expires, out _)) return;
                }
            }
        }
    }
}
