using System;
using System.Globalization;
using System.Linq;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiThemePackTests
    {
        internal static void Run()
        {
            DefaultsAndContrastAreResolved();
            AuthoredContrastIsValidated();
            StateTintsPreserveTheReviewedTransitions();
            StrictJsonRejectsInvalidInputs();
            NormalizationUsesEffectiveValuesAndContentHashes();
            ArtworkRequiresRuntimeSupport();
            CatalogueIsStrictAndHashesExactBytes();
            EffectiveDescriptorReadsAllocateNothing();
            Console.WriteLine("UiThemePackTests passed.");
        }

        private static void DefaultsAndContrastAreResolved()
        {
            var kit = UiThemePackParser.Default;
            var authored = Parse("{}");
            Assert(kit.Id == "topiaforge.default" && authored.Id == "sample", "Default identity is reserved to the kit.");
            foreach (var hud in new[] { false, true })
            {
                var ordinary = authored.SelectScheme(hud, false);
                var highContrast = authored.SelectScheme(hud, true);
                var primary = ordinary.GetPair(UiPaintRole.Primary);
                Assert(primary.Fill.ToString() == "#FF7A11" && primary.On.ToString() == (hud ? "#10141B" : "#2D3748"),
                    "The corresponding #400 brand pair is inherited verbatim.");
                foreach (UiPaintRole paint in Enum.GetValues(typeof(UiPaintRole)))
                {
                    foreach (UiTone tone in Enum.GetValues(typeof(UiTone)))
                    {
                        Assert(UiThemeColors.Contrast(ordinary.GetPair(paint).Fill, ordinary.ResolveForeground(tone, paint)) >= 4.5d,
                            "Every ordinary text tone reads on the actual fill.");
                        Assert(UiThemeColors.Contrast(highContrast.GetPair(paint).Fill, highContrast.ResolveForeground(tone, paint)) >= 7d,
                            "Every high contrast text tone reads on the derived fill.");
                    }
                }
            }

            var textSizes = new[] { 20, 24, 18, 56, 40, 30, 18, 32, 18 };
            foreach (UiTextStyle role in Enum.GetValues(typeof(UiTextStyle)))
            {
                Assert(authored.GetText(role).ReferenceSize == textSizes[(int)role], "Role sizes use the readable ramp.");
            }

            Assert(authored.GetText(UiTextStyle.Mono).Font == "mono" && !authored.GetText(UiTextStyle.Mono).IsUppercase,
                "Mono uses the kit mono face and preserves case.");
            Assert(authored.Shape.Resolve(UiThemeRadius.Pill, 91f, 31f) == 15.5f, "Pill resolves from the shorter extent.");
        }

        private static void AuthoredContrastIsValidated()
        {
            Invalid("""{"paper":{"colors":{"primary":{"on":"#FFFFFF"}}}}""", ModErrorCode.InvalidArgument, "$.paper.colors.primary");
            Invalid("""{"paper":{"inks":{"primary":"#C0C0C0"}}}""", ModErrorCode.InvalidArgument, "$.paper.inks.primary");
            // This ink passes on Surface but fails on the darker Selected neutral fill.
            Invalid("""{"paper":{"inks":{"primary":"#737373"}}}""", ModErrorCode.InvalidArgument, "$.paper.inks.primary");
            var customized = Parse("""
                {"paper":{"colors":{"selected":{"fill":"#777777","on":"#000000"}}}}
                """);
            var ordinary = customized.SelectScheme(false, false);
            var highContrast = customized.SelectScheme(false, true);
            var grey = highContrast.GetPair(UiPaintRole.Selected);
            Assert(ordinary.GetPair(UiPaintRole.Selected).Fill.ToString() == "#777777" && grey.Fill.ToString() == "#959595"
                && grey.On.ToString() == "#000000", "High contrast uses the first passing 8-bit interpolation step.");
            Assert(UiThemeColors.Contrast(grey.On, new UiThemeColor(148, 148, 148)) < 7d, "The preceding channel step is short of 7:1.");

            var authored = Parse("""{"paper":{"inks":{"primary":"#9A4100"}}}""");
            var normal = authored.SelectScheme(false, false);
            Assert(normal.ResolveForeground(UiTone.Primary, UiPaintRole.Surface).ToString() == "#9A4100",
                "A declared ink that reads is used verbatim.");
            Assert(normal.ResolveForeground(UiTone.Primary, UiPaintRole.Danger).ToString() != "#9A4100",
                "A foreground repairs against its actual non-neutral fill.");
        }

        private static void StateTintsPreserveTheReviewedTransitions()
        {
            var paper = UiThemePackParser.Default.SelectScheme(false, false);
            var pair = paper.GetPair(UiPaintRole.Danger);
            var state = UiThemeColors.ResolveTints(pair, 4.5f, UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed);
            Assert(state.Hovered == 0.94f && state.Selected == 0.96f && state.Pressed == 0.88f,
                "Paper Danger mirrors hover and selection, with an independently drawn press state.");
            Assert(pair.Fill.Tinted(state.Hovered).ToString() == "#BC3A48" && pair.Fill.Tinted(state.Pressed).ToString() == "#B03744",
                "Paper Danger hover and press remain visibly distinct after 8-bit quantization.");
            var primary = paper.GetPair(UiPaintRole.Primary);
            state = UiThemeColors.ResolveTints(primary, 4.5f, UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed);
            Assert(state.Pressed > 1f && UiThemeColors.ContrastAsDrawn(primary, state.Pressed) >= 4.5d,
                "Paper Primary lightens on press to preserve its resting ink.");
        }

        private static void StrictJsonRejectsInvalidInputs()
        {
            Invalid("""{"paper":{"focus":"#2D3748","foc\u0075s":"#2D3748"}}""", ModErrorCode.InvalidArgument);
            Invalid("""{"paper":{"colors":{"danger":{"mystery":"private input"}}}}""", ModErrorCode.InvalidArgument, "$.paper.colors.danger");
            Invalid("""{"components":{"card":{"radius":"card","border":2,"shadow":"none"}}}""", ModErrorCode.InvalidArgument, "$.components.card.border");
            Invalid("""{"components":{"box":{"radius":"small","border":"thin"}}}""", ModErrorCode.InvalidArgument, "$.components.box.shadow");
            Invalid("""{"typography":{"body":{"font":"missing-face"}}}""", ModErrorCode.InvalidArgument, "$.typography.body.font");
            Invalid("""{"typography":{"body":{"trackingEm":1e9999}}}""", ModErrorCode.InvalidArgument, "$.typography.body.trackingEm");
            Invalid("""{"typography":{"body":{"case":"\uD800"}}}""", ModErrorCode.InvalidArgument);
            Invalid("""{"shape":{"small":15,"medium":14}}""", ModErrorCode.InvalidArgument, "$.shape");
            Invalid("""{"shape":{"small":02}}""", ModErrorCode.InvalidArgument);
            Invalid("""{"motion":{"fastMs":161}}""", ModErrorCode.InvalidArgument, "$.motion");
            Invalid("""{"paper":null}""", ModErrorCode.InvalidArgument, "$.paper");
            Invalid("""{"paper":{"muted":"#995544FF"}}""", ModErrorCode.InvalidArgument, "$.paper.muted");
            Invalid("""{"$schema":"schema.json",}""", ModErrorCode.InvalidArgument);
            Invalid("""{"fonts":[{},{},{},{},{},{},{},{},{},{}]}""", ModErrorCode.RateLimited, "$.fonts");
            Invalid("{" + new string(' ', 64 * 1024) + "}", ModErrorCode.RateLimited);
            Invalid("{\"opaque\":" + new string('[', 16) + new string(']', 16) + "}", ModErrorCode.RateLimited);

            var invalidUtf8 = UiThemePackParser.Parse(new byte[] { 0x22, 0xC0, 0xAF, 0x22 });
            Assert(invalidUtf8.ErrorCode == ModErrorCode.InvalidArgument, "Overlong UTF-8 is refused.");
            var appended = UiThemePackParser.Parse(Bytes("""{"schema":1,"id":"sample"}false"""));
            Assert(appended.ErrorCode == ModErrorCode.InvalidArgument, "A second value may not follow the definition.");
            var spoofed = UiThemePackParser.Parse(Bytes("""{"schema":1,"id":"topiaforge.default"}"""));
            Assert(spoofed.ErrorCode == ModErrorCode.InvalidArgument, "A mod cannot claim the embedded theme identity.");
        }

        private static void NormalizationUsesEffectiveValuesAndContentHashes()
        {
            var minimal = Parse("{}");
            var explicitDefaults = Parse("""
                {"paper":{"colors":{"primary":{"fill":"#ff7a11"}}},"shape":{"small":8.0},"typography":{"label":{"font":"body","trackingEm":-0.0}},
                 "components":{"card":{"radius":"card","border":"standard","shadow":"card"}},"$schema":"a local schema"}
                """);
            Assert(minimal.Digest.Span.SequenceEqual(explicitDefaults.Digest.Span), "Explicit defaults and hex case canonicalize identically.");

            var first = Parse("""{"styles":[{"id":"a","target":"box","radius":"small"},{"id":"b","target":"card","radius":"card"}]}""");
            var second = Parse("""{"styles":[{"target":"card","id":"b","radius":"card","border":"standard","shadow":"none","fill":null},{"id":"a","target":"box","radius":"small"}]}""");
            Assert(first.Digest.Span.SequenceEqual(second.Digest.Span), "Named style order and explicit null fill canonicalize identically.");
            var changedFill = Parse("""{"styles":[{"id":"a","target":"box","radius":"small","fill":"surface"},{"id":"b","target":"card","radius":"card"}]}""");
            Assert(!first.Digest.Span.SequenceEqual(changedFill.Digest.Span), "A concrete named fill differs from an inherited role.");

            var changedComponent = Parse("""{"components":{"card":{"radius":"card","border":"standard","shadow":"small"}}}""");
            Assert(!minimal.Digest.Span.SequenceEqual(changedComponent.Digest.Span), "Changing an effective component changes the digest.");
            var firstInk = Parse("""{"paper":{"inks":{"primary":"#142638"}}}""");
            var secondInk = Parse("""{"paper":{"inks":{"primary":"#172737"}}}""");
            Assert(!firstInk.Digest.Span.SequenceEqual(secondInk.Digest.Span), "The exact authored ink enters the digest.");

            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                var invariant = Parse("""{"typography":{"label":{"trackingEm":0.12}}}""");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
                var localized = Parse("""{"typography":{"label":{"trackingEm":0.120}}}""");
                Assert(invariant.Digest.Span.SequenceEqual(localized.Digest.Span), "Canonical numbers are independent of the machine culture.");
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        private static void ArtworkRequiresRuntimeSupport()
        {
            const string image = """{"schema":1,"id":"sample","images":[{"id":"crate","bundlePath":"ui/art.bundle","assetName":"Crate","licensePath":"LICENSES/art.txt","pixelWidth":64,"pixelHeight":48,"isMask":true}]}""";
            const string font = """{"schema":1,"id":"sample","fonts":[{"id":"signal","bundlePath":"ui/fonts.bundle","assetName":"Signal","licensePath":"LICENSES/OFL.txt"}]}""";
            foreach (var json in new[] { image, font })
            {
                foreach (var packaged in new[] { false, true })
                {
                    var result = UiThemePackParser.Parse(Bytes(json), packaged: packaged,
                        fileHash: _ => throw new InvalidOperationException("Unsupported artwork must not read its bundle."));
                    Assert(result.ErrorCode == ModErrorCode.InvalidArgument &&
                        result.ErrorMessage.Contains("require UI kit artwork support", StringComparison.Ordinal),
                        "Authored and packaged definitions require the same artwork support.");
                }
            }
            Assert(UiThemePackParser.Parse(Bytes("""{"schema":1,"id":"sample","images":[],"fonts":[]}""")).Succeeded,
                "Empty asset declarations still admit a resource-free theme.");
            Assert(UiThemePackParser.Parse(Bytes("""{"schema":1,"id":"sample"}"""), expectedId: "other").ErrorCode ==
                ModErrorCode.InvalidArgument, "The definition id must match its manifest declaration.");
        }

        private static void CatalogueIsStrictAndHashesExactBytes()
        {
            var manifest = UiThemeCatalogueParser.ParseManifest(Bytes("\uFEFF" + """
                {"name":"sample","x-integration":{"ui":"unrelated"},"ui":{"schema":1,"default":"sample",
                "themes":[{"id":"sample","definition":"ui/themes/sample.json"}]}}
                """));
            Assert(manifest.Succeeded && manifest.Value!.Default == "sample" && manifest.Value!.Themes[0].Definition == "ui/themes/sample.json",
                "The actual top-level UI declaration is read through the manifest.");
            Assert(UiThemeCatalogueParser.ParseManifest(Bytes("""{"name":"legacy","x-doc":{"ui":true}}""")).Value!.Themes.Count == 0,
                "A legacy package has an empty theme catalogue.");
            Assert(UiThemeCatalogueParser.ParseManifest(Bytes("""{"ui":{"schema":1,"themes":[]},"\u0075i":{"schema":1,"themes":[]}}""")).ErrorCode
                == ModErrorCode.InvalidArgument, "A second decoded top-level ui key is refused.");
            Assert(UiThemeCatalogueParser.Parse(Bytes("""{"schema":1,"default":"missing","themes":[]}""")).ErrorCode == ModErrorCode.InvalidArgument,
                "A default must name a declared theme.");
            Assert(UiThemeCatalogueParser.Parse(Bytes("""{"schema":1,"themes":[{"id":"x","definition":"x.json"},{"id":"x","definition":"y.json"}]}""")).ErrorCode
                == ModErrorCode.Conflict, "A duplicate declaration ID is a conflict.");
            foreach (var path in new[] { "../secret.json", "ui//theme.json", "/theme.json", "C:/theme.json", "ui/con.txt", "ui/theme.json:stream", "ui\u0000/name" })
            {
                var literal = path.Replace("\0", "\\u0000", StringComparison.Ordinal);
                var catalogue = UiThemeCatalogueParser.Parse(Bytes("{\"schema\":1,\"themes\":[{\"id\":\"x\",\"definition\":\"" + literal + "\"}]}"));
                Assert(catalogue.ErrorCode == ModErrorCode.InvalidArgument && !catalogue.ErrorMessage.Contains(literal, StringComparison.Ordinal),
                    "An unsafe path is refused without repeating its contents.");
            }

            const string hash = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
            var packaged = UiThemeCatalogueParser.Parse(Bytes("{\"schema\":1,\"themes\":[{\"id\":\"x\",\"definition\":\"x.json\",\"sha256\":\"" + hash + "\"}]}"), packaged: true);
            Assert(packaged.Succeeded && packaged.Value!.Themes[0].Matches(Bytes("{}")) && !packaged.Value!.Themes[0].Matches(Bytes("{}\n")),
                "An admitted definition must match the exact packaged bytes.");
            var noHash = UiThemeCatalogueParser.Parse(Bytes("""{"schema":1,"themes":[{"id":"x","definition":"x.json"}]}"""), packaged: true);
            Assert(noHash.ErrorCode == ModErrorCode.InvalidArgument, "An admitted definition requires a hash.");
        }

        private static void EffectiveDescriptorReadsAllocateNothing()
        {
            var descriptor = UiThemePackParser.Default;
            _ = ReadEffectiveDescriptor(descriptor);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var sum = ReadEffectiveDescriptor(descriptor);
            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert(sum > 0 && after == before, "Warm steady-state effective theme lookups allocate zero managed bytes.");
        }

        private static int ReadEffectiveDescriptor(UiThemeDescriptor descriptor)
        {
            var sum = 0;
            for (var index = 0; index < 10_000; index++)
            {
                var scheme = descriptor.SelectScheme((index & 1) != 0, (index & 2) != 0);
                sum += scheme.ResolveForeground(UiTone.Primary, UiPaintRole.Sunken).R;
                sum += descriptor.GetText(UiTextStyle.Mono).ReferenceSize;
                sum += (int)descriptor.Components[UiThemeTarget.Card].Radius;
            }

            return sum;
        }

        private static UiThemeDescriptor Parse(string properties)
        {
            var result = Read(properties);
            return result.Value ?? throw new InvalidOperationException(result.ErrorMessage);
        }

        private static void Invalid(string properties, ModErrorCode code, string? path = null)
        {
            var result = Read(properties);
            Assert(!result.Succeeded && result.ErrorCode == code, "Expected " + code + ", got " + result.ErrorMessage);
            Assert(path == null || result.ErrorMessage.StartsWith(path + ":", StringComparison.Ordinal), "Expected a bounded diagnostic at " + path);
        }

        private static OperationResult<UiThemeDescriptor> Read(string properties) => UiThemePackParser.Parse(Bytes(
            "{\"schema\":1,\"id\":\"sample\"" + (properties.Length == 2 ? string.Empty : "," + properties.Substring(1, properties.Length - 2)) + "}"));

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("UI theme: " + message);
            }
        }
    }
}
