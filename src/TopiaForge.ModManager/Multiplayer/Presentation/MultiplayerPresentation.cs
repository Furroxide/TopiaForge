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
        private readonly MultiplayerPlayers players;
        private readonly List<MirroredHudPresenter> surfaces = new List<MirroredHudPresenter>();
        private readonly Dictionary<ParticipantId, Delivery> deliveries = new Dictionary<ParticipantId, Delivery>();
        private readonly List<ParticipantId> departed = new List<ParticipantId>();
        private readonly WorldWireWriter writer = new WorldWireWriter(32 * 1024);
        private readonly HudMirrorCoalescer coalescer = new HudMirrorCoalescer();
        private readonly Func<PresentationSink> receiverFactory;
        private PresentationSink? receiver;
        private byte[] baseline = Array.Empty<byte>();
        private ulong version = 1, publishedVersion, nextToken;
        private int surfaceBytes;
        internal long Now => players.Now;
        internal bool Hosting => players.Realm == NetRealm.ListenHost && players.Channel != null;
        private PresentationSink Receiver => receiver ??= receiverFactory();

        internal MultiplayerPresentation(MultiplayerPlayers players, Func<PresentationSink> receiverFactory)
        {
            this.players = players;
            this.receiverFactory = receiverFactory;
        }

        internal ulong Token() => ++nextToken;

        internal OperationResult<IUiSurface> CreateHud(RuntimeReplication context, IUiService local, UiSurfaceRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (context.Lifetime.IsStopping) return Stopped<IUiSurface>();
            var token = Token();
            var hud = new MirroredHudPresenter(this, context, request, token);
            var prepared = hud.Prepare(request.Body, request.Content, true);
            if (Hosting && (!prepared || surfaces.Count >= MaximumSurfaces || surfaceBytes + hud.Bytes > MaximumBytes))
                return Limit<IUiSurface>();
            var result = local.CreateSurface(request);
            if (!result.TryGetValue(out var surface)) return result;
            hud.Attach(surface);
            // Solo remains unrestricted. Only wire-sized HUDs are eligible for a later promotion.
            if (prepared && surfaces.Count < MaximumSurfaces && surfaceBytes + hud.Bytes <= MaximumBytes)
            {
                surfaces.Add(hud); surfaceBytes += hud.Bytes; version++;
            }
            hud.Tracking = context.Lifetime.Track(hud);
            return OperationResult<IUiSurface>.Success(hud);
        }

        internal bool Change(MirroredHudPresenter hud, byte[]? payload, bool commit = true)
        {
            var registered = surfaces.Contains(hud);
            var next = surfaceBytes - (registered ? hud.Bytes : 0) + (payload?.Length ?? 0) + 4;
            if (payload == null || next > MaximumBytes || (!registered && surfaces.Count >= MaximumSurfaces))
            {
                if (Hosting) return false;
                if (commit) { Remove(hud); hud.Payload = null; }
                return true;
            }
            if (!commit) return true;
            if (!registered) surfaces.Add(hud);
            surfaceBytes = next; hud.Payload = payload; version++;
            return true;
        }

        internal void Remove(MirroredHudPresenter hud)
        {
            if (!surfaces.Remove(hud)) return;
            surfaceBytes -= hud.Bytes; version++;
        }

        internal bool ChangeVisibility(MirroredHudPresenter hud, byte[]? payload, bool commit = true) =>
            !surfaces.Contains(hud) || Change(hud, payload, commit);

        internal void Send()
        {
            if (!Hosting) return;
            for (var i = surfaces.Count - 1; i >= 0; i--)
                if (surfaces[i].Context.Lifetime.IsStopping || !surfaces[i].IsAlive) surfaces[i].Dispose();
            ReconcileAudio();
            if (coalescer.Offer(version, Now) == HudMirrorDecision.SendNow || coalescer.TryFlush(Now, out _))
                PublishBaseline();
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
                if (!deliveries.TryGetValue(peer.Id, out var delivery)) deliveries.Add(peer.Id, delivery = new Delivery(Now));
                if (delivery.Generation == generation && delivery.Version == publishedVersion) continue;
                if (delivery.Generation == generation && Now - delivery.SentAt < PresentationRoutingPolicy.HudMirrorIntervalMicros) continue;
                // Retry each lagging peer independently with the latest full state, never gate everyone on one window.
                if (!channel.CanSend(InternalSystemTarget.Only(peer.Id), baseline.Length) || !delivery.Bytes.TryTake(Now, baseline.Length)) continue;
                var result = channel.Send(new ArraySegment<byte>(baseline), InternalSystemTarget.Only(peer.Id));
                if (result.TryGetValue(out var count) && count > 0)
                { delivery.Generation = generation; delivery.Version = publishedVersion; delivery.SentAt = Now; }
            }
        }

        private void PublishBaseline()
        {
            writer.Begin(30); writer.WriteString(""); writer.WriteVarUInt(version);
            writer.WriteVarUInt((ulong)surfaces.Count);
            for (var i = 0; i < surfaces.Count; i++) writer.WriteBytes(surfaces[i].Payload!);
            WriteLoops(writer);
            baseline = Copy(writer); publishedVersion = version;
        }

        internal void Reset()
        {
            deliveries.Clear();
            ClearReceived();
            ResetEffects();
            version++;
        }

        internal static byte[] Copy(WorldWireWriter value)
        {
            var bytes = new byte[value.Length];
            Buffer.BlockCopy(value.Segment.Array!, value.Segment.Offset, bytes, 0, bytes.Length);
            return bytes;
        }

        internal static OperationResult<T> Limit<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.RateLimited,
            "Shared presentation is full or too large; use fewer HUDs or shorter content and try again.");

        private sealed class Delivery
        {
            internal ulong Generation, Version;
            internal long SentAt;
            internal readonly TokenBucket Bytes;
            internal Delivery(long now) { Bytes = new TokenBucket(32 * 1024, 32 * 1024, now); }
        }
    }

    internal sealed class PresentationSink
    {
        internal readonly IUiService Ui;
        internal readonly IAudioService Audio;
        internal PresentationSink(IUiService ui, IAudioService audio) { Ui = ui; Audio = audio; }
    }
}
