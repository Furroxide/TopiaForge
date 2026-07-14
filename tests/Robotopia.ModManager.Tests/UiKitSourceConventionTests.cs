using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
            ComponentLookupHonorsUnityNullSemantics();
            CanvasSortingUsesAllocator();
            ProcessWideUiStateHasShutdownPath();
            FirstPartyModsDoNotConstructRawUnityUi();
            FirstPartyModsDoNotMutateGlobalTheme();
            UiKitFilesStayReviewable();
            FirstPartyUiFilesStayReviewable();
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

        private static void CanvasSortingUsesAllocator()
        {
            var repoRoot = Program.FindRepoRoot();
            var allocator = Path.Combine(repoRoot, "src", "Robotopia.Mods.UnityUi", "Runtime", "QwLayers.cs");
            var roots = new[]
            {
                Path.Combine(repoRoot, "src", "Robotopia.Mods.UnityUi"),
                Path.Combine(repoRoot, "src", "Robotopia.ModManager"),
                Path.Combine(repoRoot, "mods"),
            };
            var assignment = new Regex(@"\.sortingOrder\s*=", RegexOptions.CultureInvariant);

            foreach (var root in roots)
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(file) || string.Equals(file, allocator, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (assignment.IsMatch(File.ReadAllText(file)))
                    {
                        throw new InvalidOperationException(
                            "Direct Canvas.sortingOrder assignment in " + file +
                            " — allocate and assign canvas order through QwLayers.");
                    }
                }
            }
        }

        private static void ComponentLookupHonorsUnityNullSemantics()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "Robotopia.Mods.UnityUi");
            var unsafeLookup = new Regex(
                @"GetComponent\s*<[^>]+>\s*\(\s*\)\s*\?\?",
                RegexOptions.CultureInvariant);

            foreach (var file in Directory.EnumerateFiles(kitRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || !unsafeLookup.IsMatch(File.ReadAllText(file)))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "Unity component lookup uses CLR ?? null semantics in " + file
                    + " — use QwComponents.GetOrAdd so Unity fake-null values are rejected.");
            }
        }

        private static void ProcessWideUiStateHasShutdownPath()
        {
            var repoRoot = Program.FindRepoRoot();
            var kitRoot = Path.Combine(repoRoot, "src", "Robotopia.Mods.UnityUi");
            var qwUi = File.ReadAllText(Path.Combine(kitRoot, "QwUi.cs"));
            var toast = File.ReadAllText(Path.Combine(kitRoot, "Widgets", "QwToast.cs"));
            var host = File.ReadAllText(Path.Combine(kitRoot, "UiHost.cs"));
            var plugin = File.ReadAllText(
                Path.Combine(repoRoot, "src", "Robotopia.ModManager", "RobotopiaModManagerPlugin.cs"));

            RequireSource(qwUi, "public static void Shutdown()", "QwUi must expose an idempotent loader shutdown path.");
            RequireSource(qwUi, "QwToasts.Reset();", "QwUi shutdown must clear process-wide toast state.");
            RequireSource(qwUi, "QwRuntime.Shutdown();", "QwUi shutdown must stop its hidden runtime driver.");
            RequireSource(qwUi, "QwLog.Reset();", "QwUi shutdown must release owner logging delegates.");
            RequireSource(qwUi, "while (Hosts.Count > 0)", "QwUi shutdown must reclaim forgotten hosts.");
            RequireSource(host, "QwUi.OnHostDisposed(this);", "Disposed hosts must leave the global host registry.");
            RequireSource(toast, "QwToastHost.Instance.Layer(", "The toast canvas must be owned by its UiHost.");
            RequireSource(toast, "Queue.Clear();", "Toast shutdown must clear pending notifications.");
            RequireSource(toast, "Views.Clear();", "Toast shutdown must release pooled view references.");
            if (toast.Contains("QwLayers.CreateCanvas(\"QuantumWorksToasts\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The process-wide toast canvas bypasses UiHost ownership and cannot be reliably torn down.");
            }

            RequireSource(plugin, "QwUi.Shutdown();", "The manager plugin must invoke QwUi shutdown from OnDestroy.");
        }

        private static void RequireSource(string source, string expected, string message)
        {
            if (!source.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void UiKitFilesStayReviewable()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "Robotopia.Mods.UnityUi");
            foreach (var file in Directory.EnumerateFiles(kitRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                var lines = File.ReadLines(file).Count();
                if (lines > 400)
                {
                    throw new InvalidOperationException(
                        "QwUi source file exceeds the 400-line responsibility boundary (" + lines + "): " + file);
                }
            }
        }

        private static void FirstPartyModsDoNotConstructRawUnityUi()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            var rawComponent = new Regex(
                @"AddComponent\s*<\s*(?:Canvas|CanvasScaler|GraphicRaycaster|Image|RawImage|Button|Toggle|Slider|"
                + @"ScrollRect|Scrollbar|Text|TextMeshProUGUI|TMP_InputField|HorizontalLayoutGroup|"
                + @"VerticalLayoutGroup|GridLayoutGroup|ContentSizeFitter)\s*>",
                RegexOptions.CultureInvariant);

            foreach (var file in Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                if (rawComponent.IsMatch(source)
                    || source.Contains("new GameObject") && source.Contains("typeof(RectTransform)"))
                {
                    throw new InvalidOperationException(
                        "First-party mod constructs raw Unity UI in " + file
                        + " — build Robotopia-owned visuals through QwUi. Read-only/native-game UI adapters may inspect existing components.");
                }
            }
        }

        private static void FirstPartyModsDoNotMutateGlobalTheme()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            var assignment = new Regex(
                @"QwTheme\.(?:HighContrast|UiScale|ReducedMotion|MotionScale)\s*=",
                RegexOptions.CultureInvariant);

            foreach (var file in Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || !assignment.IsMatch(File.ReadAllText(file)))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "First-party mod mutates process-wide QwTheme in " + file
                    + " — pass QwAccessibilityProfile through QwUiOptions or UiHost instead.");
            }
        }

        private static void FirstPartyUiFilesStayReviewable()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            foreach (var file in Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || !IsUiFile(file))
                {
                    continue;
                }

                var lines = File.ReadLines(file).Count();
                if (lines > 400)
                {
                    throw new InvalidOperationException(
                        "First-party UI source file exceeds the 400-line responsibility boundary (" + lines + "): " + file);
                }
            }
        }

        private static bool IsUiFile(string file)
        {
            var separator = Path.DirectorySeparatorChar;
            var name = Path.GetFileNameWithoutExtension(file);
            return file.Contains(separator + "Ui" + separator)
                || file.Contains(separator + "Hud" + separator)
                || name.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Modal", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Gallery", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PauseMenu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsBuildOutput(string file)
        {
            var separator = Path.DirectorySeparatorChar;
            return file.Contains(separator + "obj" + separator) || file.Contains(separator + "bin" + separator);
        }
    }
}
