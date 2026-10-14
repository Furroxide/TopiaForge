using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitMultiplayerCoreTests
    {
        private static void IconRasterDrawsEveryIcon()
        {
            const int size = 24;
            var icons = (TopiaForgeIcon[])Enum.GetValues(typeof(TopiaForgeIcon));
            var rasters = new float[icons.Length][];
            for (var icon = 0; icon < icons.Length; icon++)
            {
                var pixels = new float[size * size];
                var covered = 0;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var coverage = TopiaForgeIconRaster.Coverage(icons[icon], x + 0.5f, y + 0.5f, size);
                        Assert(coverage >= 0f && coverage <= 1f, icons[icon] + " coverage stays in [0, 1]");
                        pixels[(y * size) + x] = coverage;
                        if (coverage > 0.5f)
                        {
                            covered++;
                        }
                    }
                }

                Assert(covered >= 12, icons[icon] + " draws a visible glyph (" + covered + " solid pixels)");
                Assert(covered <= (size * size) / 2, icons[icon] + " is a glyph, not a filled tile (" + covered + " solid pixels)");
                rasters[icon] = pixels;
            }

            var microphone = rasters[Array.IndexOf(icons, TopiaForgeIcon.Microphone)];
            var microphoneOff = rasters[Array.IndexOf(icons, TopiaForgeIcon.MicrophoneOff)];
            var differing = 0;
            for (var index = 0; index < microphone.Length; index++)
            {
                if (Math.Abs(microphone[index] - microphoneOff[index]) > 0.5f)
                {
                    differing++;
                }
            }

            Assert(differing >= 12, "the muted microphone differs visibly from the live one (" + differing + " pixels)");
            Assert(microphone[(20 * size) + 12] > 0.5f, "the microphone body sits in the upper half");
            Assert(microphone[(4 * size) + 12] > 0.5f, "the microphone base sits at the bottom");
        }

        private static void SteadyStatePathsAllocateNothing()
        {
            var falloff = TopiaForgeNametagFalloff.Default;
            var viewport = new TopiaForgeViewport(0f, 0f, 1920f, 1080f);
            var frame = CameraAt(0f, 1.6f, 0f, viewport);
            var hold = new TopiaForgePeakHold();
            var stepper = new TopiaForgePhaseStepperState(7);
            var log = new TopiaForgeChatLogModel(capacity: 16);
            for (var index = 0; index < 16; index++)
            {
                log.TryAppend("warm line " + index, TopiaForgeChatLineKind.Player, index, out _);
            }

            var keys = new int[64];
            for (var index = 0; index < keys.Length; index++)
            {
                keys[index] = (index * 37) % 11;
            }

            var sorter = new TopiaForgeStableSorter();
            IReadOnlyList<int> rows = keys;
            var comparer = Comparer<int>.Default;
            const string name = "Player One";
            var sink = 0f;

            void Frame(int tick)
            {
                var time = tick * 0.016f;
                sink += TopiaForgePlainText.Sanitize(name, 32).Length;
                sink += TopiaForgeSignalMath.BarsForPing(tick % 400);
                sink += TopiaForgeSignalMath.BarsForQuality((tick % 10) / 10f);
                var decibels = TopiaForgeLevelMath.ToDecibels(0.2f + (0.1f * (tick % 5)));
                sink += TopiaForgeLevelMath.ToFraction(decibels, -60f, 0f);
                sink += (int)TopiaForgeLevelMath.Zone(decibels, -40f, -6f, -1f);
                sink += TopiaForgeLevelMath.Release(0.5f, 0.2f, 0.016f, 0.4f);
                hold.Update((tick % 7) / 7f, 0.016f);
                TopiaForgeBreath.Evaluate(time, 1f, out var scale, out var alpha);
                sink += scale + alpha + TopiaForgeTalkPresentation.For(TopiaForgeTalkState.Talking).Caption.Length;
                var point = frame.Project(tick % 9, 1.8f, 12f);
                var placement = TopiaForgeNametagMath.Place(point, frame.DistanceTo(tick % 9, 1.8f, 12f), viewport, falloff);
                sink += placement.Alpha + placement.Scale;
                sink += TopiaForgeChatFade.Alpha(time, 8f, 1.5f, false);
                sink += log.CountVisibleWhenClosed(20f, 8f, 1.5f, 8);
                sink += log[tick % log.Count].Text.Length;
                stepper.MoveTo(tick % 7);
                stepper.SetProgress(5, tick % 7, 7);
                sorter.Sort(rows, comparer, (tick & 1) == 0 ? TopiaForgeSortDirection.Ascending : TopiaForgeSortDirection.Descending);
                sink += sorter.SourceIndexAt(tick % keys.Length) + sorter.ViewIndexOf(3);
            }

            for (var warm = 0; warm < 32; warm++)
            {
                Frame(warm);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var tick = 0; tick < 2000; tick++)
            {
                Frame(tick);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            AssertEqual(allocated, 0L, "per-frame Core paths allocate nothing (sink " + sink + ")");
        }
    }
}
