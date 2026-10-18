using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Typeface conventions. Small kit text is drawn with Quicksand Medium, chosen in one place: at UI
    /// scale 1.0 in a 1920x1080 window the Light face's stems were half a pixel wide at 14 px, and the dot of
    /// an i merged with its stem, so "TopiaForge" read as "ToplaForge".
    /// </summary>
    internal static partial class UiKitSourceConventionTests
    {
        private const string MediumAssetName = "TopiaForge Body Medium SDF";
        private const string LightAssetName = "TopiaForge Body SDF";
        private const string MediumAssetPath = "tools/unity-ui-bundle/Assets/FontAssets/TopiaForge Body Medium SDF.asset";
        private const string LightAssetPath = "tools/unity-ui-bundle/Assets/FontAssets/TopiaForge Body SDF.asset";
        private const string MediumFontPath = "tools/unity-ui-bundle/Assets/Fonts/TopiaForgeBody-Medium.ttf";

        // Every style at body size or smaller gets the Medium face; Light is left to larger Quicksand styles.
        private static void SmallTextStylesUseTheMediumFace()
        {
            foreach (TopiaForgeTextStyle style in Enum.GetValues(typeof(TopiaForgeTextStyle)))
            {
                var face = TopiaForgeTokens.FaceOf(style);
                if (TopiaForgeTokens.IsDisplay(style))
                {
                    Check(face == TopiaForgeFace.Display, style + " is a display style and must use the display face, not " + face + ".");
                }
                else if (TopiaForgeTokens.SizeOf(style) <= TopiaForgeTokens.BodySize)
                {
                    Check(face == TopiaForgeFace.Text, style + " is small text and must use the Medium face, not " + face + ".");
                }
                else
                {
                    Check(face != TopiaForgeFace.Display, style + " is a Quicksand style and must not use the display face.");
                }
            }

            foreach (var style in new[] { TopiaForgeTextStyle.Body, TopiaForgeTextStyle.Label, TopiaForgeTextStyle.Caption })
            {
                Check(TopiaForgeTokens.FaceOf(style) == TopiaForgeFace.Text, style + " must use the Medium face.");
            }
        }

        // The face is resolved in one place, and the Medium face is the asset baked from the Medium instance
        // with the Light asset's atlas settings and character set.
        private static void SmallTextResolvesToTheBakedMediumAsset()
        {
            var root = Program.FindRepoRoot();
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            var fonts = File.ReadAllText(Path.Combine(kit, "Text", "TopiaForgeFonts.cs"));
            var bundle = File.ReadAllText(Path.Combine(kit, "Rendering", "TopiaForgeBrandBundle.cs"));
            var builder = File.ReadAllText(Path.Combine(root, "tools", "unity-ui-bundle", "Assets", "Editor", "UiBundleBuilder.cs"));

            RequireSource(fonts, "switch (TopiaForgeTokens.FaceOf(style))", "TopiaForgeFonts.For must choose every style's face through TopiaForgeTokens.FaceOf.");
            Check(Regex.IsMatch(fonts, @"case TopiaForgeFace\.Heading:\s*return heading;\s*default:\s*return body;"),
                "TopiaForgeFonts.For must draw the small-text face with the body font.");
            RequireSource(fonts, "var bodyFont = TopiaForgeBrandBundle.BodyFont != null", "The brand tier must draw small text with the bundle's body face.");
            RequireSource(bundle, "BodyName = \"" + MediumAssetName + "\";", "The brand bundle's body face must be the Medium SDF.");
            RequireSource(bundle, "BodyFont = bundle.LoadAsset<TMP_FontAsset>(BodyName);", "The brand bundle must load the Medium SDF as its body face.");
            RequireSource(builder, "BodyMediumFontPath = \"Assets/Fonts/TopiaForgeBody-Medium.ttf\";", "The Medium SDF must be baked from the Medium instance.");
            RequireSource(builder, "BakeFontAsset(BodyMediumFontPath, BodyMediumAssetPath, \"" + MediumAssetName + "\", 1024);",
                "The bundle must bake the Medium SDF at the Light SDF's 1024 px atlas size.");
            RequireSource(builder, "BakeFontAsset(\"Assets/Fonts/Quicksand-VariableFont_wght.ttf\", BodyAssetPath, \"" + LightAssetName + "\", 1024);",
                "The Light SDF's bake settings changed; keep the Medium SDF's in step with them.");
            Check(Regex.IsMatch(builder, @"RequiredAssets\s*=\s*\{[^}]*\bBodyMediumAssetPath\b"), "The Medium SDF must be a required bundle asset.");

            var manifest = File.ReadAllText(Path.Combine(kit, "Assets", "topiaforge-ui.manifest.json"));
            RequireSource(manifest, "\"Assets/FontAssets/" + MediumAssetName + ".asset\"", "The committed UI bundle must contain the Medium SDF.");

            var medium = File.ReadAllText(Path.Combine(root, MediumAssetPath));
            var light = File.ReadAllText(Path.Combine(root, LightAssetPath));
            RequireSource(medium, "  m_Name: " + MediumAssetName + "\n", "The Medium SDF asset must carry its bundle name.");
            RequireSource(medium, "    m_StyleName: Medium\n", "The Medium SDF must be baked from the Medium instance.");
            RequireSource(medium, "  m_SourceFontFileGUID: " + MetaGuid(Path.Combine(root, MediumFontPath + ".meta")) + "\n",
                "The Medium SDF must be baked from " + MediumFontPath + ".");
            foreach (var setting in new[] { "m_PointSize", "m_AtlasWidth", "m_AtlasHeight", "m_AtlasPadding", "m_AtlasRenderMode", "m_AtlasPopulationMode" })
            {
                var lightValue = YamlValue(light, setting);
                var mediumValue = YamlValue(medium, setting);
                Check(lightValue == mediumValue, "The Medium SDF's " + setting + " (" + mediumValue + ") must match the Light SDF's (" + lightValue + ").");
            }

            Check(Characters(medium).SequenceEqual(Characters(light)), "The Medium SDF must hold exactly the Light SDF's character set.");
        }

        // Small text never reaches the Light face: only the font resolver names it, a literal style handed to
        // TopiaForgeFonts.For is never a Light style unless the label takes that style's size too, and a
        // variable style always brings its own size.
        private static void NothingSelectsTheLightFaceForSmallText()
        {
            var root = Program.FindRepoRoot();
            var resolver = new[]
            {
                Path.Combine(root, "src", "TopiaForge.Mods.UnityUi", "Text", "TopiaForgeFonts.cs"),
                Path.Combine(root, "src", "TopiaForge.Mods.UnityUi", "Rendering", "TopiaForgeBrandBundle.cs"),
            };
            var literal = new Regex(@"TopiaForgeFonts\.For\(\s*TopiaForgeTextStyle\.(\w+)\s*\)", RegexOptions.CultureInvariant);
            var variable = new Regex(@"TopiaForgeFonts\.For\(\s*(?!TopiaForgeTextStyle\.)([A-Za-z_][\w.]*)\s*\)", RegexOptions.CultureInvariant);
            var checkedCalls = 0;
            foreach (var directory in new[]
            {
                Path.Combine(root, "src", "TopiaForge.Mods.UnityUi"),
                Path.Combine(root, "src", "TopiaForge.ModManager"),
                Path.Combine(root, "mods"),
            })
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(file) || resolver.Any(path => string.Equals(path, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var source = File.ReadAllText(file);
                    if (source.Contains("TopiaForgeFonts.Heading", StringComparison.Ordinal)
                        || source.Contains("TopiaForgeBrandBundle.HeadingFont", StringComparison.Ordinal)
                        || source.Contains("\"" + LightAssetName + "\"", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Only the font resolver may select the Light face, but " + file + " reaches it directly. Take a style's face from TopiaForgeFonts.For.");
                    }

                    foreach (Match match in literal.Matches(source))
                    {
                        checkedCalls++;
                        var name = match.Groups[1].Value;
                        var style = (TopiaForgeTextStyle)Enum.Parse(typeof(TopiaForgeTextStyle), name);
                        var sizedAsStyle = source.Contains("TopiaForgeTokens." + name + "Size", StringComparison.Ordinal)
                            || source.Contains("TopiaForgeTokens.SizeOf(TopiaForgeTextStyle." + name + ")", StringComparison.Ordinal);
                        if (TopiaForgeTokens.FaceOf(style) == TopiaForgeFace.Heading && !sizedAsStyle)
                        {
                            throw new InvalidOperationException(
                                file + " takes the Light face of " + name + " for a label it sizes itself. Small labels take a small style's face, such as Label or Caption.");
                        }
                    }

                    foreach (Match match in variable.Matches(source))
                    {
                        checkedCalls++;
                        var expression = match.Groups[1].Value;
                        if (!source.Contains("TopiaForgeTokens.SizeOf(" + expression + ")", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                file + " takes the face of style '" + expression + "' without its size. Size that label with TopiaForgeTokens.SizeOf(" + expression + ").");
                        }
                    }
                }
            }

            Check(checkedCalls >= 20, "Expected the kit's widgets to resolve their fonts through TopiaForgeFonts.For; found " + checkedCalls + " calls. Did the resolver move?");
        }

        // Bold styles stay bold. Faux bold used to be on only while the bold face was the body face, so a new
        // body face would have silently drawn headings, labels and numerals at regular weight.
        private static void BoldStylesStayBold()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var fonts = File.ReadAllText(Path.Combine(kit, "Text", "TopiaForgeFonts.cs"));
            var label = File.ReadAllText(Path.Combine(kit, "Widgets", "TopiaForgeLabel.cs"));
            RequireSource(fonts, "public static bool UseFauxBold => true;", "No bold face ships, so TopiaForgeFonts.UseFauxBold must stay on in every tier.");
            RequireSource(label, "(TopiaForgeTokens.IsBold(style) && TopiaForgeFonts.UseFauxBold)", "Labels must draw bold styles with faux bold.");
            foreach (var style in new[] { TopiaForgeTextStyle.Heading, TopiaForgeTextStyle.Label, TopiaForgeTextStyle.Numeral })
            {
                Check(TopiaForgeTokens.IsBold(style), style + " must stay a bold style.");
            }
        }

        // The committed instance is the file THIRD_PARTY_NOTICES.md records, the wght 500 instance the
        // committed generator writes.
        private static void MediumInstanceMatchesItsRecordedProvenance()
        {
            var root = Program.FindRepoRoot();
            var font = File.ReadAllBytes(Path.Combine(root, MediumFontPath));
            if (font.Length < 1024 && Encoding.ASCII.GetString(font).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(MediumFontPath + " is a Git LFS pointer. Run 'git lfs checkout' before the tests.");
            }

            var hash = Convert.ToHexString(SHA256.HashData(font)).ToLowerInvariant();
            var notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"));
            RequireSource(notices, "`" + MediumFontPath + "`", "THIRD_PARTY_NOTICES.md must record the Medium instance by its path.");
            RequireSource(notices, "`" + hash + "`", "THIRD_PARTY_NOTICES.md must record the Medium instance's SHA-256, " + hash + ".");
            var generator = File.ReadAllText(Path.Combine(root, "tools", "unity-ui-bundle", "make_body_medium_font.py"));
            RequireSource(generator, "WEIGHT = 500\n", "The small-text face must be the wght 500 instance.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string MetaGuid(string metaPath)
        {
            var match = Regex.Match(File.ReadAllText(metaPath), @"^guid: ([0-9a-f]{32})$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Check(match.Success, metaPath + " has no GUID.");
            return match.Groups[1].Value;
        }

        private static string YamlValue(string yaml, string name)
        {
            var match = Regex.Match(yaml, @"^\s*" + Regex.Escape(name) + @": (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Check(match.Success, "The SDF asset has no " + name + ".");
            return match.Groups[1].Value.Trim();
        }

        private static int[] Characters(string yaml)
        {
            return Regex.Matches(yaml, @"^    m_Unicode: (\d+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(value => value)
                .ToArray();
        }
    }
}
