using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.Worlds
{
    /// <summary>Temporarily removes inherited directional lighting while an arena owns its sun.</summary>
    internal sealed class ArenaSunSuppression : IDisposable
    {
        private static readonly Dictionary<Light, Entry> Borrowed = new Dictionary<Light, Entry>();
        private readonly List<Light> owned = new List<Light>();
        private bool disposed;

        internal void Apply(IEnumerable<Light> lights)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ArenaSunSuppression));
            foreach (var light in lights)
            {
                if (light == null || light.type != LightType.Directional || !light.gameObject.scene.IsValid() || owned.Contains(light)) continue;
                if (!Borrowed.TryGetValue(light, out var entry))
                {
                    entry = new Entry(light.enabled);
                    Borrowed.Add(light, entry);
                }
                owned.Add(light);
                entry.Users++;
                light.enabled = false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var light in owned)
            {
                var entry = Borrowed[light];
                if (--entry.Users != 0) continue;
                Borrowed.Remove(light);
                if (light != null) light.enabled = entry.Enabled;
            }
            owned.Clear();
        }

        private sealed class Entry
        {
            internal Entry(bool enabled) { Enabled = enabled; }
            internal bool Enabled { get; }
            internal int Users { get; set; }
        }
    }
}
