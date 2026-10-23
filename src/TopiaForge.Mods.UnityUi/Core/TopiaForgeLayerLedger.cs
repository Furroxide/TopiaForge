using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Which sorting order each kit canvas holds, recorded when the order was allocated and whenever the allocator moved it
    /// (window focus permutes the orders its windows hold). A canvas's slot goes back to its band by that record, never by
    /// reading the canvas: a canvas that a scene or another mod destroyed can no longer be read. Before this ledger such a
    /// canvas kept its slot for good, so a HUD made in each world moved one order up the band with every world change.
    /// Canvases are keyed by reference, so a destroyed canvas still finds its record. Unity-free and unit-tested.
    /// </summary>
    /// <typeparam name="TCanvas">What stands for a canvas: its root object in the game, any object in tests.</typeparam>
    public sealed class TopiaForgeLayerLedger<TCanvas> where TCanvas : class
    {
        private readonly TopiaForgeLayerBands bands;
        private readonly Dictionary<TCanvas, Slot> slots = new Dictionary<TCanvas, Slot>(ReferenceComparer.Instance);

        /// <summary>Creates a ledger over a band allocator.</summary>
        public TopiaForgeLayerLedger(TopiaForgeLayerBands bands)
        {
            this.bands = bands ?? throw new ArgumentNullException(nameof(bands));
        }

        /// <summary>Gets how many canvases hold a slot.</summary>
        public int Count => slots.Count;

        /// <summary>
        /// Allocates the next order in a band for a canvas and records it. <paramref name="exhausted"/> says the band was
        /// full and the order is shared, as the allocator does. A canvas that already holds a slot keeps it.
        /// </summary>
        public int Claim(TCanvas canvas, TopiaForgeLayerBand band, out bool exhausted)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            if (slots.TryGetValue(canvas, out var held))
            {
                exhausted = false;
                return held.Order;
            }

            exhausted = !bands.TryAllocate(band, out var order);
            slots.Add(canvas, new Slot(band, order));
            return order;
        }

        /// <summary>Gets the order a canvas holds by its record, or false for a canvas that holds no slot.</summary>
        public bool TryGetOrder(TCanvas canvas, out int sortingOrder)
        {
            if (canvas != null && slots.TryGetValue(canvas, out var slot))
            {
                sortingOrder = slot.Order;
                return true;
            }

            sortingOrder = 0;
            return false;
        }

        /// <summary>
        /// Records that the allocator gave a canvas another order of its own band, as window focus does when it permutes the
        /// orders its windows hold. Returns false, and records nothing, for a canvas without a slot or an order outside its
        /// band.
        /// </summary>
        public bool Reassign(TCanvas canvas, int sortingOrder)
        {
            if (canvas == null || !slots.TryGetValue(canvas, out var slot) || !bands.Holds(slot.Band, sortingOrder))
            {
                return false;
            }

            slots[canvas] = new Slot(slot.Band, sortingOrder);
            return true;
        }

        /// <summary>
        /// Returns a canvas's slot to its band by the record, whatever became of the canvas. A canvas released once, or never
        /// claimed, releases nothing, so a canvas destroyed after its owner released it cannot free a slot that another
        /// canvas has since taken.
        /// </summary>
        public bool Release(TCanvas canvas)
        {
            if (canvas == null || !slots.TryGetValue(canvas, out var slot))
            {
                return false;
            }

            slots.Remove(canvas);
            bands.TryRelease(slot.Order);
            return true;
        }

        private readonly struct Slot
        {
            internal Slot(TopiaForgeLayerBand band, int order)
            {
                Band = band;
                Order = order;
            }

            internal TopiaForgeLayerBand Band { get; }

            internal int Order { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<TCanvas>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(TCanvas? left, TCanvas? right) => ReferenceEquals(left, right);

            public int GetHashCode(TCanvas value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
