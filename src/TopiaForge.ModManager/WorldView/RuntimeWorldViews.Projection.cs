using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeWorldViews
    {
        private readonly Dictionary<ViewTarget, HiddenTarget> projected = new Dictionary<ViewTarget, HiddenTarget>();
        // The participant and key each listed player name was first resolved for, while that admission lasts.
        private readonly Dictionary<ViewTarget, ListedPlayer> listedPlayers = new Dictionary<ViewTarget, ListedPlayer>();
        private List<ViewTarget>? unlisted;
        private MultiplayerSessionSnapshot? observedRoster;
        private string? projectedEnvironment;
        private VisionFog? projectedFog;
        private void ApplyReceived()
        {
            var renderer = binding?.Renderer;
            if (renderer == null || !renderer.IsAlive) return;
            if (received.World != binding!.WorldId) { ClearProjection(); return; }
            if (projectedEnvironment != received.Environment && renderer.SetEnvironment(received.Environment).Succeeded)
                projectedEnvironment = received.Environment;
            if (!Nullable.Equals(projectedFog, received.Fog) && renderer.SetFog(received.Fog).Succeeded)
                projectedFog = received.Fog;
            // Re-resolve stable references: a player's replacement avatar may be a different native object.
            List<KeyValuePair<ViewTarget, HiddenTarget>>? stale = null;
            foreach (var target in projected)
                if (Array.IndexOf(received.Hidden, target.Key) < 0 || !Holds(target.Key, target.Value))
                    (stale ??= new List<KeyValuePair<ViewTarget, HiddenTarget>>()).Add(target);
            if (stale != null) foreach (var target in stale) projected.Remove(target.Key);
            var applied = false;
            try
            {
                foreach (var target in received.Hidden)
                {
                    if (projected.ContainsKey(target)) continue;
                    var entity = ResolveListed(target);
                    if (entity == null) continue;
                    // A player renamed for a new lifetime keeps the hold taken under the old name: no release, no
                    // gap. The host flushes the roster before the views' message on the same ordered stream, so the
                    // new name resolves here by the time the renamed view arrives.
                    var moved = Moved(stale, entity);
                    if (moved != null) { projected.Add(target, moved); continue; }
                    if (renderer.Hide(entity).TryGetValue(out var lease)) projected.Add(target, new HiddenTarget(entity, lease));
                }
                applied = true;
            }
            finally
            {
                // Release what this pass replaced. A failed pass keeps tracking the old holds instead, so none is left
                // held untracked and nothing the last good view hid is shown; the next pass retries.
                if (stale != null)
                    foreach (var target in stale)
                        if (applied || projected.ContainsKey(target.Key)) target.Value.Lease.Dispose();
                        else projected.Add(target.Key, target.Value);
                if (listedPlayers.Count != 0) ForgetUnlisted();
            }
        }
        // A listed player keeps its hold on the root presence retains for them while the name cannot resolve: during
        // the reconnect grace, and after a rejoin until the renamed baseline arrives. A player who left releases it.
        private bool Holds(ViewTarget target, HiddenTarget held)
        {
            var current = ResolveListed(target);
            return ReferenceEquals(current, held.Entity) || (current == null && target.Kind == 3 && Retained(held.Entity));
        }
        // A listed player resolves by name or, while the name cannot resolve, to the current avatar of the participant
        // it was first resolved for. Poses can arrive before the host's renamed view, so a root made after a rejoin is
        // held as soon as presence makes it.
        private IEntity? ResolveListed(ViewTarget target)
        {
            var entity = Resolve(target);
            if (target.Kind != 3) return entity;
            if (entity != null)
            {
                if (!listedPlayers.ContainsKey(target) && services.TryGetPlayerParticipant(entity, out var participant, out var key))
                    listedPlayers.Add(target, new ListedPlayer(participant, key));
                return entity;
            }
            return listedPlayers.TryGetValue(target, out var listed) && services.Presence?.TryGetAvatar(listed.Participant, out var avatar) == true
                && avatar != null && avatar.IsAlive ? avatar : null;
        }
        // A participant who left, or came back under another key, is a new player: their old name no longer follows them.
        private void ObserveRoster()
        {
            var snapshot = services.Runtime.Control?.SessionSnapshot;
            if (snapshot == null || ReferenceEquals(observedRoster, snapshot)) return;
            observedRoster = snapshot;
            if (listedPlayers.Count == 0) return;
            foreach (var listed in listedPlayers)
                if (!snapshot.TryGetParticipant(listed.Value.Participant, out var row) || row == null || !row.PlayerKey.Equals(listed.Value.Key))
                    (unlisted ??= new List<ViewTarget>()).Add(listed.Key);
            ForgetCollected();
        }
        private void ForgetUnlisted()
        {
            foreach (var listed in listedPlayers)
                if (Array.IndexOf(received.Hidden, listed.Key) < 0) (unlisted ??= new List<ViewTarget>()).Add(listed.Key);
            ForgetCollected();
        }
        private void ForgetCollected()
        {
            if (unlisted == null || unlisted.Count == 0) return;
            for (var index = 0; index < unlisted.Count; index++) listedPlayers.Remove(unlisted[index]);
            unlisted.Clear();
        }
        private static HiddenTarget? Moved(List<KeyValuePair<ViewTarget, HiddenTarget>>? stale, IEntity entity)
        {
            if (stale == null) return null;
            for (var index = 0; index < stale.Count; index++)
            {
                var held = stale[index];
                if (held.Key.Kind != 3 || !ReferenceEquals(held.Value.Entity, entity)) continue;
                stale.RemoveAt(index);
                return held.Value;
            }
            return null;
        }
        private void ClearProjection()
        {
            listedPlayers.Clear(); observedRoster = null;
            if (projected.Count == 0 && projectedEnvironment == null && projectedFog == null) return;
            var failures = new List<Exception>();
            foreach (var target in projected.Values)
                try { target.Lease.Dispose(); } catch { failures.Add(new InvalidOperationException("A private view could not be restored.")); }
            projected.Clear();
            var renderer = binding?.Renderer;
            if (renderer?.IsAlive == true)
            {
                if (projectedEnvironment != null) renderer.SetEnvironment(null);
                if (projectedFog != null) renderer.SetFog(null);
            }
            projectedEnvironment = null; projectedFog = null;
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private readonly struct ListedPlayer
        {
            internal readonly ParticipantId Participant;
            internal readonly MultiplayerPlayerKey Key;
            internal ListedPlayer(ParticipantId participant, MultiplayerPlayerKey key) { Participant = participant; Key = key; }
        }
        private sealed class HiddenTarget
        {
            internal readonly IEntity Entity;
            internal readonly IDisposable Lease;
            internal HiddenTarget(IEntity entity, IDisposable lease) { Entity = entity; Lease = lease; }
        }
    }
}
