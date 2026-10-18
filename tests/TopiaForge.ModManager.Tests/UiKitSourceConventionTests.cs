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
            TmpFactoryKeepsThinGlyphsDrawn();
            ComponentLookupHonorsUnityNullSemantics();
            CanvasSortingUsesAllocator();
            ProcessWideUiStateHasShutdownPath();
            ToastsPresentEveryViewFresh();
            PaperTextTonesReachLabels();
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

        // TMP sizes a glyph's quad to its outline plus 1.25 atlas texels. The body face's thin l and I then get
        // quads narrower than a pixel at UI scale 1.0, and the GPU skips a quad that covers no pixel centre: the
        // approved privacy text rendered "defau t" in the game. Every label needs extra padding, and a size set
        // first, because a deferred Awake restores TMP's defaults over both while the size is still unset.
        private static void TmpFactoryKeepsThinGlyphsDrawn()
        {
            var repoRoot = Program.FindRepoRoot();
            var factory = File.ReadAllText(Path.Combine(repoRoot, "src", "TopiaForge.Mods.UnityUi", "Text", "TopiaForgeTmp.cs"));
            var created = factory.IndexOf("AddComponent<TextMeshProUGUI>()", StringComparison.Ordinal);
            var returned = factory.IndexOf("return tmp;", StringComparison.Ordinal);
            var sized = Regex.Match(factory, @"\btmp\.fontSize\s*=(?!=)", RegexOptions.CultureInvariant);
            var padded = Regex.Match(factory, @"\btmp\.extraPadding\s*=\s*true\s*;", RegexOptions.CultureInvariant);
            if (created < 0 || returned < 0 || !sized.Success || !padded.Success
                || sized.Index < created || sized.Index > returned || padded.Index < created || padded.Index > returned)
            {
                throw new InvalidOperationException(
                    "TopiaForgeTmp.Create must give every label a size and extra padding before returning it; "
                    + "without them thin glyphs vanish from small kit text.");
            }

            var disabled = new Regex(@"\.extraPadding\s*=(?!=)(?!\s*true\b)", RegexOptions.CultureInvariant);
            foreach (var root in new[]
            {
                Path.Combine(repoRoot, "src", "TopiaForge.Mods.UnityUi"),
                Path.Combine(repoRoot, "src", "TopiaForge.ModManager"),
                Path.Combine(repoRoot, "mods"),
            })
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    if (!IsBuildOutput(file) && disabled.IsMatch(File.ReadAllText(file)))
                    {
                        throw new InvalidOperationException(
                            "TMP extra padding is switched off in " + file + " — kit text keeps it on so thin glyphs stay drawn.");
                    }
                }
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
            RequireSource(host, "TopiaForgeUi.OnHostDisposed(this)", "Disposed hosts must leave the global host registry.");
            RequireSource(toast, "TopiaForgeToastHost.Instance.Layer(", "The toast canvas must be owned by its UiHost.");
            RequireSource(toast, "Stack.Clear();", "Toast shutdown must clear pending notifications.");
            RequireSource(
                File.ReadAllText(Path.Combine(kitRoot, "Core", "TopiaForgeToastStack.cs")),
                "waiting.Clear();",
                "Clearing the toast stack must drop its waiting notifications.");
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

        // Toast views are pooled. A view was presented fresh only while it sat at its creation spot, so every toast after a
        // view's first kept the last exit's fade and slide, and each exit slid it 40 units further: in the game, every toast
        // after the first was invisible, overlay open or not. TopiaForgeToastStack says when a view is fresh, which the core
        // tests cover; the Unity side must place and bring in such a view, and slide exits from where toasts rest.
        private static void ToastsPresentEveryViewFresh()
        {
            var toast = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeToast.cs"));
            RequireSource(toast, "Stack.TryPlace(index, out var top, out var fresh)", "Toasts must be placed as the toast stack says.");
            RequireSource(toast, "view.Label.textWrappingMode = TextWrappingModes.Normal;", "A longer toast must wrap to its lines, not end after one.");
            RequireSource(toast, "view.Label.GetPreferredValues(ThreeLines, width, 0f).y", "A toast's text must stop at three lines, so a longer one ends in an ellipsis.");
            RequireSource(toast, "Stack.SetHeight(index, height);", "A toast that wraps must push the toasts below it down.");
            if (!Regex.IsMatch(toast, @"if \(fresh\)\s*\{[^}]*anchoredPosition = new Vector2\(restingX, targetY\);[^}]*TopiaForgeMotion\.ToastIn\(view\.Root, restingX\);"))
            {
                throw new InvalidOperationException("A fresh toast view must be placed at its slot and brought in, whatever it showed before.");
            }

            RequireSource(toast, "TopiaForgeMotion.ToastOut(view.Root, -TopiaForgeTokens.SafeMargin,", "A toast's exit must slide from where toasts rest, so exits never add up.");
            if (toast.Contains("Mathf.Approximately(position.x, 0f)", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A toast view's freshness must come from the toast stack, not from where the view sits.");
            }

            // The exit's completion frees the view, and the next waiting toast takes it at once. Under reduced motion, or with the
            // tween pool full, that completion runs inside ToastOut, so a slide started after it pushed the new toast 40 units
            // aside, partly off the screen, for as long as it showed. The slide must start before the completing fade.
            var presets = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Motion", "TopiaForgeMotionPresets.cs"));
            var exit = Regex.Match(presets, @"void ToastOut\([^)]*\)\s*\{(?<body>[^}]*)\}");
            var slide = exit.Groups["body"].Value.IndexOf("TopiaForgeTween.MoveX(", StringComparison.Ordinal);
            var fade = exit.Groups["body"].Value.IndexOf("TopiaForgeTween.FadeTo(", StringComparison.Ordinal);
            if (!exit.Success || slide < 0 || fade < 0 || slide > fade || !Regex.IsMatch(exit.Groups["body"].Value, @"FadeTo\([^;]*onDone\);"))
            {
                throw new InvalidOperationException("A toast's exit must start its slide before the fade whose completion hands the view to the next toast.");
            }
        }

        // Status and brand colors are made for fills and rings, and on Paper they fail WCAG AA as text (warning reads at
        // 2.7:1 on paper). The resolved theme takes each status tone's text color from TopiaForgeSchemes.PaperText, which
        // the core tests measure, and labels draw body text with it.
        private static void PaperTextTonesReachLabels()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var theme = File.ReadAllText(Path.Combine(kitRoot, "Theme", "TopiaForgeResolvedTheme.cs"));
            foreach (var role in new[] { "Primary", "Accent", "Success", "Warning", "Danger" })
            {
                RequireSource(theme, "TopiaForgeSchemes.PaperText(colors." + role + ", HighContrast)", "On Paper, " + role + " text must take its readable color.");
            }

            var label = File.ReadAllText(Path.Combine(kitRoot, "Widgets", "TopiaForgeLabel.cs"));
            RequireSource(label, "theme.TextToneColor(tone)", "Labels must draw body text in the tones' readable text colors.");
            if (Regex.IsMatch(label, @"text\.color = [Tt]heme\.ToneColor\(tone\)"))
            {
                throw new InvalidOperationException("A label sets a tone's fill color on its text instead of its readable text color.");
            }

            // A ghost button has no fill, so its label sits on the surface like any body text: in the brand orange it read at
            // 2.0 to 2.6:1 on Paper, in high contrast too (REFRESH, ADD TO FAVORITES, MUTE, COPY DETAILS and CLOSE among them).
            var button = File.ReadAllText(Path.Combine(kitRoot, "Widgets", "TopiaForgeButton.cs"));
            RequireSource(button, "SetContentColor(theme.TextToneColor(TopiaForgeTone.Primary));", "A ghost button's label must take the Primary tone's readable text color.");
            if (button.Contains("SetContentColor(theme.Primary)", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A button draws its label in the Primary fill color on its own surface.");
            }
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
