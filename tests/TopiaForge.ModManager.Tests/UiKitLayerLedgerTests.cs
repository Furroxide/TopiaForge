using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The kit's sorting-slot ledger. A canvas's slot goes back by what was allocated to it (or last assigned by the
    /// allocator), never by reading the canvas, which a scene or another mod may already have destroyed. In the game a HUD
    /// made in each world moved from order 30001 to 30002 across one world change, because the destroyed canvas could no
    /// longer say which order it held; the HUD band has 800 slots.
    /// </summary>
    internal static class UiKitLayerLedgerTests
    {
        public static void Run()
        {
            AClaimRecordsTheOrderAllocated();
            ADestroyedCanvasGivesItsSlotBackByTheRecord();
            ReleasingTwiceNeverFreesAnotherCanvasesSlot();
            WindowFocusMovesTheRecordWithTheOrder();
            ExhaustedBandsShareAndReleaseByCount();
            AThousandSceneChangesLeakNothing();
            Console.WriteLine("UiKitLayerLedgerTests passed.");
        }

        private static void AClaimRecordsTheOrderAllocated()
        {
            var ledger = new TopiaForgeLayerLedger<object>(new TopiaForgeLayerBands());
            var hud = new object();
            Assert(ledger.Claim(hud, TopiaForgeLayerBand.Hud, out var exhausted) == TopiaForgeLayerBands.DefaultHudBase && !exhausted,
                "the first HUD canvas takes the band's base order");
            Assert(ledger.TryGetOrder(hud, out var order) && order == TopiaForgeLayerBands.DefaultHudBase && ledger.Count == 1,
                "the ledger records the order the canvas holds");
            Assert(ledger.Claim(hud, TopiaForgeLayerBand.Hud, out _) == TopiaForgeLayerBands.DefaultHudBase && ledger.Count == 1,
                "claiming a canvas that holds a slot keeps that slot instead of taking another");
            Assert(!ledger.TryGetOrder(new object(), out _), "a canvas never claimed holds no slot");
            AssertThrows<ArgumentNullException>(() => ledger.Claim(null!, TopiaForgeLayerBand.Hud, out _), "a canvas is required");
        }

        private static void ADestroyedCanvasGivesItsSlotBackByTheRecord()
        {
            var bands = new TopiaForgeLayerBands();
            var ledger = new TopiaForgeLayerLedger<StandInCanvas>(bands);
            var before = bands.Remaining(TopiaForgeLayerBand.Hud);
            var hud = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud);
            Assert(hud.SortingOrder == TopiaForgeLayerBands.DefaultHudBase, "the canvas shows the order it was given");

            // A scene unloads the canvas: it can no longer be read, as a destroyed Unity canvas cannot.
            hud.Destroy();
            Assert(ledger.Release(hud), "a destroyed canvas still finds its record");
            Assert(bands.Remaining(TopiaForgeLayerBand.Hud) == before && ledger.Count == 0, "its slot is back in the band");
            var next = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud);
            Assert(next.SortingOrder == TopiaForgeLayerBands.DefaultHudBase,
                "the next world's HUD takes the same order, 30000, rather than moving up the band");
        }

        private static void ReleasingTwiceNeverFreesAnotherCanvasesSlot()
        {
            var bands = new TopiaForgeLayerBands();
            var ledger = new TopiaForgeLayerLedger<object>(bands);
            var first = new object();
            ledger.Claim(first, TopiaForgeLayerBand.Hud, out _);
            Assert(ledger.Release(first), "the owner releases the canvas before destroying it");
            var second = new object();
            Assert(ledger.Claim(second, TopiaForgeLayerBand.Hud, out _) == TopiaForgeLayerBands.DefaultHudBase,
                "another canvas takes the freed order");

            // The first canvas's destroy notice arrives late, as Unity calls OnDestroy at the end of the frame.
            Assert(!ledger.Release(first), "a canvas released once releases nothing again");
            Assert(ledger.TryGetOrder(second, out var held) && held == TopiaForgeLayerBands.DefaultHudBase
                && bands.Remaining(TopiaForgeLayerBand.Hud) == 799,
                "so the late notice cannot free the order the second canvas holds");
            Assert(!ledger.Release(new object()) && !ledger.Release(null!), "a canvas never claimed releases nothing");
        }

        private static void WindowFocusMovesTheRecordWithTheOrder()
        {
            var bands = new TopiaForgeLayerBands();
            var ledger = new TopiaForgeLayerLedger<StandInCanvas>(bands);
            var a = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Window);
            var b = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Window);
            var windowBase = TopiaForgeLayerBands.DefaultWindowBase;
            Assert(a.SortingOrder == windowBase && b.SortingOrder == windowBase + 1, "two windows hold the first two window orders");

            // Bringing A to the front swaps the two orders, as the window registry does.
            a.Assign(ledger, windowBase + 1);
            b.Assign(ledger, windowBase);
            a.Destroy();
            Assert(ledger.Release(a), "the destroyed front window releases the order it last held");
            var c = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Window);
            Assert(c.SortingOrder == windowBase + 1 && b.SortingOrder == windowBase,
                "a new window takes the order A last held, never the one B holds now");
            Assert(!ledger.Reassign(b, TopiaForgeLayerBands.DefaultHudBase), "an order outside the canvas's band is refused");
            Assert(ledger.TryGetOrder(b, out var kept) && kept == windowBase, "and the record is unchanged");
            Assert(!ledger.Reassign(new StandInCanvas(), windowBase + 5), "a canvas without a slot cannot be reassigned");
        }

        private static void ExhaustedBandsShareAndReleaseByCount()
        {
            var bands = new TopiaForgeLayerBands(0, 2, 4, 6, 8, 10);
            var ledger = new TopiaForgeLayerLedger<object>(bands);
            var one = new object();
            var two = new object();
            var three = new object();
            ledger.Claim(one, TopiaForgeLayerBand.Hud, out _);
            ledger.Claim(two, TopiaForgeLayerBand.Hud, out _);
            Assert(ledger.Claim(three, TopiaForgeLayerBand.Hud, out var exhausted) == 1 && exhausted,
                "a full band shares its last order and says so");
            ledger.Release(two);
            Assert(bands.Remaining(TopiaForgeLayerBand.Hud) == 0, "a shared order stays taken while another canvas holds it");
            ledger.Release(three);
            Assert(bands.Remaining(TopiaForgeLayerBand.Hud) == 1, "and goes back once every holder has released it");
        }

        private static void AThousandSceneChangesLeakNothing()
        {
            var bands = new TopiaForgeLayerBands();
            var ledger = new TopiaForgeLayerLedger<StandInCanvas>(bands);
            var hudBefore = bands.Remaining(TopiaForgeLayerBand.Hud);
            var windowBefore = bands.Remaining(TopiaForgeLayerBand.Window);
            var modalBefore = bands.Remaining(TopiaForgeLayerBand.Modal);

            // What lives as long as the mods: two persistent HUDs and a persistent window, made once.
            var persistent = new List<StandInCanvas>
            {
                StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud),
                StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud),
                StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Window),
            };

            var sceneOwned = new List<StandInCanvas>();
            for (var change = 0; change < 1000; change++)
            {
                // Each world has canvases of its own: a direct-kit HUD, a window that the player focuses, and a dialog.
                sceneOwned.Add(StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud));
                var window = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Window);
                sceneOwned.Add(window);
                var held = persistent[2].SortingOrder;
                persistent[2].Assign(ledger, window.SortingOrder);
                window.Assign(ledger, held);
                sceneOwned.Add(StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Modal));

                // The next scene load destroys them all, in no particular order; the kit releases by the record.
                for (var index = sceneOwned.Count - 1; index >= 0; index--)
                {
                    sceneOwned[index].Destroy();
                    Assert(ledger.Release(sceneOwned[index]), "change " + change + ": a destroyed canvas gives its slot back");
                }

                sceneOwned.Clear();
            }

            Assert(ledger.Count == persistent.Count, "after 1,000 scene changes only the persistent canvases hold slots");
            Assert(bands.Remaining(TopiaForgeLayerBand.Hud) == hudBefore - 2
                && bands.Remaining(TopiaForgeLayerBand.Window) == windowBefore - 1
                && bands.Remaining(TopiaForgeLayerBand.Modal) == modalBefore,
                "every band has exactly the slots it had, less the persistent canvases' own");
            var nextHud = StandInCanvas.Claimed(ledger, TopiaForgeLayerBand.Hud);
            Assert(nextHud.SortingOrder == TopiaForgeLayerBands.DefaultHudBase + 2,
                "a HUD made after 1,000 changes takes the first free order, as on the first world");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message);
        }

        /// <summary>Stands in for a canvas: it shows its order until destroyed, and then can no longer be read.</summary>
        private sealed class StandInCanvas
        {
            private int order;
            private bool destroyed;

            public int SortingOrder => destroyed
                ? throw new InvalidOperationException("A destroyed canvas cannot be read.")
                : order;

            public static StandInCanvas Claimed(TopiaForgeLayerLedger<StandInCanvas> ledger, TopiaForgeLayerBand band)
            {
                var canvas = new StandInCanvas();
                canvas.order = ledger.Claim(canvas, band, out _);
                return canvas;
            }

            public void Assign(TopiaForgeLayerLedger<StandInCanvas> ledger, int sortingOrder)
            {
                order = sortingOrder;
                Assert(ledger.Reassign(this, sortingOrder), "the allocator moves a canvas within its band");
            }

            public void Destroy() => destroyed = true;
        }
    }
}
