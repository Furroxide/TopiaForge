#nullable enable
using System;
using System.Collections.Generic;

namespace TopiaForge.WorldCompanion.Editor
{
    internal enum WorldMarkerStatus { Missing, Unique, Ambiguous, LimitExceeded }

    internal readonly struct WorldMarkerResult<T> where T : class
    {
        internal WorldMarkerResult(WorldMarkerStatus status, T? marker)
        { Status = status; Marker = marker; }
        internal WorldMarkerStatus Status { get; }
        internal T? Marker { get; }
    }

    // Unity-free production hierarchy search, linked into the ordinary test runner.
    internal static class WorldMarkerHierarchy
    {
        internal const int MaximumInspectedNodes = 16384;
        internal static WorldMarkerResult<T> Find<T>(T root, string name,
            Func<T, string> readName, Func<T, int> childCount, Func<T, int, T> childAt) where T : class
        {
            var queue = new Queue<T>(); queue.Enqueue(root);
            T? marker = null; var inspected = 0;
            while (queue.Count > 0)
            {
                if (++inspected > MaximumInspectedNodes)
                    return new WorldMarkerResult<T>(WorldMarkerStatus.LimitExceeded, null);
                var current = queue.Dequeue();
                if (string.Equals(readName(current), name, StringComparison.Ordinal))
                {
                    if (marker != null) return new WorldMarkerResult<T>(WorldMarkerStatus.Ambiguous, null);
                    marker = current;
                }
                // Transform children include inactive objects. The root participates too.
                for (var index = 0; index < childCount(current); index++) queue.Enqueue(childAt(current, index));
            }
            return new WorldMarkerResult<T>(marker == null ? WorldMarkerStatus.Missing : WorldMarkerStatus.Unique, marker);
        }
    }
}
