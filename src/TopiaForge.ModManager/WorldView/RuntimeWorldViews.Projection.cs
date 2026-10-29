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
            List<ViewTarget>? stale = null;
            foreach (var target in projected)
                if (Array.IndexOf(received.Hidden, target.Key) < 0 || !ReferenceEquals(Resolve(target.Key), target.Value.Entity))
                    (stale ??= new List<ViewTarget>()).Add(target.Key);
            if (stale != null) foreach (var target in stale)
            { var old = projected[target]; projected.Remove(target); old.Lease.Dispose(); }
            foreach (var target in received.Hidden)
            {
                if (projected.ContainsKey(target)) continue;
                var entity = Resolve(target);
                if (entity == null) continue;
                if (renderer.Hide(entity).TryGetValue(out var lease)) projected.Add(target, new HiddenTarget(entity, lease));
            }
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
