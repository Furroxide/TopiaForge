using System;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    internal readonly struct TopiaForgeTypographyMetrics
    {
        private TopiaForgeTypographyMetrics(float pointSize, float characterSpacing, bool uppercase)
        {
            PointSize = pointSize;
            CharacterSpacing = characterSpacing;
            Uppercase = uppercase;
        }

        internal float PointSize { get; }
        internal float CharacterSpacing { get; }
        internal bool Uppercase { get; }

        internal static TopiaForgeTypographyMetrics Resolve(UiThemeTextRole role, float uiScale, float faceSize,
            float ascender, float descender, float faceScale)
        {
            var requestedHeight = role.ReferenceSize * Math.Max(1f, uiScale) / uiScale;
            var faceHeight = (ascender - descender) * faceScale;
            var pointSize = float.IsFinite(faceSize) && float.IsFinite(faceHeight) && faceHeight > 0f && faceSize > 0f
                ? requestedHeight * faceSize / faceHeight : requestedHeight;
            return new TopiaForgeTypographyMetrics(pointSize, role.TrackingEm * 100f, role.IsUppercase);
        }
    }
}
