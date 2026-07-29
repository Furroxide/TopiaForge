using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Sorting bands replacing the old hardcoded canvas orders (31800/31900/32000).</summary>
    public enum TopiaForgeLayerBand
    {
        /// <summary>Selects the HUD option.</summary>
        Hud,
        /// <summary>Selects the window option.</summary>
        Window,
        /// <summary>Selects the modal option.</summary>
        Modal,
        /// <summary>Selects the toast option.</summary>
        Toast,
        /// <summary>Selects the debug option.</summary>
        Debug,
    }

    /// <summary>
    /// Sequential canvas sorting-order allocation within fixed bands, keeping every kit
    /// canvas above the game's UI and modal surfaces above HUD surfaces regardless of
    /// creation order across mods. Pure and unit-tested; TopiaForgeLayers wraps it with logging.
    /// </summary>
    public sealed class TopiaForgeLayerBands
    {
        /// <summary>The default HUD base design-token value.</summary>
        public const int DefaultHudBase = 30000;
        /// <summary>The default window base design-token value.</summary>
        public const int DefaultWindowBase = 30800;
        /// <summary>The default modal base design-token value.</summary>
        public const int DefaultModalBase = 31400;
        /// <summary>The default toast base design-token value.</summary>
        public const int DefaultToastBase = 31800;
        /// <summary>The default debug base design-token value.</summary>
        public const int DefaultDebugBase = 31900;
        /// <summary>The default ceiling design-token value.</summary>
        public const int DefaultCeiling = 32000;

        private readonly int[] bases;
        private readonly int[] limits;
        private readonly int[] next;
        private readonly SortedSet<int>[] released;
        private readonly Dictionary<int, int>[] allocationCounts;

        /// <summary>Creates layer-band ranges using the default band size.</summary>
        public TopiaForgeLayerBands()
            : this(DefaultHudBase, DefaultWindowBase, DefaultModalBase, DefaultToastBase, DefaultDebugBase, DefaultCeiling)
        {
        }

        /// <summary>Creates layer-band ranges using the supplied bases and band size.</summary>
        public TopiaForgeLayerBands(int hudBase, int windowBase, int modalBase, int toastBase, int debugBase, int ceiling)
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

        /// <summary>Gets the base sorting order for a layer band.</summary>
        public int BaseOf(TopiaForgeLayerBand band)
        {
            return bases[(int)band];
        }

        /// <summary>
        /// Allocates the next sorting order in a band. Returns false on exhaustion (the
        /// caller should log and reuse the band's last order rather than throw mid-game).
        /// </summary>
        public bool TryAllocate(TopiaForgeLayerBand band, out int sortingOrder)
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
        public int Remaining(TopiaForgeLayerBand band)
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
