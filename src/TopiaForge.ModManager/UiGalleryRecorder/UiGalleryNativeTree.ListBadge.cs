using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool ListBadge(string path, RectTransform row, string authored, UiThemeColor? rowFill, UiCaptureNodeFlags flags)
        {
            if (row.Find("RowBadge") is not RectTransform badge || badge.GetComponent<Image>() is not { } fill ||
                badge.Find("Ring")?.GetComponent<Image>() is not { } ring) return false;
            if (!Text(path, null, "ListItemBadge", Named<TMP_Text>(badge, "Label"), authored, UiTextStyle.Label,
                    Over(fill, rowFill), flags, Decoration(badge, ring))) return false;
            var edge = Decoration(badge, ring);
            Add(path + "/boundary", null, "ListItemBadgeBoundary", ring.rectTransform,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.SemanticMark),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(ring)), rowFill, edge.CornerRadius, edge.BorderWidth),
                ring.isActiveAndEnabled);
            return true;
        }
    }
}
