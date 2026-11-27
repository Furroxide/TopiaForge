using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresence
    {
        private readonly Dictionary<ParticipantId, PresencePresentationRecord> pendingPresentations = new Dictionary<ParticipantId, PresencePresentationRecord>();
        private readonly List<ParticipantId> appliedPresentations = new List<ParticipantId>();
        private readonly WorldWireWriter presentationWriter = new WorldWireWriter(PresencePresentationWire.MaximumBytes);
        private ulong presentationVersion;
        private long nextPresentationSend;

        internal AvatarPresentation? PresentationFor(ParticipantId id)
        {
            UnityMainThreadGuard.AssertCurrent();
            Reconcile(Now());
            if (!peersById.TryGetValue(id, out var peer) || peer.Removed || !peer.IsConnected) return null;
            return Realm == NetRealm.Client && !peer.HasPresentation ? null : EnsurePresentation(peer);
        }

        private AvatarPresentation EnsurePresentation(PresencePeer peer)
        {
            if (peer.Presentation != null) return peer.Presentation;
            var tint = PresenceTint.For(peer.Index);
            var color = new RgbaColor(tint.Red, tint.Green, tint.Blue);
            peer.PresentationVersion = ++presentationVersion;
            peer.Presentation = new AvatarPresentation(new AvatarAppearance(color, color), () =>
            {
                peer.PresentationVersion = ++presentationVersion;
                nametagVersion++;
            });
            return peer.Presentation;
        }

        // Reliable, per-recipient dirty state: at most four records per recipient per 100 ms.
        // A successful enqueue is required before a version is acknowledged. Stream replacement resets the baseline.
        internal void SendPresentations(IInternalSystemChannel open)
        {
            var now = Now();
            if (now < nextPresentationSend || reconciled == null) return;
            nextPresentationSend = now + 100_000;
            for (var i = 0; i < peers.Count; i++)
            {
                var recipient = peers[i];
                if (recipient.IsLocal || !recipient.IsConnected || recipient.Removed) continue;
                var generation = open.GetGeneration(recipient.Id);
                if (generation == 0) continue;
                if (generation != recipient.PresentationGeneration)
                {
                    recipient.PresentationGeneration = generation;
                    Array.Clear(recipient.PresentationSent, 0, recipient.PresentationSent.Length);
                }
                var target = InternalSystemTarget.Only(recipient.Id);
                var sent = 0;
                for (var scanned = 0; scanned < peers.Count && sent < 4; scanned++)
                {
                    if (recipient.PresentationCursor >= peers.Count) recipient.PresentationCursor = 0;
                    var subject = peers[recipient.PresentationCursor++];
                    if (subject.Index == 0 || subject.Removed || subject.Presentation == null
                        || recipient.PresentationSent[subject.Index] == subject.PresentationVersion) continue;
                    PresencePresentationWire.Write(presentationWriter, reconciled.Id.Value, subject);
                    if (!open.CanSend(target, presentationWriter.Length)) break;
                    var result = open.Send(presentationWriter.Segment, target);
                    if (!result.Succeeded || result.Value == 0) break;
                    recipient.PresentationSent[subject.Index] = subject.PresentationVersion;
                    sent++;
                }
            }
        }

        internal bool TakePresentation(WorldWireReader reader)
        {
            if (Realm != NetRealm.Client || !PresencePresentationWire.TryRead(reader, out var record)) return false;
            if (reconciled == null || record!.Session != reconciled.Id.Value) return false;
            if (peersById.TryGetValue(record.Id, out var peer) && (peer.PlayerKey.Value ?? string.Empty) == record.Key && peer.PresentationLifetime == record.Lifetime)
            {
                EnsurePresentation(peer).Take(record.Appearance, record.Name, record.Visible, record.PoweredDown);
                peer.HasPresentation = true;
                return true;
            }
            // Admission snapshots and reliable presence may be observed in either order. Never attach by index alone.
            if (!pendingPresentations.ContainsKey(record.Id) && pendingPresentations.Count >= PresenceMessages.MaximumEntries) return false;
            pendingPresentations[record.Id] = record;
            return true;
        }

        private void ApplyPendingPresentations()
        {
            if (pendingPresentations.Count == 0 || reconciled == null) return;
            appliedPresentations.Clear();
            foreach (var pair in pendingPresentations)
            {
                var record = pair.Value;
                if (!peersById.TryGetValue(record.Id, out var peer)) continue;
                if (record.Session == reconciled.Id.Value && record.Key == (peer.PlayerKey.Value ?? string.Empty) && record.Lifetime == peer.PresentationLifetime)
                {
                    EnsurePresentation(peer).Take(record.Appearance, record.Name, record.Visible, record.PoweredDown);
                    peer.HasPresentation = true;
                }
                appliedPresentations.Add(pair.Key);
            }
            for (var i = 0; i < appliedPresentations.Count; i++) pendingPresentations.Remove(appliedPresentations[i]);
        }
    }
}
