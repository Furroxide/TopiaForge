using System;
using System.Collections.Generic;
using System.IO;

namespace TopiaForge.Mods
{
    internal static partial class UiThemePackParser
    {
        internal static readonly string[] PaintRoles = { "surface", "raised", "sunken", "selected", "primary", "accent", "success", "warning", "danger" };
        internal static readonly string[] ToneNames = { "neutral", "success", "warning", "danger", "muted", "primary", "accent" };
        internal static readonly string[] TextRoles = { "body", "heading", "caption", "banner", "display", "title", "label", "numeral", "mono" };
        internal const int FoundationComponentCount = (int)UiThemeTarget.Button;
        private const int ControlComponentCount = (int)UiThemeTarget.StatBar;
        internal static readonly string[] Targets =
            { "card", "box", "badge", "pips", "button", "input", "tabs", "tooltip", "statBar", "keyGlyph", "actionSlot", "overlay", "stinger" };
        internal static readonly string[] Radii = { "none", "small", "medium", "control", "card", "pill" };
        internal static readonly string[] Borders = { "none", "thin", "standard", "strong" };
        internal static readonly string[] Shadows = { "none", "small", "card" };
        internal static readonly string[] Eases = { "outCubic", "outBack" };
        private static readonly string[] InkNames = { "primary", "accent", "success", "warning", "danger" };
        private static readonly string[] Cases = { "preserve", "upper" };
        private static readonly string[] KitFonts = { "body", "display", "mono" };
        private static readonly int[] ReferenceSizes = { 20, 24, 18, 56, 40, 30, 18, 32, 18 };
        private static readonly Lazy<UiThemeDescriptor> DefaultPack = new Lazy<UiThemeDescriptor>(LoadDefaults);

        internal static UiThemeDescriptor Default => DefaultPack.Value;

        internal static OperationResult<UiThemeDescriptor> Parse(ReadOnlySpan<byte> utf8, bool packaged = false,
            string? expectedId = null, Func<string, byte[]?>? fileHash = null,
            Func<UiThemeImage, UiThemeImageMetadata?>? imageMetadata = null)
        {
            try
            {
                var pack = UiThemeJson.Read(utf8, "$");
                return OperationResult<UiThemeDescriptor>.Success(Parse(pack, Default, expectedId,
                    new AssetInputs(packaged, fileHash, imageMetadata)));
            }
            catch (UiThemeDataException error)
            {
                return OperationResult<UiThemeDescriptor>.Failure(error.Code, error.Message);
            }
        }

        private static UiThemeDescriptor LoadDefaults()
        {
            const string resource = "TopiaForge.Mods.topiaforge-ui-theme.defaults.json";
            using var stream = typeof(UiThemePackParser).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The kit's embedded theme defaults are missing.");
            if (stream.Length > UiThemeJson.MaximumBytes)
            {
                throw new InvalidOperationException("The kit's embedded theme defaults exceed the descriptor size limit.");
            }

            using var contents = new MemoryStream((int)stream.Length);
            stream.CopyTo(contents);
            try
            {
                return Parse(UiThemeJson.Read(contents.ToArray(), "$"), null, "topiaforge.default",
                    new AssetInputs(false, null, null));
            }
            catch (UiThemeDataException error)
            {
                throw new InvalidOperationException("Invalid kit theme defaults. " + error.Message, error);
            }
        }

