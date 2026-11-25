namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeTooltipTests
    {
        private const string TooltipKitBoundary = """
            using System;
            using TMPro;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace TopiaForge.Mods.UnityUi
            {
                internal class TopiaForgeWidget
                {
                    internal TopiaForgeWidget(GameObject go) => Go = go;
                    internal GameObject Go { get; }
                    internal RectTransform Rect => Go.transform;
                    internal UiHost Host { get; } = new();
                    internal TopiaForgeResolvedTheme ResolvedTheme { get; set; } = new(UiThemePackParser.Default);
                    internal LayoutElement EnsureLayoutElement() => TopiaForgeComponents.GetOrAdd<LayoutElement>(Go);
                }
                internal sealed class UiHost
                {
                    internal string? OwnerId => null;
                    internal float EffectiveUiScale { get; set; } = 1f;
                }
                internal sealed class TopiaForgeModalInstance : ITopiaForgeDismissable
                {
                    internal string? OwnerId => null;
                    internal bool IsTrust { get; init; }
                    public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Modal;
                    public void Dismiss() { }
                }
                internal sealed class TopiaForgeResolvedTheme
                {
                    private readonly UiThemeScheme scheme;
                    internal TopiaForgeResolvedTheme(UiThemeDescriptor descriptor, bool highContrast = false)
                    {
                        Descriptor = descriptor;
                        HighContrast = highContrast;
                        scheme = descriptor.SelectScheme(hud: false, highContrast);
                    }
                    internal UiThemeDescriptor Descriptor { get; }
                    internal bool HighContrast { get; }
                    internal Color SurfaceAlt => Encode(scheme.GetPair(UiPaintRole.Raised).Fill);
                    internal Color ForegroundOn(UiPaintRole role) => Encode(scheme.ResolveForeground(UiTone.Neutral, role));
                    internal Color BoundaryOn(UiPaintRole role) => Encode(scheme.ResolveBoundary(role));
                    internal Color Shadow => new(1);
                    internal Color ShadowStrong => new(2);
                    private static Color Encode(UiThemeColor rgb) => new(rgb.R << 16 | rgb.G << 8 | rgb.B);
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? Radius { get; private set; }
                    internal float Stroke { get; private set; }
                    internal void SetShape(float? radius, float stroke = 0f)
                    {
                        Radius = radius;
                        Stroke = stroke;
                    }
                }
                internal enum TopiaForgeTextStyle { Label }
                internal static class TopiaForgeTokens
                {
                    internal const float ControlHeight = 40f;
                    internal static int SizeOf(TopiaForgeTextStyle style) => 18;
                }
                internal static class TopiaForgeTmp
                {
                    internal static TextMeshProUGUI Create(GameObject go) => go.AddComponent<TextMeshProUGUI>();
                }
                internal static class TopiaForgeFonts
                {
                    internal static readonly TMP_FontAsset Fallback = new();
                    internal static TMP_FontAsset For(TopiaForgeTextStyle style) => Fallback;
                    internal static bool UseFauxBold => true;
                }
                internal static class TopiaForgeTypography
                {
                    internal static readonly TMP_FontAsset Scoped = new();
                    internal static TopiaForgeResolvedTheme? LastTheme;
                    internal static TopiaForgeTextStyle LastStyle;
                    internal static void ApplyControlText(TextMeshProUGUI label, TopiaForgeResolvedTheme theme,
                        TopiaForgeTextStyle style, float scale)
                    {
                        LastTheme = theme;
                        LastStyle = style;
                        label.font = Scoped;
                    }
                }
                internal static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform rect, float left = 0f, float top = 0f,
                        float right = 0f, float bottom = 0f) { }
                }
                internal static class TopiaForgeControlFocus
                {
                    internal static int Installed;
                    internal static void Install(TopiaForgeWidget widget, Selectable target, UiPaintRole role) => Installed++;
                }
                internal static class TopiaForgeComponents
                {
                    internal static T GetOrAdd<T>(GameObject go) where T : Component => go.GetComponent<T>() ?? go.AddComponent<T>();
                }
                internal static class TopiaForgeRuntime { internal static void Ensure() { } }
                internal static class TopiaForgeInput
                {
                    internal static bool Back;
                    internal static bool BackHeld() => Back;
                    internal static bool BackPressedThisFrame() => Back;
                }
                internal static class TopiaForgeInputOwnership
                {
                    internal static bool IsLiveRoot(object root) => root is GameObject go && go != null && go.activeInHierarchy;
                    internal static bool HoldsSelection(object root)
                    {
                        if (root is not GameObject go) return false;
                        for (var at = EventSystem.current?.currentSelectedGameObject?.transform as Transform; at != null; at = at.parent)
                            if (at.gameObject == go) return true;
                        return false;
                    }
                }
                internal static class TopiaForgeIntent
                {
                    internal static Scope PlayerAction(string? owner) => default;
                    internal readonly struct Scope : IDisposable { public void Dispose() { } }
                }
                internal static class TopiaForgeLog
                {
                    internal static void Error(Exception exception, string message) => throw new InvalidOperationException(message, exception);
                }
            }
            """;
    }
}
