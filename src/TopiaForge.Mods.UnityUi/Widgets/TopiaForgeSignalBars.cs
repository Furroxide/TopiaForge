using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Connection-quality glyph: four rising bars toned by strength, with an optional ping label.
    /// The unknown state draws four flat stubs, so it differs from "no bars" by shape as well as
    /// tone. The mapping lives in the unit-tested Core (<see cref="TopiaForgeSignalMath"/>), and every
    /// setter dirty-checks, so per-frame updates allocate nothing while the reading is unchanged.
    /// </summary>
    public sealed class TopiaForgeSignalBars : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float GlyphWidth = 20f;
        private const float GlyphHeight = 14f;
        private const float LabelWidth = 50f;
        private const float UnknownStub = 0.22f;
        private const string MillisecondsFormat = "0' ms'";

        private static readonly float[] Heights = { 0.4f, 0.6f, 0.8f, 1f };
        private static readonly string UnknownText = new string((char)0x2014, 1);

        private readonly UImage[] bars = new UImage[TopiaForgeSignalMath.MaxBars];
        private readonly RectTransform[] barRects = new RectTransform[TopiaForgeSignalMath.MaxBars];
        private readonly TopiaForgeContainer content;
        private TopiaForgeLabel? label;
        private TopiaForgeSignalThresholds thresholds = TopiaForgeSignalThresholds.Default;
        private int shownBars = int.MinValue;
        private int ping = int.MinValue;
        private bool stubs;

        internal TopiaForgeSignalBars(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("SignalBars"))
        {
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            content = new TopiaForgeContainer(Host, Scheme, Go);

            var glyph = new GameObject("Glyph", typeof(RectTransform));
            glyph.transform.SetParent(Go.transform, false);
            var glyphLayout = glyph.AddComponent<LayoutElement>();
            glyphLayout.minWidth = GlyphWidth;
            glyphLayout.preferredWidth = GlyphWidth;
            glyphLayout.minHeight = GlyphHeight;
            glyphLayout.preferredHeight = GlyphHeight;

            for (var index = 0; index < bars.Length; index++)
            {
                var barGo = new GameObject("Bar" + index, typeof(RectTransform));
                barGo.transform.SetParent(glyph.transform, false);
                var bar = barGo.AddComponent<UImage>();
                bar.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Bar);
                bar.type = UImage.Type.Sliced;
                bar.raycastTarget = false;
                var rect = (RectTransform)barGo.transform;
                rect.anchorMin = new Vector2(index / (float)bars.Length, 0f);
                rect.anchorMax = new Vector2((index + 1) / (float)bars.Length, Heights[index]);
                rect.offsetMin = new Vector2(1f, 0f);
                rect.offsetMax = new Vector2(-1f, 0f);
                bars[index] = bar;
                barRects[index] = rect;
            }

            this.FixedHeight(GlyphHeight + 4f);
            ApplyBars(TopiaForgeSignalMath.Unknown);
        }

        /// <summary>Gets the bars shown, or <see cref="TopiaForgeSignalMath.Unknown"/>.</summary>
        public int Bars => shownBars;

        /// <summary>Adds (or removes) the ping label beside the glyph ("42 ms").</summary>
        public TopiaForgeSignalBars ShowMilliseconds(bool show = true)
        {
            if (show && label == null)
            {
                label = content.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap().FixedWidth(LabelWidth);
                WriteLabel();
            }

            if (label != null && label.Go.activeSelf != show)
            {
                label.Go.SetActive(show);
            }

            return this;
        }

        /// <summary>Uses custom ping ceilings and re-evaluates the current ping.</summary>
        public TopiaForgeSignalBars Thresholds(TopiaForgeSignalThresholds value)
        {
            thresholds = value;
            if (ping >= 0)
            {
                ApplyBars(TopiaForgeSignalMath.BarsForPing(ping, thresholds));
            }

            return this;
        }

        /// <summary>Dirty-checked round-trip time; a negative ping shows the unknown state.</summary>
        public void SetPing(int milliseconds)
        {
            if (milliseconds < 0)
            {
                milliseconds = -1;
            }

            if (milliseconds == ping)
            {
                return;
            }

            ping = milliseconds;
            ApplyBars(TopiaForgeSignalMath.BarsForPing(milliseconds, thresholds));
            WriteLabel();
        }

        /// <summary>Dirty-checked normalized quality (0 unusable, 1 perfect); leaves the ping label alone.</summary>
        public void SetQuality(float quality)
        {
            ApplyBars(TopiaForgeSignalMath.BarsForQuality(quality));
        }

        /// <summary>Dirty-checked bucketed level (0 worst, <paramref name="maxLevel"/> best); leaves the ping label alone.</summary>
        public void SetLevel(int level, int maxLevel)
        {
            ApplyBars(TopiaForgeSignalMath.BarsForLevel(level, maxLevel));
        }

        /// <summary>Dirty-checked raw bar count (0 to 4, negative for unknown); leaves the ping label alone.</summary>
        public void SetBars(int value)
        {
            ApplyBars(TopiaForgeSignalMath.Clamp(value));
        }

        /// <summary>Shows the unknown state on both the glyph and the ping label.</summary>
        public void SetUnknown()
        {
            SetPing(-1);
            ApplyBars(TopiaForgeSignalMath.Unknown);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            Repaint(theme);
        }

        private void ApplyBars(int value)
        {
            if (value == shownBars)
            {
                return;
            }

            shownBars = value;
            Repaint(Theme);
        }

        private void WriteLabel()
        {
            if (label == null)
            {
                return;
            }

            if (ping < 0)
            {
                label.SetText(UnknownText);
            }
            else
            {
                label.SetNumber(string.Empty, ping, MillisecondsFormat);
            }
        }

        private void Repaint(TopiaForgeResolvedTheme theme)
        {
            var unknown = shownBars < 0;
            if (stubs != unknown)
            {
                stubs = unknown;
                for (var index = 0; index < barRects.Length; index++)
                {
                    var anchor = barRects[index].anchorMax;
                    anchor.y = unknown ? UnknownStub : Heights[index];
                    barRects[index].anchorMax = anchor;
                }
            }

            var lit = theme.ToneColor(ToneFor(shownBars));
            var empty = theme.Tint;
            for (var index = 0; index < bars.Length; index++)
            {
                bars[index].color = !unknown && index < shownBars ? lit : empty;
            }
        }

        private static TopiaForgeTone ToneFor(int value)
        {
            if (value < 0)
            {
                return TopiaForgeTone.Faint;
            }

            return value >= 3 ? TopiaForgeTone.Success : value == 2 ? TopiaForgeTone.Warning : TopiaForgeTone.Danger;
        }
    }
}
