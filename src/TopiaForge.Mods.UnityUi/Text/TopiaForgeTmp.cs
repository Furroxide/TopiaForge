using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Single creation point for kit TMP labels.</summary>
    internal static class TopiaForgeTmp
    {
        /// <summary>
        /// TextMeshProUGUI only flips m_isOrthographic in Awake(), and all TMP measurement
        /// (GetPreferredValues, preferred sizes) scales glyph metrics by 0.1 while it is false.
        /// Kit UI is routinely built and measured under a still-inactive window, so every label
        /// must set it explicitly at creation — create all kit TMP components through here.
        /// </summary>
        internal static TextMeshProUGUI Create(GameObject go)
        {
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.isOrthographic = true;
            if (go.GetComponentInParent<TopiaForgePlainTextRoot>(true) != null)
            {
                tmp.richText = false;
                tmp.parseCtrlCharacters = false;
            }
            // Awake() re-applies TMP's project defaults, extra padding and raycast target among them,
            // while the size still holds TMP's unset marker. Under an inactive window Awake runs only
            // later, so set a kit size first; callers then set the size of their style.
            tmp.fontSize = TopiaForgeTokens.BodySize;
            // TMP sizes each glyph quad to the glyph's outline plus a small material padding (1.25
            // texels of the 90-point atlas). Quicksand's stems are 0.04 em in Light and 0.08 em in
            // Medium, so at small sizes a thin glyph such as l or I can get a quad narrower than a
            // pixel. The GPU skips a quad that covers no pixel centre, which drops whole letters, and
            // a narrow quad also cuts off the anti-aliased edge. Extra padding widens every quad to
            // the atlas's full distance range.
            tmp.extraPadding = true;
            tmp.raycastTarget = false;
            return tmp;
        }
    }

    /// <summary>Marks a received UI subtree, including controls or pooled rows created later, as literal text.</summary>
    internal sealed class TopiaForgePlainTextRoot : MonoBehaviour { }
}
