using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Source-level conventions for the TopiaForgeUi kit that the type system can't express,
    /// enforced by scanning the kit's checked-in sources.
    /// </summary>
    internal static class UiKitSourceConventionTests
    {
        public static void Run()
        {
            AllTmpComponentsCreatedThroughTopiaForgeTmp();
            ComponentLookupHonorsUnityNullSemantics();
            CanvasSortingUsesAllocator();
            ProcessWideUiStateHasShutdownPath();
            UnityUiReferencesStayInternal();
            FirstPartyModsDoNotConstructRawUnityUi();
            FirstPartyModsDoNotMutateGlobalTheme();
            UiKitFilesStayReviewable();
            FirstPartyUiFilesStayReviewable();
            Console.WriteLine("UiKitSourceConventionTests passed.");
        }

        // TextMeshProUGUI only flips m_isOrthographic in Awake(), and all TMP measurement scales
        // glyph metrics by 0.1 while it is false. Kit UI is routinely built and measured under a
        // still-inactive window, so every TMP component must come from TopiaForgeTmp.Create, which sets the
        // flag at creation. Three separate widgets have shipped this bug (TopiaForgeBadge, TopiaForgeLabel, TopiaForgeButton);
        // this test keeps a fourth from ever compiling in.
        private static void AllTmpComponentsCreatedThroughTopiaForgeTmp()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var factory = Path.Combine(kitRoot, "Text", "TopiaForgeTmp.cs");
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
                        + " — create kit TMP labels via TopiaForgeTmp.Create, which sets isOrthographic before any measurement can run.");
                }
            }

            if (!factorySeen)
            {
                throw new InvalidOperationException("Text/TopiaForgeTmp.cs not found under " + kitRoot + " — did the TMP factory move? Update this test.");
            }
        }

        private static void CanvasSortingUsesAllocator()
        {
            var repoRoot = Program.FindRepoRoot();
            var allocator = Path.Combine(repoRoot, "src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeLayers.cs");
            var roots = new[]
            {
                Path.Combine(repoRoot, "src", "TopiaForge.Mods.UnityUi"),
                Path.Combine(repoRoot, "src", "TopiaForge.ModManager"),
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
                            " — allocate and assign canvas order through TopiaForgeLayers.");
                    }
                }
            }
        }

        private static void ComponentLookupHonorsUnityNullSemantics()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
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
                    + " — use TopiaForgeComponents.GetOrAdd so Unity fake-null values are rejected.");
            }
        }

        private static void ProcessWideUiStateHasShutdownPath()
        {
            var repoRoot = Program.FindRepoRoot();
            var kitRoot = Path.Combine(repoRoot, "src", "TopiaForge.Mods.UnityUi");
            var topiaForgeUi = File.ReadAllText(Path.Combine(kitRoot, "TopiaForgeUi.cs"));
            var toast = File.ReadAllText(Path.Combine(kitRoot, "Widgets", "TopiaForgeToast.cs"));
            var host = File.ReadAllText(Path.Combine(kitRoot, "UiHost.cs"));
            var plugin = File.ReadAllText(
                Path.Combine(repoRoot, "src", "TopiaForge.ModManager", "TopiaForgeModManagerPlugin.cs"));
            var project = File.ReadAllText(
                Path.Combine(kitRoot, "TopiaForge.Mods.UnityUi.csproj"));
            var brandBundle = File.ReadAllText(
                Path.Combine(kitRoot, "Rendering", "TopiaForgeBrandBundle.cs"));

            RequireSource(topiaForgeUi, "public static void Shutdown()", "TopiaForgeUi must expose an idempotent loader shutdown path.");
            RequireSource(topiaForgeUi, "TopiaForgeToasts.Reset();", "TopiaForgeUi shutdown must clear process-wide toast state.");
            RequireSource(topiaForgeUi, "TopiaForgeRuntime.Shutdown();", "TopiaForgeUi shutdown must stop its hidden runtime driver.");
            RequireSource(topiaForgeUi, "TopiaForgeLog.Reset();", "TopiaForgeUi shutdown must release owner logging delegates.");
            RequireSource(topiaForgeUi, "while (Hosts.Count > 0)", "TopiaForgeUi shutdown must reclaim forgotten hosts.");
            RequireSource(host, "TopiaForgeUi.OnHostDisposed(this);", "Disposed hosts must leave the global host registry.");
            RequireSource(toast, "TopiaForgeToastHost.Instance.Layer(", "The toast canvas must be owned by its UiHost.");
            RequireSource(toast, "Queue.Clear();", "Toast shutdown must clear pending notifications.");
            RequireSource(toast, "Views.Clear();", "Toast shutdown must release pooled view references.");
            if (toast.Contains("TopiaForgeLayers.CreateCanvas(\"TopiaForgeToasts\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The process-wide toast canvas bypasses UiHost ownership and cannot be reliably torn down.");
            }

            RequireSource(plugin, "TopiaForgeUi.Shutdown();", "The manager plugin must invoke TopiaForgeUi shutdown from OnDestroy.");
            RequireSource(plugin,
                "PluginGuid = \"io.github.furroxide.topiaforge.modmanager\"",
                "The BepInEx plugin GUID must use the canonical TopiaForge identifier.");
            RequireSource(project,
                "LogicalName=\"TopiaForge.Mods.UnityUi.topiaforge-ui.bundle\"",
                "The TopiaForge UI bundle must use the renamed embedded resource identity.");
            RequireSource(brandBundle,
                "ResourceName = \"TopiaForge.Mods.UnityUi.topiaforge-ui.bundle\"",
                "The TopiaForge UI loader must resolve the renamed embedded resource identity.");
        }

        private static void RequireSource(string source, string expected, string message)
        {
            if (!source.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void UnityUiReferencesStayInternal()
        {
            var repositoryRoot = Program.FindRepoRoot();
            var expected = new HashSet<string>(new[]
            {
                "mods/TopiaForge.UgcLiveSync/TopiaForge.UgcLiveSync.csproj",
                "mods/TopiaForge.UiGallery/TopiaForge.UiGallery.csproj",
                "mods/TopiaForge.Worlds/TopiaForge.Worlds.csproj",
                "src/TopiaForge.ModManager/TopiaForge.ModManager.csproj"
            }, StringComparer.Ordinal);
            var unityUiReference = new Regex(
                @"<(?:Project|Package)Reference\b[^>]*\bInclude\s*=\s*""[^""]*TopiaForge\.Mods\.UnityUi(?:\.csproj)?""",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var actual = new HashSet<string>(StringComparer.Ordinal);

            foreach (var project in Directory.EnumerateFiles(
                         repositoryRoot,
                         "*.csproj",
                         SearchOption.AllDirectories))
            {
                if (IsBuildOutput(project)
                    || IsOutsideTrackedTree(repositoryRoot, project)
                    || !unityUiReference.IsMatch(File.ReadAllText(project)))
                {
                    continue;
                }

                actual.Add(Path.GetRelativePath(repositoryRoot, project).Replace('\\', '/'));
            }

            if (!actual.SetEquals(expected))
            {
                throw new InvalidOperationException(
                    "UnityUi references must remain restricted to loader-owned providers and the QA gallery. " +
                    "Expected: " + string.Join(", ", expected.OrderBy(value => value, StringComparer.Ordinal)) +
                    "; actual: " + string.Join(", ", actual.OrderBy(value => value, StringComparer.Ordinal)) + ".");
            }

            foreach (var relative in actual)
            {
                var project = File.ReadAllText(Path.Combine(repositoryRoot, relative));
                RequireSource(
                    project,
                    "<TopiaForgeSafeProject>false</TopiaForgeSafeProject>",
                    relative + " must explicitly opt out of safe-mod analysis as an internal provider project.");
            }
        }

        private static void UiKitFilesStayReviewable()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
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
                        "TopiaForgeUi source file exceeds the 400-line responsibility boundary (" + lines + "): " + file);
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
                        + " — build TopiaForge-owned visuals through TopiaForgeUi. Read-only/native-game UI adapters may inspect existing components.");
                }
            }
        }

        private static void FirstPartyModsDoNotMutateGlobalTheme()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            var assignment = new Regex(
                @"TopiaForgeTheme\.(?:HighContrast|UiScale|ReducedMotion|MotionScale)\s*=",
                RegexOptions.CultureInvariant);

            foreach (var file in Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || !assignment.IsMatch(File.ReadAllText(file)))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "First-party mod mutates process-wide TopiaForgeTheme in " + file
                    + " — pass TopiaForgeAccessibilityProfile through TopiaForgeUiOptions or UiHost instead.");
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

        /// <summary>
        /// Reports whether a path lives outside the tracked source tree.
        /// </summary>
        /// <remarks>
        /// Repository-wide enumeration walks the working directory, not the git
        /// index, so it also descends into ignored trees. A nested git worktree,
        /// a vendored dependency, or any ignored scratch copy would otherwise be
        /// scanned and report the same project twice under a different prefix.
        /// </remarks>
        private static bool IsOutsideTrackedTree(string repositoryRoot, string file)
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            foreach (var segment in relative.Split('/'))
            {
                if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, ".claude", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "third_party", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
