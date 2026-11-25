using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Mods
{
    internal static class UiThemeCanonical
    {
        internal static byte[] ComputeDigest(UiThemeDescriptor theme, ReadOnlySpan<byte> defaultFingerprint)
        {
            var json = new StringBuilder("{\"schema\":1,");
            Member(json, "id", theme.Id);
            json.Append(",\"paper\":");
            WriteScheme(json, theme.SelectScheme(false, false));
            json.Append(",\"hud\":");
            WriteScheme(json, theme.SelectScheme(true, false));
            json.Append(",\"typography\":{");
            for (var index = 0; index < UiThemePackParser.TextRoles.Length; index++)
            {
                Separate(json, index);
                var role = theme.GetText((UiTextStyle)index);
                Name(json, UiThemePackParser.TextRoles[index]).Append('{');
                Member(json, "font", role.Font).Append(',');
                Member(json, "case", role.IsUppercase ? "upper" : "preserve").Append(',');
                NumberMember(json, "trackingEm", role.TrackingEm).Append('}');
            }

            json.Append("},\"shape\":{");
            NumberMember(json, "small", theme.Shape.Small).Append(',');
            NumberMember(json, "medium", theme.Shape.Medium).Append(',');
            NumberMember(json, "control", theme.Shape.Control).Append(',');
            NumberMember(json, "card", theme.Shape.Card).Append("},\"motion\":{");
            NumberMember(json, "fastMs", theme.Motion.FastMs).Append(',');
            NumberMember(json, "baseMs", theme.Motion.BaseMs).Append(',');
            NumberMember(json, "slowMs", theme.Motion.SlowMs).Append(',');
            Member(json, "ease", UiThemePackParser.Eases[(int)theme.Motion.Ease]).Append("},\"components\":{");
            // Keep the exact K1 digest for existing descriptors, including the defaults fingerprint.
            var componentCount = theme.MinimumWireVersion == UiWireCodec.FoundationContractVersion
                ? UiThemePackParser.FoundationComponentCount : UiThemePackParser.Targets.Length;
            for (var index = 0; index < componentCount; index++)
            {
                Separate(json, index);
                Name(json, UiThemePackParser.Targets[index]).Append('{');
                WriteComponent(json, theme.Components[(UiThemeTarget)index]);
                json.Append('}');
            }

            json.Append("},\"styles\":[");
            var ordinal = 0;
            foreach (var style in theme.Styles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Separate(json, ordinal++);
                json.Append('{');
                Member(json, "id", style.Key).Append(',');
                Member(json, "target", UiThemePackParser.Targets[(int)style.Value.Target]).Append(',');
                WriteComponent(json, style.Value.Component);
                json.Append(',');
                Member(json, "fill", style.Value.Fill is UiPaintRole role ? UiThemePackParser.PaintRoles[(int)role] : null).Append('}');
            }

            json.Append("],\"images\":");
            WriteAssets(json, theme.Images);
            json.Append(",\"fonts\":");
            WriteAssets(json, theme.Fonts);
            json.Append(',');
            Member(json, "kitDefaultFingerprint", UiThemeData.Hex(defaultFingerprint)).Append('}');

            var normalized = json.ToString();
            if (Encoding.UTF8.GetByteCount(normalized) > UiThemeJson.MaximumBytes)
            {
                throw UiThemeDataException.Limit("$", "The normalized theme exceeds its UTF-8 size limit.");
            }

            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        }

        private static void WriteScheme(StringBuilder json, UiThemeScheme scheme)
        {
            json.Append("{\"colors\":{");
            for (var index = 0; index < UiThemePackParser.PaintRoles.Length; index++)
            {
                Separate(json, index);
                var pair = scheme.GetPair((UiPaintRole)index);
                Name(json, UiThemePackParser.PaintRoles[index]).Append('{');
                Member(json, "fill", pair.Fill.ToString()).Append(',');
                Member(json, "on", pair.On.ToString()).Append('}');
            }

            json.Append("},");
            Member(json, "muted", scheme.Muted.ToString()).Append(',');
            Member(json, "outline", scheme.Outline.ToString()).Append(',');
            Member(json, "focus", scheme.Focus.ToString()).Append(",\"inks\":{");
            var ordinal = 0;
            for (var index = 0; index < UiThemePackParser.ToneNames.Length; index++)
            {
                if (scheme.AuthoredInk((UiTone)index) is UiThemeColor ink)
                {
                    Separate(json, ordinal++);
                    Member(json, UiThemePackParser.ToneNames[index], ink.ToString());
                }
            }

            json.Append("}}");
        }

        private static void WriteComponent(StringBuilder json, UiThemeComponent component)
        {
            Member(json, "radius", UiThemePackParser.Radii[(int)component.Radius]).Append(',');
            Member(json, "border", UiThemePackParser.Borders[(int)component.Border]).Append(',');
            Member(json, "shadow", UiThemePackParser.Shadows[(int)component.Shadow]);
        }

        private static void WriteAssets(StringBuilder json, IEnumerable<UiThemeAsset> assets)
        {
            json.Append('[');
            var ordinal = 0;
            foreach (var asset in assets.OrderBy(asset => asset.Id, StringComparer.Ordinal))
            {
                Separate(json, ordinal++);
                json.Append('{');
                Member(json, "id", asset.Id).Append(',');
                Member(json, "bundlePath", asset.BundlePath).Append(',');
                Member(json, "assetName", asset.AssetName).Append(',');
                // The exact bundle bytes pin the named resource. Licence locations stay local.
                Member(json, "bundleSha256", asset.BundleSha256);
                if (asset is UiThemeImage image)
                {
                    json.Append(',');
                    Name(json, "pixelWidth").Append(Integer(image.PixelWidth)).Append(',');
                    Name(json, "pixelHeight").Append(Integer(image.PixelHeight)).Append(',');
                    Name(json, "isMask").Append(image.IsMask.HasValue ? (image.IsMask.Value ? "true" : "false") : "null");
                }

                json.Append('}');
            }

            json.Append(']');
        }

        private static string Integer(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

        private static StringBuilder NumberMember(StringBuilder json, string name, float value) =>
            Name(json, name).Append(value == 0f ? "0" : value.ToString("G9", CultureInfo.InvariantCulture));

        private static void Separate(StringBuilder json, int index)
        {
            if (index != 0)
            {
                json.Append(',');
            }
        }

        private static StringBuilder Member(StringBuilder json, string name, string? value) =>
            value == null ? Name(json, name).Append("null") : Quoted(Name(json, name), value);

        private static StringBuilder Name(StringBuilder json, string name) => Quoted(json, name).Append(':');

        private static StringBuilder Quoted(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (var character in value)
            {
                if (character == '"' || character == '\\')
                {
                    json.Append('\\').Append(character);
                }
                else if (character < ' ')
                {
                    json.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    json.Append(character);
                }
            }

            return json.Append('"');
        }
    }
}
