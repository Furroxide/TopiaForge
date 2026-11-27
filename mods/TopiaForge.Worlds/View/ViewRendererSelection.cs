using System;
using System.Collections.Generic;

namespace TopiaForge.Worlds
{
    internal static class ViewRendererSelection
    {
        internal static bool BelongsTo<T>(T root, T rendererNode, Func<T, T?> parent, Func<T, bool> authored) where T : class
        {
            var seen = new HashSet<T>();
            for (var node = rendererNode; node != null && seen.Add(node); node = parent(node))
            {
                if (EqualityComparer<T>.Default.Equals(node, root)) return true;
                if (authored(node)) return false;
            }
            return false;
        }
    }
}
