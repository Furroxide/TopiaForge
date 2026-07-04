using System;
using System.IO;

namespace Robotopia.ModManager.Tests
{
    /// <summary>
    /// Source-level conventions for the QwUi kit that the type system can't express,
    /// enforced by scanning the kit's checked-in sources.
    /// </summary>
    internal static class UiKitSourceConventionTests
    {
        public static void Run()
        {
            AllTmpComponentsCreatedThroughQwTmp();
            Console.WriteLine("UiKitSourceConventionTests passed.");
        }

        // TextMeshProUGUI only flips m_isOrthographic in Awake(), and all TMP measurement scales
        // glyph metrics by 0.1 while it is false. Kit UI is routinely built and measured under a
        // still-inactive window, so every TMP component must come from QwTmp.Create, which sets the
        // flag at creation. Three separate widgets have shipped this bug (QwBadge, QwLabel, QwButton);
        // this test keeps a fourth from ever compiling in.
        private static void AllTmpComponentsCreatedThroughQwTmp()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "Robotopia.Mods.UnityUi");
            var factory = Path.Combine(kitRoot, "Text", "QwTmp.cs");
            var factorySeen = false;

            foreach (var file in Directory.EnumerateFiles(kitRoot, "*.cs", SearchOption.AllDirectories))
            {
                var separator = Path.DirectorySeparatorChar;
                if (file.Contains(separator + "obj" + separator) || file.Contains(separator + "bin" + separator))
                {
                    continue;
                }

                if (string.Equals(file, factory, StringComparison.OrdinalIgnoreCase))
                {
                    factorySeen = true;
                    continue;
                }

                var source = File.ReadAllText(file);
                if (source.Contains("AddComponent<TextMeshProUGUI>") || source.Contains("AddComponent<TMPro.TextMeshProUGUI>"))
                {
                    throw new InvalidOperationException(
                        "Direct AddComponent<TextMeshProUGUI> in " + file
                        + " — create kit TMP labels via QwTmp.Create, which sets isOrthographic before any measurement can run.");
                }
            }

            if (!factorySeen)
            {
                throw new InvalidOperationException("Text/QwTmp.cs not found under " + kitRoot + " — did the TMP factory move? Update this test.");
            }
        }
    }
}
