namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiToastNativeTests
    {
        private const string OverflowScenarios = """"
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TMPro;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;

            namespace TopiaForge.ModManager
            {
                internal static partial class ToastProbe
                {
                    private static void QueuesAboveATallHud()
                    {
                        const float Reserve = .12f;
                        var dock = new TopiaForgeHudDock();
                        var hud = dock.Add(TopiaForgeCorner.BottomRight, 360f, 2f);
                        dock.SetShown(hud, true);
                        TopiaForgeToasts.SetBottomRightHudTop(dock.GetShownTop(TopiaForgeCorner.BottomRight), Reserve);
                        var viewer = new UiHost("overflow")
                        {
                            Profile = new TopiaForgeAccessibilityProfile(uiScale: 2f, reducedMotion: true),
                        };
                        var observed = new List<string>();
                        foreach (var text in new[] { "First", "Second", "Third", "Fourth" })
                            TopiaForgeToasts.ShowPlainObserved(text, TopiaForgeTone.Success, viewer, (view, _) =>
                            {
                                if (view.Go.activeSelf) observed.Add(text);
                                return null;
                            });
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        var visible = canvas.transform.Children.Where(child => child.activeSelf).ToArray();
                        Check(visible.Length == 2 && visible.All(child =>
                            child.transform.anchoredPosition.y + child.transform.sizeDelta.y * child.transform.localScale.y <= Screen.height) &&
                            observed.SequenceEqual(new[] { "First", "Second" }),
                            "Only two toasts fit above the tall bottom-right HUD. Native positions and heights: " +
                            string.Join(", ", visible.Select(child => child.transform.anchoredPosition.y + "+" +
                                (child.transform.sizeDelta.y * child.transform.localScale.y))));
                        TopiaForgeToasts.Tick(3.5f);
                        Check(observed.SequenceEqual(new[] { "First", "Second", "Third", "Fourth" }) &&
                            canvas.transform.Children.Count(child => child.activeSelf) == 2,
                            "The pending pair starts presenting only after the first pair's duration.");
                        TopiaForgeToasts.Tick(3.49f);
                        Check(canvas.transform.Children.Count(child => child.activeSelf) == 2,
                            "The pending pair keeps a full visible duration.");
                        TopiaForgeToasts.Tick(.02f);
                        Check(!canvas.transform.Children.Any(child => child.activeSelf), "The promoted toasts retire after their own duration.");
                        TopiaForgeToasts.Reset();
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                    }

                    private static void ResumesVisibleToastsAfterViewportGrowth()
                    {
                        var viewer = new UiHost("suspension") { Profile = new TopiaForgeAccessibilityProfile(
                            highContrast: true, uiScale: 2f, reducedMotion: true) };
                        TopiaForgeToasts.ShowPlain("First", TopiaForgeTone.Neutral, viewer, 30f);
                        TopiaForgeToasts.ShowPlain("Second", TopiaForgeTone.Neutral, viewer, 30f);
                        Func<TopiaForgeToasts.ToastPaint?>? original = null;
                        var starts = 0;
                        var releases = 0;
                        foreach (var title in new[] { "Third", "Fourth" })
                            TopiaForgeToasts.ShowPlainObserved(title, TopiaForgeTone.Warning, viewer, (_, read) =>
                            {
                                starts++;
                                original ??= read;
                                return () => releases++;
                            });
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        Check(starts == 2 && Labels(canvas).Length == 4, "Both original observation leases start when their views fit.");
                        TopiaForgeToasts.Tick(1f);
                        TopiaForgeToasts.SetBottomRightHudTop(738f, .12f);
                        TopiaForgeToasts.Tick(0f);
                        Check(Labels(canvas).SequenceEqual(new[] { "First", "Second" }) &&
                            starts == 2 && releases == 2 && original!() == null,
                            "Growing the dock hides only the non-fitting views and revokes their old observation leases.");
                        TopiaForgeToasts.Tick(10f);
                        Screen.height = 1800;
                        try
                        {
                            TopiaForgeToasts.Tick(0f);
                            Check(Labels(canvas).SequenceEqual(new[] { "First", "Second", "Third", "Fourth" }) &&
                                starts == 4 && releases == 2 && original!() == null &&
                                canvas.transform.Children.All(child => child.Group.alpha == 1f),
                                "Viewport growth restores both retained views, full opacity, and fresh observation leases.");
                            TopiaForgeToasts.Tick(2.49f);
                            Check(Labels(canvas).Length == 4, "Neither retained toast ages during the ten hidden seconds.");
                            TopiaForgeToasts.Tick(.02f);
                            Check(Labels(canvas).SequenceEqual(new[] { "First", "Second" }) && releases == 4,
                                "Restored toasts retain precisely their remaining duration.");
                        }
                        finally
                        {
                            Screen.height = 1080;
                            TopiaForgeToasts.Reset();
                            TopiaForgeToasts.SetBottomRightHudTop(0f);
                        }
                    }

                    private static void PendingQueueStaysBoundedAndIdle()
                    {
                        var viewer = new UiHost("bounded") { Profile = new TopiaForgeAccessibilityProfile(
                            uiScale: 2f, reducedMotion: true) };
                        TopiaForgeToasts.SetBottomRightHudTop(1000f, .12f);
                        for (var index = 0; index <= 64; index++)
                            TopiaForgeToasts.ShowPlain("Waiting " + index, TopiaForgeTone.Neutral, viewer);
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        Check(Labels(canvas).Length == 0, "A fully occupied viewport keeps every queued view hidden.");
                        var idle = AllocationProbe.Measure("native viewport-blocked toast tick",
                            static () => TopiaForgeToasts.Tick(.001f), frames: 10_000, warmups: 128);
                        Check(idle == 0, "A viewport-blocked queue allocates zero bytes while waiting.");
                        Console.WriteLine("Native viewport-blocked toast queue: " + idle + " bytes across 10,000 settled ticks.");
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                        TopiaForgeToasts.Tick(0f);
                        Check(Labels(canvas).SequenceEqual(new[] { "Waiting 1", "Waiting 2", "Waiting 3", "Waiting 4" }),
                            "Releasing the dock promotes the FIFO head after dropping only the oldest of 65 pending offers.");
                        TopiaForgeToasts.Reset();
                    }

                    private static void PendingViewerScaleCanOpenSpace()
                    {
                        var viewer = new UiHost("scaling") { Profile = new TopiaForgeAccessibilityProfile(
                            uiScale: 2f, reducedMotion: true) };
                        TopiaForgeToasts.SetBottomRightHudTop(870f, .12f);
                        TopiaForgeToasts.ShowPlain("On hold", TopiaForgeTone.Neutral, viewer);
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        Check(Labels(canvas).Length == 0, "The initial pending viewer's scale needs more vertical room.");
                        viewer.Profile = new TopiaForgeAccessibilityProfile(uiScale: 1f, reducedMotion: true);
                        TopiaForgeToasts.Tick(0f);
                        Check(Labels(canvas).SequenceEqual(new[] { "On hold" }),
                            "A change in only the pending viewer's scale retries admission.");
                        TopiaForgeToasts.Reset();
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                    }

                    private static void OutdatedReflowCannotMoveBehindHud()
                    {
                        TopiaForgeToasts.SetBottomRightHudTop(650f, .12f);
                        TopiaForgeToasts.ShowPlain("Keep above dock", duration: 10f);
                        TopiaForgeTween.Tick(1f);
                        var toast = TopiaForgeUi.LastHost.LayerRoot.transform.Children[0];
                        var firstY = toast.transform.anchoredPosition.y;
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                        TopiaForgeToasts.Tick(0f);
                        Check(TopiaForgeTween.ActiveCount != 0, "Moving a still-fitting toast down can animate.");
                        TopiaForgeToasts.SetBottomRightHudTop(650f, .12f);
                        TopiaForgeToasts.Tick(0f);
                        TopiaForgeTween.Tick(1f);
                        Check(toast.transform.anchoredPosition.y == firstY && toast.Group.alpha == 1f,
                            "A stale reflow tween cannot move the retained toast behind the restored HUD.");
                        TopiaForgeToasts.Reset();
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                    }

                    private static void PendingColumnUsesMeasuredTextHeight()
                    {
                        const string ThreeLines = "First line\nSecond line\nThird line";
                        var viewer = new UiHost("measured") { Profile = new TopiaForgeAccessibilityProfile(
                            uiScale: 2f, reducedMotion: true) };
                        TopiaForgeToasts.SetBottomRightHudTop(810f, .12f);
                        TopiaForgeToasts.ShowPlain(ThreeLines, TopiaForgeTone.Neutral, viewer);
                        TopiaForgeToasts.ShowPlain("Short", TopiaForgeTone.Neutral, viewer);
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        Check(Labels(canvas).Length == 0, "A measured three-line toast waits even though a later short toast alone could fit.");
                        TopiaForgeToasts.SetBottomRightHudTop(760f, .12f);
                        TopiaForgeToasts.Tick(0f);
                        Check(Labels(canvas).SequenceEqual(new[] { ThreeLines }) &&
                            canvas.transform.Children[0].transform.sizeDelta.y == 84f,
                            "The first toast keeps all three lines and the single FIFO holds the shorter follower.");
                        TopiaForgeToasts.Reset();
                        TopiaForgeToasts.SetBottomRightHudTop(0f);
                    }

                    private static string[] Labels(GameObject canvas) => canvas.transform.Children
                        .Where(child => child.activeSelf)
                        .Select(child => child.Find("Label").GetComponent<TextMeshProUGUI>().text).ToArray();
                }
            }
            """";
    }
}
