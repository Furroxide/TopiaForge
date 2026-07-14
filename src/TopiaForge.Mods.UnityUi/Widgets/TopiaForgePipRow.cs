using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A row of charge pips (the Zombies uplink pattern). SetCount rebuilds only when
    /// the count changes; SetFilled dirty-checks per pip, with the next-charging pip
    /// breathing in via regen alpha (0.28 + 0.5 * regen — ported behavior).
    /// </summary>
    public sealed class TopiaForgePipRow : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float PipSize = 12f;

        private readonly HorizontalLayoutGroup layout;
        private readonly List<UImage> pips = new List<UImage>();
        private int filledCount = -1;
        private float lastRegen = -1f;
        private TopiaForgeTone filledTone = TopiaForgeTone.Accent;

        internal TopiaForgePipRow(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("PipRow"))
        {
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            layout = TopiaForgeComponents.GetOrAdd<HorizontalLayoutGroup>(Go);
            this.FixedHeight(PipSize + 2f);
        }

        /// <summary>Centers the complete pip group within the row.</summary>
        public TopiaForgePipRow AlignCenter()
        {
            layout.childAlignment = TextAnchor.MiddleCenter;
            return this;
        }

        public TopiaForgePipRow Tone(TopiaForgeTone value)
        {
            filledTone = value;
            Repaint();
            return this;
        }

        /// <summary>Rebuilds pips only when the count actually changes.</summary>
        public void SetCount(int count)
        {
            if (count == pips.Count)
            {
                return;
            }

            for (var index = pips.Count - 1; index >= 0; index--)
            {
                Object.Destroy(pips[index].gameObject);
            }

            pips.Clear();
            for (var index = 0; index < count; index++)
            {
                var go = new GameObject("Pip" + index, typeof(RectTransform));
                go.transform.SetParent(Go.transform, false);
                var image = go.AddComponent<UImage>();
                image.sprite = TopiaForgeSprites.Circle();
                image.raycastTarget = false;
                var layout = go.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.minWidth = PipSize;
                layout.preferredWidth = PipSize;
                layout.minHeight = PipSize;
                layout.preferredHeight = PipSize;
                pips.Add(image);
            }

            filledCount = -1;
            lastRegen = -1f;
        }

        /// <summary>
        /// Dirty-checked fill state; the pip after the last filled one shows regen
        /// progress via alpha.
        /// </summary>
        public void SetFilled(int filled, float regenFraction = 0f)
        {
            filled = Mathf.Clamp(filled, 0, pips.Count);
            regenFraction = Mathf.Clamp01(regenFraction);
            if (filled == filledCount && Mathf.Abs(regenFraction - lastRegen) <= 0.01f)
            {
                return;
            }

            filledCount = filled;
            lastRegen = regenFraction;
            Repaint();
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            Repaint();
        }

        private void Repaint()
        {
            if (filledCount < 0)
            {
                return;
            }

            var theme = Theme;
            var filledColor = theme.ToneColor(filledTone);
            for (var index = 0; index < pips.Count; index++)
            {
                if (index < filledCount)
                {
                    pips[index].color = filledColor;
                }
                else if (index == filledCount && lastRegen > 0f)
                {
                    var regenColor = filledColor;
                    regenColor.a = 0.28f + (0.5f * lastRegen);
                    pips[index].color = regenColor;
                }
                else
                {
                    pips[index].color = theme.Tint;
                }
            }
        }
    }
}
