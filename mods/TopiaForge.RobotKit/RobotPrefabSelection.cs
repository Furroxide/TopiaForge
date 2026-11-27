using System;
using System.Collections.Generic;

namespace TopiaForge.RobotKit
{
    internal static class RobotPrefabSelection
    {
        internal static T? Select<T>(IReadOnlyList<T>? catalog, string? requestedId, Func<T, string> id)
            where T : class
        {
            if (catalog == null || catalog.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(requestedId)) return catalog[0];
            foreach (var candidate in catalog)
                if (string.Equals(id(candidate), requestedId, StringComparison.OrdinalIgnoreCase)) return candidate;
            return null;
        }
    }
}
