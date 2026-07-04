using TMPro;
using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Single creation point for kit TMP labels.</summary>
    internal static class QwTmp
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
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}
