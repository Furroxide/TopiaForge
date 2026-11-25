using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Host-committed, read-only presentation on the authenticated Players channel.</summary>
    internal sealed partial class MultiplayerPresentation
    {
        internal const int MaximumBytes = 28 * 1024;
        internal const int MaximumSurfaces = 32;
        private const int EventBurst = 32;
        private const int EventsPerSecond = 20;
        private const int ReceiveBurstBytes = 128 * 1024;
        private const int ReceiveBytesPerSecond = 64 * 1024;
        private const int BaselineBurstBytes = 32 * 1024;
        private const int BaselineBytesPerSecond = 32 * 1024;
        private readonly MultiplayerPlayers players;
        private readonly List<MirroredHudPresenter> surfaces = new List<MirroredHudPresenter>();
        private readonly List<MirroredHudPresenter> authoredHuds = new List<MirroredHudPresenter>();
        private readonly Dictionary<ParticipantId, Delivery> deliveries = new Dictionary<ParticipantId, Delivery>();
        private readonly List<ParticipantId> departed = new List<ParticipantId>();
        private readonly WorldWireWriter writer = new WorldWireWriter(32 * 1024);
        private readonly HudMirrorCoalescer coalescer = new HudMirrorCoalescer();
        private readonly Func<PresentationSink> receiverFactory;
        private PresentationSink? receiver;
        private byte[] baseline = Array.Empty<byte>();
        private string? baselineClockEpoch;
        private ulong version = 1, publishedVersion, nextToken;
        private int surfaceBytes;
        internal long Now => players.Now;
        internal string UiClockEpoch => players.UiClockEpoch;
        internal bool Hosting => players.Realm == NetRealm.ListenHost && players.Channel != null;
        private PresentationSink Receiver
        {
            get
            {
                if (receiver == null)
                {
                    receiver = receiverFactory();
                    if (receiver.Ui is IUiClockReceiver target) target.FollowClock(() => players.UiClockTime, () => UiClockEpoch);
                }
                return receiver;
            }
        }

        internal IUiService UiReceiver => Receiver.Ui;

        internal MultiplayerPresentation(MultiplayerPlayers players, Func<PresentationSink> receiverFactory)
        {
            this.players = players;
            this.receiverFactory = receiverFactory;
        }

        internal ulong Token() => ++nextToken;

        internal OperationResult<IUiSurface> CreateSharedSurface(RuntimeReplication context, IUiService local, UiSurfaceRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (context.Lifetime.IsStopping) return Stopped<IUiSurface>();
            var themed = UiPresentationTheme.Resolve(local, request, out var theme);
            if (!themed.Succeeded) return OperationResult<IUiSurface>.Failure(themed.ErrorCode, themed.ErrorMessage);
            var source = local as IUiPresentationThemeSource;
            var validation = UiPresentationImages.Prepare(request.Content, source?.PresentationThemeScope, theme, out var presentation);
            if (!validation.Succeeded) return OperationResult<IUiSurface>.Failure(validation.ErrorCode, validation.ErrorMessage);
            validation = UiPresentationInputs.Prepare(local, presentation, out presentation);
            if (!validation.Succeeded) return OperationResult<IUiSurface>.Failure(validation.ErrorCode, validation.ErrorMessage);
            var hud = new MirroredHudPresenter(this, context, request, nextToken + 1, local, theme);
            var staged = StageEnvelope(hud, theme, presentation, out var envelope);
            if (!staged.Succeeded) return OperationResult<IUiSurface>.Failure(staged.ErrorCode, staged.ErrorMessage);
            var prepared = hud.Prepare(presentation, envelope);
            if (Hosting && hud.InvalidComposition != null)
                return OperationResult<IUiSurface>.Failure(ModErrorCode.InvalidArgument, hud.InvalidComposition);
            if (Hosting && (!prepared || surfaces.Count >= MaximumSurfaces || surfaceBytes + hud.Bytes > MaximumBytes))
                return Limit<IUiSurface>();
            var result = local.CreateSurface(request);
            if (!result.TryGetValue(out var surface)) return result;
            Token();
            hud.Attach(surface);
            authoredHuds.Add(hud);
            // Solo keeps its local surface even if its full wire representation cannot be published yet.
            if (prepared && surfaces.Count < MaximumSurfaces && surfaceBytes + hud.Bytes <= MaximumBytes)
            {
                surfaces.Add(hud); surfaceBytes += hud.Bytes; version++;
                RememberGeneration(hud.Encoded);
            }
            hud.Tracking = context.Lifetime.Track(hud);
            return OperationResult<IUiSurface>.Success(hud);
        }

        internal bool Change(MirroredHudPresenter hud, MirroredHudPresenter.EncodedHud? payload, bool commit = true)
        {
            var registered = surfaces.Contains(hud);
            var next = surfaceBytes - (registered ? hud.Bytes : 0) + (payload?.Bytes.Length ?? 0) + 4;
            if (payload == null || next > MaximumBytes || (!registered && surfaces.Count >= MaximumSurfaces))
            {
                if (Hosting) return false;
                if (commit) { Remove(hud); hud.Encoded = null; }
                return true;
            }
            if (!commit) return true;
            if (!registered) surfaces.Add(hud);
            surfaceBytes = next; hud.Encoded = payload; version++;
            RememberGeneration(payload);
            return true;
        }

        internal void Remove(MirroredHudPresenter hud)
        {
            if (!surfaces.Remove(hud)) return;
            surfaceBytes -= hud.Bytes; version++;
        }

        internal void Release(MirroredHudPresenter hud)
        {
            authoredHuds.Remove(hud);
            Remove(hud);
        }

        internal bool ChangeVisibility(MirroredHudPresenter hud, MirroredHudPresenter.EncodedHud? payload, bool commit = true) =>
            !surfaces.Contains(hud) || Change(hud, payload, commit);

        internal void RefreshClockBindings()
        {
            var epoch = UiClockEpoch;
            for (var i = surfaces.Count - 1; i >= 0; i--)
                if (surfaces[i].IsAlive) surfaces[i].RefreshClockBinding(epoch);
        }

        internal void Send()
        {
            if (!Hosting) return;
            var epoch = UiClockEpoch;
            for (var i = surfaces.Count - 1; i >= 0; i--)
            {
                var surface = surfaces[i];
                if (surface.Context.Lifetime.IsStopping) surface.Dispose();
                else if (!surface.IsAlive) surface.Detach();
                else surface.RefreshClockBinding(epoch);
            }
            ReconcileAudio();
            if (coalescer.Offer(version, Now) == HudMirrorDecision.SendNow || coalescer.TryFlush(Now, out _))
                PublishBaseline();
            // A new stream must wait for the coalescer rather than receive a baseline from an ended clock.
            if (baselineClockEpoch != null && baselineClockEpoch != epoch) return;
            var snapshot = players.Snapshot!;
            var channel = players.Channel!;
            departed.Clear();
            foreach (var pair in deliveries)
                if (!snapshot.TryGetParticipant(pair.Key, out _)) departed.Add(pair.Key);
            for (var i = 0; i < departed.Count; i++) deliveries.Remove(departed[i]);
            for (var i = 0; i < snapshot.Participants.Count; i++)
            {
                var peer = snapshot.Participants[i];
                if (peer.IsLocal) continue;
                var generation = channel.GetGeneration(peer.Id);
                if (generation == 0) continue;
                var delivery = DeliveryFor(peer.Id, generation);
                if (delivery.Version == publishedVersion) continue;
                if (delivery.Version != 0 && Now - delivery.SentAt < PresentationRoutingPolicy.HudMirrorIntervalMicros) continue;
                // Retry each lagging peer independently with the latest full state.
                if (!channel.CanSend(InternalSystemTarget.Only(peer.Id), baseline.Length) ||
                    !delivery.BaselineBytes.TryTake(Now, baseline.Length)) continue;
                var result = channel.Send(new ArraySegment<byte>(baseline), InternalSystemTarget.Only(peer.Id));
                if (result.TryGetValue(out var count) && count > 0)
                { delivery.Version = publishedVersion; delivery.SentAt = Now; }
            }
        }

        private void PublishBaseline()
        {
            writer.Begin(30); writer.WriteString(""); writer.WriteVarUInt(version);
            writer.WriteVarUInt((ulong)surfaces.Count);
            baselineClockEpoch = null;
            for (var i = 0; i < surfaces.Count; i++)
            {
                var payload = surfaces[i].Encoded!.Value;
                writer.WriteBytes(payload.Bytes);
                baselineClockEpoch ??= payload.ClockEpoch;
            }
            WriteLoops(writer);
            baseline = Copy(writer); publishedVersion = version;
        }

        internal void Reset()
        {
            deliveries.Clear();
            ClearReceived();
            ResetEffects();
            ResetRemoteStingers();
            version++;
        }

        internal static byte[] Copy(WorldWireWriter value)
        {
            var bytes = new byte[value.Length];
            Buffer.BlockCopy(value.Segment.Array!, value.Segment.Offset, bytes, 0, bytes.Length);
            return bytes;
        }

        internal static OperationResult<T> Limit<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.RateLimited,
            "Shared presentation is full or too large; use fewer surfaces or shorter content and try again.");

        private Delivery DeliveryFor(ParticipantId participant, ulong generation)
        {
            if (!deliveries.TryGetValue(participant, out var delivery) || delivery.Generation != generation)
                deliveries[participant] = delivery = new Delivery(generation, Now);
            return delivery;
        }

        private sealed class Delivery
        {
            internal readonly ulong Generation;
            internal ulong Version;
            internal long SentAt;
            internal readonly TokenBucket BaselineBytes;
            internal readonly TokenBucket Events;
            internal readonly TokenBucket EventBytes;

            internal Delivery(ulong generation, long now)
            {
                Generation = generation;
                BaselineBytes = new TokenBucket(BaselineBurstBytes, BaselineBytesPerSecond, now);
                Events = new TokenBucket(EventBurst, EventsPerSecond, now);
                // The receiving byte bucket covers both baselines and one-shot effects.
                EventBytes = new TokenBucket(ReceiveBurstBytes - BaselineBurstBytes,
                    ReceiveBytesPerSecond - BaselineBytesPerSecond, now);
            }
        }
    }

    internal sealed class PresentationSink
    {
        internal readonly IUiService Ui;
        internal readonly IAudioService Audio;
        internal PresentationSink(IUiService ui, IAudioService audio) { Ui = ui; Audio = audio; }
    }
}
