using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface
        {
            private float? savedOuterScrollOffset;

            private Rect OuterScrollViewport => outerScroll != null && outerScroll.viewport != null
                ? outerScroll.viewport.rect : default;

            private UiLayoutConstraints MeasureScrollingContent(TopiaForgeContainer parent, double width, VerticalLayoutGroup? group)
            {
                if (outerScroll == null || !outerScroll.vertical || outerScroll.content != parent.Rect ||
                    outerScroll.viewport == null || width <= 0d)
                    throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/0", "The surface scroll viewport is unavailable.");

                var prefix = (double)(group?.padding.top ?? 0);
                if (body != null) prefix += PreferredSize(body, width).y + (group?.spacing ?? 0f);
                return UiLayoutConstraints.ForVerticalScroll(width, Math.Max(0f, OuterScrollViewport.height),
                    prefix, group?.padding.bottom ?? 0);
            }

            private void RememberOuterScrollOffset()
            {
                if (outerScroll == null || outerScroll.content == null || savedOuterScrollOffset.HasValue) return;
                var content = outerScroll.content;
                var position = content.anchoredPosition;
                savedOuterScrollOffset = Math.Max(0f, position.y);
                outerScroll.StopMovement();
                content.anchoredPosition = new Vector2(position.x, 0f);
            }

            private void RestoreOuterScrollOffset()
            {
                if (savedOuterScrollOffset is not float offset || outerScroll == null || outerScroll.content == null ||
                    outerScroll.viewport == null) return;
                savedOuterScrollOffset = null;
                try
                {
                    var content = outerScroll.content;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                    var limit = Math.Max(0f, content.rect.height - OuterScrollViewport.height);
                    outerScroll.StopMovement();
                    content.anchoredPosition = new Vector2(content.anchoredPosition.x, Math.Clamp(offset, 0f, limit));
                }
                catch (Exception error)
                {
                    try { logger.Error(error, "TopiaForgeUi could not restore the surface scroll position."); }
                    catch { }
                }
            }
        }
    }
}