        private static UiThemeDescriptor Parse(UiThemeJsonObject pack, UiThemeDescriptor? defaults, string? expectedId,
            AssetInputs assets)
        {
            pack.Allow("$schema", "schema", "id", "paper", "hud", "typography", "shape", "motion", "components", "styles", "images", "fonts");
            pack.OptionalString("$schema");
            pack.RequiredInteger("schema", 1, 1);
            var id = UiThemeData.Id(pack.RequiredString("id"), pack.At("id"), allowReserved: defaults == null);
            if (expectedId != null && !string.Equals(id, expectedId, StringComparison.Ordinal))
            {
                throw UiThemeDataException.Invalid(pack.At("id"), "Theme identifier differs from the catalogue entry.");
            }

            var paper = ParseScheme(pack.Object("paper"), defaults?.SelectScheme(hud: false, highContrast: false));
            var hud = ParseScheme(pack.Object("hud"), defaults?.SelectScheme(hud: true, highContrast: false));
            var fonts = ParseFonts(pack, assets);
            var images = ParseImages(pack, assets);
            var components = ParseComponents(pack.Object("components"), defaults, out var minimumWireVersion, out var canonicalComponentCount);
            var styles = ParseStyles(pack);
            foreach (var style in styles.Values)
            {
                if (style.Target is not (UiThemeTarget.ActionSlot or UiThemeTarget.Stinger)) continue;
                minimumWireVersion = UiWireCodec.ControlsContractVersion;
                canonicalComponentCount = Math.Max(canonicalComponentCount, (int)style.Target + 1);
            }
            var descriptor = new UiThemeDescriptor(id, paper, hud, ParseTypography(pack.Object("typography"), fonts, defaults),
                ParseShape(pack.Object("shape"), defaults?.Shape), ParseMotion(pack.Object("motion"), defaults?.Motion),
                components, styles, images, fonts, minimumWireVersion, canonicalComponentCount,
                defaults?.Digest ?? ReadOnlyMemory<byte>.Empty);
            ValidateScheme(descriptor.SelectScheme(false, false), pack.At("paper"), 4.5f);
            ValidateScheme(descriptor.SelectScheme(true, false), pack.At("hud"), 4.5f);
            ValidateScheme(descriptor.SelectScheme(false, true), pack.At("paper"), 7f);
            ValidateScheme(descriptor.SelectScheme(true, true), pack.At("hud"), 7f);
            return descriptor;
        }

        private static UiThemeScheme ParseScheme(UiThemeJsonObject value, UiThemeScheme? defaults)
        {
            value.Allow("colors", "muted", "outline", "focus", "inks");
            var colors = value.Object("colors");
            colors.Allow(PaintRoles);
            var pairs = new UiThemePair[PaintRoles.Length];
            for (var index = 0; index < PaintRoles.Length; index++)
            {
                var role = colors.Object(PaintRoles[index]);
                var fallback = defaults?.GetPair((UiPaintRole)index);
                role.Allow("fill", "on");
                var fill = Color(role, "fill", fallback?.Fill);
                var on = Color(role, "on", fallback?.On);
                if (UiThemeColors.Contrast(fill, on) < 4.5d)
                {
                    throw UiThemeDataException.Invalid(role.Path, "Fill and content contrast must reach 4.5:1.");
                }

                pairs[index] = new UiThemePair(fill, on);
            }

            var inks = value.Object("inks");
            inks.Allow(InkNames);
            var authored = new UiThemeColor?[ToneNames.Length];
            foreach (var name in InkNames)
            {
                if (inks.OptionalString(name) is not string declared)
                {
                    continue;
                }

                var ink = UiThemeColor.Parse(declared, inks.At(name));
                if (!UiThemeScheme.ReadsOnNeutralFills(ink, pairs, 4.5d))
                {
                    throw UiThemeDataException.Invalid(inks.At(name), "Authored ink must reach 4.5:1 on all neutral fills.");
                }

                authored[Array.IndexOf(ToneNames, name)] = ink;
            }

            return new UiThemeScheme(pairs, Color(value, "muted", defaults?.Muted),
                Color(value, "outline", defaults?.Outline), Color(value, "focus", defaults?.Focus), authored);
        }

        private static UiThemeColor Color(UiThemeJsonObject value, string name, UiThemeColor? fallback) =>
            value.OptionalString(name) is string text ? UiThemeColor.Parse(text, value.At(name)) : fallback
                ?? throw UiThemeDataException.Invalid(value.At(name), "Expected an opaque #RRGGBB color.");

