using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeWorldViews
    {
        private readonly Dictionary<ViewTarget, HiddenTarget> projected = new Dictionary<ViewTarget, HiddenTarget>();
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
                    var entity = Resolve(target);
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
            }
        }
        // A listed player keeps its hold on the root presence retains for them while the name cannot resolve: during
        // the reconnect grace, and after a rejoin until the renamed baseline arrives. A player who left releases it.
        private bool Holds(ViewTarget target, HiddenTarget held)
        {
            var current = Resolve(target);
            return ReferenceEquals(current, held.Entity) || (current == null && target.Kind == 3 && Retained(held.Entity));
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
        private sealed class HiddenTarget
        {
            internal readonly IEntity Entity;
            internal readonly IDisposable Lease;
            internal HiddenTarget(IEntity entity, IDisposable lease) { Entity = entity; Lease = lease; }
        }
    }
}
