namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string NativeScrollCases = """
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using UnityEngine;
            using UnityEngine.UI;

            public static partial class NativeClipCases
            {
                private static void ScrollAperturesMustRevealNativeGeometry(Canvas canvas, RectTransform window)
                {
                    var tooShort = Scroll(window, 100, 150, 200, 30, 400);
                    var impossible = HitIn(canvas, tooShort, 245);
                    Check(Clipped(impossible) && !impossible.Nodes.Single().IsRendered,
                        "A 30-pixel viewport cannot excuse an offscreen 40-pixel hit rectangle.");

                    var exactly = Scroll(window, 100, 150, 200, 40, 400);
                    var reachable = HitIn(canvas, exactly, 245);
                    Check(!Clipped(reachable) && reachable.Nodes.Single() is { IsRendered: false, Clipped: true, Height: 40 },
                        "A 40-pixel viewport can reveal its whole offscreen 40-pixel hit rectangle.");

                    var alreadyScrolled = Scroll(window, 100, 260, 200, 40, 400, offset: 60);
                    Check(!Clipped(HitIn(canvas, alreadyScrolled, 213)),
                        "Legal scroll travel can also return to a hit above the current viewport.");
                    var limitedTravel = Scroll(window, 100, 200, 200, 60, 80);
                    limitedTravel.parent!.GetComponent<RectMask2D>()!.padding = new Vector4(0, 20, 0, 0);
                    Check(Clipped(HitIn(canvas, limitedTravel, 239)),
                        "A padded viewport and insufficient scroll travel can prevent a nominally fitting hit from being revealed.");

                    var outer = Scroll(window, 110, 210, 250, 80, 500);
                    var fixedMask = Mask(outer, 120, 240, 215, 30);
                    var nested = Scroll(fixedMask, 125, 240, 200, 80, 600);
                    Check(Clipped(HitIn(canvas, nested, 360)),
                        "Both scrolls together cannot reveal a hit through an inner fixed 30-pixel mask.");
                    var viableMask = Mask(outer, 120, 240, 215, 40);
                    var viableNested = Scroll(viableMask, 125, 240, 200, 80, 600);
                    Check(!Clipped(HitIn(canvas, viableNested, 360)),
                        "An inner fixed 40-pixel mask and both scrolls can reveal the reachable hit.");

                    var activeFocus = At(exactly, 132, 145, 110, 50);
                    Check(Clipped(new UiGalleryNativeTree(canvas).ReadFocus(activeFocus)),
                        "The recorder flags a focused paint envelope clipped by its scroll viewport.");

                    var oneLine = Scroll(window, 100, 200, 250, 20, 400);
                    var transcript = At(oneLine, 130, 250, 100, 100).Attach(new TMP_Text());
                    var transcriptCapture = new UiGalleryNativeTree(canvas).ReadText(transcript, passive: true);
                    Check(!Clipped(transcriptCapture) && transcriptCapture.Nodes.Single().Clipped,
                        "A multiline label can be revealed one complete line at a time.");
                    var belowLineFloor = Scroll(window, 100, 200, 250, 19, 400);
                    var cutLine = At(belowLineFloor, 130, 250, 100, 100).Attach(new TMP_Text());
                    Check(Clipped(new UiGalleryNativeTree(canvas).ReadText(cutLine, passive: true)),
                        "A multiline label needs room for one complete measured line.");
                }

                private static UiTreeCapture HitIn(Canvas canvas, RectTransform content, float top)
                {
                    var rect = At(content, 150, top, 100, 40);
                    var button = rect.Attach(new Selectable());
                    rect.Attach(new Image(canvas));
                    return Hits(canvas, button);
                }
            }
            """;
    }
}