        private static void ValidateScheme(UiThemeScheme scheme, string path, float textMinimum)
        {
            var markMinimum = textMinimum == 7f ? 4.5f : 3f;
            for (var fill = 0; fill < PaintRoles.Length; fill++)
            {
                var background = (UiPaintRole)fill;
                var pair = scheme.GetPair(background);
                var tints = UiThemeColors.ResolveTints(pair, textMinimum,
                    UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed);
                var marks = UiThemeColors.ResolveTints(pair, markMinimum,
                    UiThemeColors.ControlHighlighted, UiThemeColors.ControlHighlighted, UiThemeColors.ControlPressed);
                if (!CheckTints(pair, tints, textMinimum) || !CheckTints(pair, marks, markMinimum))
                {
                    throw UiThemeDataException.Invalid(path + ".colors." + PaintRoles[fill], "Effective control contrast is too low.");
                }

                for (var tone = 0; tone < ToneNames.Length; tone++)
                {
                    if (UiThemeColors.Contrast(scheme.ResolveForeground((UiTone)tone, background), pair.Fill) < textMinimum)
                    {
                        throw UiThemeDataException.Invalid(path + ".colors." + PaintRoles[fill], "Effective text contrast is too low.");
                    }
                }
            }
        }

        private static bool CheckTints(UiThemePair pair, UiThemeStateTints tints, float minimum) =>
            UiThemeColors.Contrast(pair.Fill, pair.On) >= minimum
            && UiThemeColors.ContrastAsDrawn(pair, tints.Hovered) >= minimum
            && UiThemeColors.ContrastAsDrawn(pair, tints.Selected) >= minimum
            && UiThemeColors.ContrastAsDrawn(pair, tints.Pressed) >= minimum;

        private static UiThemeTextRole[] ParseTypography(UiThemeJsonObject value, UiThemeFont[] fonts, UiThemeDescriptor? defaults)
        {
            value.Allow(TextRoles);
            var typography = new UiThemeTextRole[TextRoles.Length];
            for (var index = 0; index < TextRoles.Length; index++)
            {
                var role = value.Object(TextRoles[index]);
                var fallback = defaults?.GetText((UiTextStyle)index);
                role.Allow("font", "case", "trackingEm");
                var font = role.OptionalString("font") ?? fallback?.Font ?? role.RequiredString("font");
                if (Array.IndexOf(KitFonts, font) < 0 && Array.Find(fonts, candidate => candidate.Id == font) == null)
                {
                    throw UiThemeDataException.Invalid(role.At("font"), "Expected a kit face or a font declared in this theme.");
                }

                var textCase = ReadEnum(role, "case", Cases, fallback is { } template ? (template.IsUppercase ? 1 : 0) : null);
                var tracking = ReadNumber(role, "trackingEm", 0.12d, fallback?.TrackingEm);
                typography[index] = new UiThemeTextRole(font, textCase == 1, tracking, ReferenceSizes[index]);
            }

            return typography;
        }

        private static UiThemeShape ParseShape(UiThemeJsonObject value, UiThemeShape? defaults)
        {
            value.Allow("small", "medium", "control", "card");
            var small = ReadNumber(value, "small", 32d, defaults?.Small);
            var medium = ReadNumber(value, "medium", 32d, defaults?.Medium);
            var control = ReadNumber(value, "control", 32d, defaults?.Control);
            var card = ReadNumber(value, "card", 32d, defaults?.Card);
            if (small > medium || medium > control || control > card)
            {
                throw UiThemeDataException.Invalid(value.Path, "Shape radii must be non-decreasing.");
            }

            return new UiThemeShape(small, medium, control, card);
        }

        private static UiThemeMotion ParseMotion(UiThemeJsonObject value, UiThemeMotion? defaults)
        {
            value.Allow("fastMs", "baseMs", "slowMs", "ease");
            var fast = ReadNumber(value, "fastMs", 500d, defaults?.FastMs);
            var normal = ReadNumber(value, "baseMs", 500d, defaults?.BaseMs);
            var slow = ReadNumber(value, "slowMs", 500d, defaults?.SlowMs);
            var ease = ReadEnum(value, "ease", Eases, (int?)defaults?.Ease);
            if (fast > normal || normal > slow)
            {
                throw UiThemeDataException.Invalid(value.Path, "Motion durations must be non-decreasing.");
            }

            return new UiThemeMotion(fast, normal, slow, (UiThemeEase)ease);
        }

