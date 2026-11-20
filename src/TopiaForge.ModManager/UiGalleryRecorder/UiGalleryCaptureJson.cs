using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal static class UiGalleryCaptureJson
    {
        private static readonly Lazy<string?> BrandDigest = new Lazy<string?>(ReadBrandDigest);

        internal static void Save(string ownerDirectory, string fixture, string viewer, UiTreeCapture tree,
            UiGalleryNativeTree native, UiWireEnvelope? envelope)
        {
            var json = new StringBuilder();
            json.Append('{');
            Field(json, "source", "Native Gallery resolved tree");
            Field(json, "fixture", fixture);
            Field(json, "viewer", viewer);
            Field(json, "delivery", envelope == null ? "local" : "authenticated-remote");
            Field(json, "gameVersion", Application.version);
            Field(json, "sdkVersion", typeof(UiNode).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            Field(json, "brandBundleSha256", BrandDigest.Value);
            Field(json, "packageSha256", envelope == null ? null : Hex(envelope.PackageDigest.Span));
            Field(json, "theme", native.Descriptor.Id);
            Field(json, "descriptorSha256", Hex(native.Descriptor.Digest.Span));
            Field(json, "scheme", native.Scheme.ToString());
            Field(json, "viewportWidth", native.Width);
            Field(json, "viewportHeight", native.Height);
            Field(json, "pixelsPerUnit", native.PixelsPerUnit);
            Field(json, "surfaceAlpha", native.SurfaceAlpha);
            Field(json, "highContrast", native.Accessibility.HighContrast);
            Field(json, "uiScale", native.Accessibility.UiScale);
            Field(json, "reducedMotion", native.Accessibility.ReducedMotion);
            Field(json, "motionIntensity", native.Accessibility.MotionIntensity);

            Key(json, "fonts").Append('[');
            foreach (var font in native.Fonts)
            {
                Entry(json);
                Field(json, "path", font.Path);
                Field(json, "family", font.Family);
                Field(json, "style", font.Style);
                Field(json, "sizePixels", font.SizePixels);
                Field(json, "ascentLine", font.Ascender);
                Field(json, "descentLine", font.Descender);
                Field(json, "measuredHeightPixels", font.MeasuredHeight);
                json.Append('}');
            }
            json.Append(']');

            Key(json, "nodes").Append('[');
            foreach (var node in tree.Nodes)
            {
                Entry(json);
                Field(json, "path", node.Path);
                Field(json, "id", node.Id);
                Field(json, "kind", node.Kind);
                Field(json, "text", node.Text);
                Field(json, "semanticLabel", node.SemanticLabel);
                Field(json, "decorative", node.Decorative);
                Field(json, "disabled", node.Disabled);
                Field(json, "selected", node.Selected);
                Field(json, "x", node.X);
                Field(json, "y", node.Y);
                Field(json, "width", node.Width);
                Field(json, "height", node.Height);
                Field(json, "textHeightPixels", node.TextHeightPixels);
                Field(json, "contrastRatio", node.ContrastRatio);
                Field(json, "themeId", node.ThemeId);
                Field(json, "styleId", node.StyleId);
                StatusStyle(json, node);
                Field(json, "inkColor", node.InkColor);
                Field(json, "fillColor", node.FillColor);
                Field(json, "cornerRadiusPixels", node.CornerRadiusPixels);
                Field(json, "borderWidthPixels", node.BorderWidthPixels);
                Field(json, "shadowOffsetXPixels", node.ShadowOffsetXPixels);
                Field(json, "shadowOffsetYPixels", node.ShadowOffsetYPixels);
                Field(json, "usedFallback", node.UsedFallback);
                Field(json, "isRendered", node.IsRendered);
                Field(json, "clipped", node.Clipped);
                Field(json, "occluded", node.Occluded);
                json.Append('}');
            }
            json.Append(']');

            Key(json, "issues").Append('[');
            foreach (var issue in tree.CheckAccessibility())
            {
                Entry(json);
                Field(json, "kind", issue.Kind.ToString());
                Field(json, "path", issue.Path);
                Field(json, "message", issue.Message);
                json.Append('}');
            }
            json.Append(']').Append('}');

            var captureId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture);
            var path = Path.Combine(ownerDirectory, "native-captures", fixture + "-" + captureId + "-" + Guid.NewGuid().ToString("N") + ".json");
            JsonUtil.SaveJsonObject(path, json.ToString());
        }

        private static void StatusStyle(StringBuilder json, UiCapturedNode node)
        {
            if (node.RequestedStyleId != null)
                Field(json, "requestedStyleId", node.RequestedStyleId);
            if (node.RequestedPaintRole is { } requested)
                Field(json, "requestedPaintRole", UiThemePackParser.PaintRoles[(int)requested]);
            if (node.EffectivePaintRole is { } effective)
                Field(json, "effectivePaintRole", UiThemePackParser.PaintRoles[(int)effective]);
        }

        private static string? ReadBrandDigest()
        {
            using var stream = typeof(UiHost).Assembly.GetManifestResourceStream("TopiaForge.Mods.UnityUi.topiaforge-ui.bundle");
            if (stream == null) return null;
            using var hash = SHA256.Create();
            return Hex(hash.ComputeHash(stream));
        }

        private static string Hex(ReadOnlySpan<byte> digest) => BitConverter.ToString(digest.ToArray()).Replace("-", "");

        private static StringBuilder Key(StringBuilder output, string key)
        {
            if (output[output.Length - 1] != '{') output.Append(',');
            return output.Append('"').Append(key).Append('"').Append(':');
        }

        private static void Entry(StringBuilder output)
        {
            if (output[output.Length - 1] != '[') output.Append(',');
            output.Append('{');
        }

        private static void Field(StringBuilder output, string key, string? value) =>
            Key(output, key).Append(value == null ? "null" : JsonUtil.Serialize(value));

        private static void Field(StringBuilder output, string key, bool value) => Key(output, key).Append(value ? "true" : "false");

        private static void Field(StringBuilder output, string key, int value) =>
            Key(output, key).Append(value.ToString(CultureInfo.InvariantCulture));

        private static void Field(StringBuilder output, string key, float? value) =>
            Key(output, key).Append(value is float number && !float.IsNaN(number) && !float.IsInfinity(number)
                ? number.ToString("R", CultureInfo.InvariantCulture) : "null");
    }
}
