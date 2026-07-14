using System;
using System.Collections.Generic;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Sorting bands replacing the old hardcoded canvas orders (31800/31900/32000).</summary>
    public enum QwLayerBand
    {
        Hud,
        Window,
        Modal,
        Toast,
        Debug,
    }

    /// <summary>
    /// Sequential canvas sorting-order allocation within fixed bands, keeping every kit
    /// canvas above the game's UI and modal surfaces above HUD surfaces regardless of
    /// creation order across mods. Pure and unit-tested; QwLayers wraps it with logging.
    /// </summary>
    public sealed class QwLayerBands
    {
        public const int DefaultHudBase = 30000;
        public const int DefaultWindowBase = 30800;
        public const int DefaultModalBase = 31400;
        public const int DefaultToastBase = 31800;
        public const int DefaultDebugBase = 31900;
        public const int DefaultCeiling = 32000;

        private readonly int[] bases;
        private readonly int[] limits;
        private readonly int[] next;
        private readonly SortedSet<int>[] released;
        private readonly Dictionary<int, int>[] allocationCounts;

        public QwLayerBands()
            : this(DefaultHudBase, DefaultWindowBase, DefaultModalBase, DefaultToastBase, DefaultDebugBase, DefaultCeiling)
        {
        }

        public QwLayerBands(int hudBase, int windowBase, int modalBase, int toastBase, int debugBase, int ceiling)
        {
            if (!(hudBase < windowBase && windowBase < modalBase && modalBase < toastBase && toastBase < debugBase && debugBase < ceiling))
            {
                throw new ArgumentException("Layer band bases must be strictly ascending: hud < window < modal < toast < debug < ceiling.");
            }

            bases = new[] { hudBase, windowBase, modalBase, toastBase, debugBase };
            limits = new[] { windowBase, modalBase, toastBase, debugBase, ceiling };
            next = new[] { hudBase, windowBase, modalBase, toastBase, debugBase };
            released = new[]
            {
                new SortedSet<int>(),
                new SortedSet<int>(),
                new SortedSet<int>(),
                new SortedSet<int>(),
                new SortedSet<int>(),
            };
            allocationCounts = new[]
            {
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
            };
        }

        public int BaseOf(QwLayerBand band)
        {
            return bases[(int)band];
        }

        /// <summary>
        /// Allocates the next sorting order in a band. Returns false on exhaustion (the
        /// caller should log and reuse the band's last order rather than throw mid-game).
        /// </summary>
        public bool TryAllocate(QwLayerBand band, out int sortingOrder)
        {
            var index = (int)band;
            if (released[index].Count > 0)
            {
                sortingOrder = released[index].Min;
                released[index].Remove(sortingOrder);
            }
            else if (next[index] < limits[index])
            {
                sortingOrder = next[index];
                next[index]++;
            }
            else
            {
                sortingOrder = limits[index] - 1;
                IncrementCount(index, sortingOrder);
                return false;
            }

            IncrementCount(index, sortingOrder);
            return true;
        }

        /// <summary>
        /// Returns an allocated order to its band. Repeated exhaustion can temporarily share the
        /// band's last order; that slot is reusable only after every holder has released it.
        /// </summary>
        public bool TryRelease(int sortingOrder)
        {
            for (var index = 0; index < bases.Length; index++)
            {
                if (sortingOrder < bases[index] || sortingOrder >= limits[index])
                {
                    continue;
                }

                if (!allocationCounts[index].TryGetValue(sortingOrder, out var count))
                {
                    return false;
                }

                if (count > 1)
                {
                    allocationCounts[index][sortingOrder] = count - 1;
                }
                else
                {
                    allocationCounts[index].Remove(sortingOrder);
                    released[index].Add(sortingOrder);
                }

                return true;
            }

            return false;
        }

        /// <summary>Remaining allocations available in a band.</summary>
        public int Remaining(QwLayerBand band)
        {
            var index = (int)band;
            return (limits[index] - next[index]) + released[index].Count;
        }

        private void IncrementCount(int index, int sortingOrder)
        {
            allocationCounts[index].TryGetValue(sortingOrder, out var count);
            allocationCounts[index][sortingOrder] = count + 1;
        }
    }
}