        private static Dictionary<UiThemeTarget, UiThemeComponent> ParseComponents(UiThemeJsonObject value, UiThemeDescriptor? defaults,
            out int minimumWireVersion, out int canonicalComponentCount)
        {
            value.Allow(Targets);
            minimumWireVersion = UiWireCodec.FoundationContractVersion;
            canonicalComponentCount = FoundationComponentCount;
            var components = new Dictionary<UiThemeTarget, UiThemeComponent>();
            for (var index = 0; index < Targets.Length; index++)
            {
                var target = (UiThemeTarget)index;
                var name = Targets[index];
                var authored = value.Has(name);
                if (defaults != null && !authored)
                {
                    components.Add(target, defaults.Components[target]);
                    continue;
                }
                if (defaults != null && index >= FoundationComponentCount)
                {
                    minimumWireVersion = UiWireCodec.ControlsContractVersion;
                    if (target is not (UiThemeTarget.Overlay or UiThemeTarget.Stinger))
                        canonicalComponentCount = Math.Max(canonicalComponentCount, Math.Max(ControlComponentCount, index + 1));
                }

                var component = value.Object(name);
                component.Allow("radius", "border", "shadow");
                var resolved = ParseComponent(component, requireComplete: true);
                if (target is (UiThemeTarget.Overlay or UiThemeTarget.Stinger) && defaults != null)
                {
                    var preset = defaults.Components[target];
                    if (resolved.Radius != preset.Radius || resolved.Border != preset.Border || resolved.Shadow != preset.Shadow)
                        canonicalComponentCount = Math.Max(canonicalComponentCount, index + 1);
                }
                components.Add(target, resolved);
            }

            return components;
        }

        private static Dictionary<string, UiThemeNamedStyle> ParseStyles(UiThemeJsonObject pack)
        {
            var styles = new Dictionary<string, UiThemeNamedStyle>(StringComparer.Ordinal);
            var values = pack.Array("styles", 32);
            for (var index = 0; index < values.Count; index++)
            {
                var style = UiThemeJsonObject.From(values[index], UiThemeData.Entry(pack.At("styles"), index));
                style.Allow("id", "target", "radius", "border", "shadow", "fill");
                var id = UiThemeData.Id(style.RequiredString("id"), style.At("id"));
                var target = (UiThemeTarget)ReadEnum(style, "target", Targets);
                if ((int)target >= FoundationComponentCount && target is not (UiThemeTarget.ActionSlot or UiThemeTarget.Stinger))
                    throw UiThemeDataException.Invalid(style.At("target"), "This component does not support named styles.");
                var fill = style.OptionalString("fill", nullable: true);
                UiPaintRole? role = fill == null ? null : (UiPaintRole)EnumIndex(fill, style.At("fill"), PaintRoles);
                var component = ParseComponent(style, requireComplete: false);
                if (!styles.TryAdd(id, new UiThemeNamedStyle(target, component, role)))
                {
                    throw UiThemeDataException.Invalid(style.At("id"), "Style identifier is already declared.");
                }
            }

            return styles;
        }

        private static UiThemeComponent ParseComponent(UiThemeJsonObject value, bool requireComplete) => new UiThemeComponent(
            (UiThemeRadius)ReadEnum(value, "radius", Radii),
            (UiThemeBorder)ReadEnum(value, "border", Borders, requireComplete ? null : (int)UiThemeBorder.Standard),
            (UiThemeShadow)ReadEnum(value, "shadow", Shadows, requireComplete ? null : (int)UiThemeShadow.None));

        private static int ReadEnum(UiThemeJsonObject value, string name, string[] names, int? fallback = null) =>
            value.OptionalString(name) is string text ? EnumIndex(text, value.At(name), names) : fallback
                ?? throw UiThemeDataException.Invalid(value.At(name), "Expected a named value.");

        private static int EnumIndex(string value, string path, string[] names)
        {
            UiThemeData.ListedName(value, path, names, out var index);
            return index;
        }

        private static float ReadNumber(UiThemeJsonObject value, string name, double maximum, float? fallback) =>
            value.Number(name, 0d, maximum) ?? fallback
                ?? throw UiThemeDataException.Invalid(value.At(name), "Expected a finite number in the permitted range.");
    }
}
