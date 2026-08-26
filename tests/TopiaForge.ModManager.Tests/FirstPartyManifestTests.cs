using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyManifestTests
    {
        // Robotopia compatibility is declared per mod, by whether the mod actually resolves GameCode symbols.
        // A mod with declared native bindings may claim only the build its bindings were verified against; a mod
        // that rides the SDK alone gets a bounded range so an ordinary game update does not brick it. The range
        // ceiling sits roughly two published steps above the pin (builds advance by about +100 each), so it
        // admits the current build and the next one, and nothing beyond a review.
        private const string BoundGameRange = "0.0.2409";
        private const string SdkOnlyGameRange = ">=0.0.2409 <0.0.2600";
        private const string InstalledGameVersion = "0.0.2409";

        // Exactly the mods carrying a bindings/<id>.gamebindings.json manifest.
        private static readonly HashSet<string> GameBoundModIds = new(StringComparer.Ordinal)
        {
            "io.github.furroxide.topiaforge.chronos",
            "io.github.furroxide.topiaforge.creatorcontent",
            "io.github.furroxide.topiaforge.no-feedback-url",
            "io.github.furroxide.topiaforge.perffixes",
            "io.github.furroxide.topiaforge.performance",
            "io.github.furroxide.topiaforge.prompts",
            "io.github.furroxide.topiaforge.robotkit",
            "io.github.furroxide.topiaforge.worlds",
        };
        private const string LoaderRange = ">=0.1.0-rc.1 <0.2.0";
        private const string SdkRange = ">=0.1.0-rc.1 <0.2.0";

        internal static void Run()
        {
            var repoRoot = FindRepoRoot();
            var manifestPaths = Directory.GetFiles(
                    Path.Combine(repoRoot, "mods"),
                    "topiaforge.mod.json",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            Assert(manifestPaths.Count == 14, "exactly 14 first-party manifests should be release-audited");

            var manifests = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in manifestPaths)
            {
                var manifest = ModManifestJson.LoadFile(path);
                manifests.Add(manifest.Id, manifest);
                Assert(manifest.SchemaVersion == ModManifest.CurrentSchemaVersion,
                    manifest.Id + " must use the TopiaForge manifest schema discriminator");
                Assert(manifest.Id.StartsWith("io.github.furroxide.topiaforge.", StringComparison.Ordinal),
                    manifest.Id + " must live under the first-party TopiaForge identifier namespace");
                Assert(manifest.EntryAssembly.StartsWith("TopiaForge.", StringComparison.Ordinal)
                    && manifest.EntryType.StartsWith("TopiaForge.", StringComparison.Ordinal),
                    manifest.Id + " must expose only TopiaForge assembly and type identities");
                var isGameBound = GameBoundModIds.Contains(manifest.Id);
                var bindingsPath = Path.Combine(repoRoot, "bindings", manifest.Id + ".gamebindings.json");
                Assert(File.Exists(bindingsPath) == isGameBound,
                    manifest.Id + " must be classified by whether it actually declares native game bindings");
                Assert(manifest.SupportedGameVersionRange == (isGameBound ? BoundGameRange : SdkOnlyGameRange),
                    isGameBound
                        ? manifest.Id + " declares native bindings, so it must pin the audited Robotopia build"
                        : manifest.Id + " rides the SDK alone, so it must declare the bounded game range");
                Assert(manifest.SupportedLoaderVersionRange == LoaderRange,
                    manifest.Id + " must declare the compatible V1 loader line");
                Assert(manifest.SupportedSdkVersionRange == SdkRange,
                    manifest.Id + " must declare the compatible V1 SDK line");
                Assert(manifest.License == "AGPL-3.0-or-later",
                    manifest.Id + " must use the approved project license");
                Assert(manifest.LicenseFiles.SequenceEqual(new[] { "LICENSE" }),
                    manifest.Id + " must include the approved project license in its package");
                AssertDeclaredLicenseFilesResolve(repoRoot, path, manifest);
                Assert(!manifest.Capabilities.Contains("ai", StringComparer.OrdinalIgnoreCase),
                    manifest.Id + " must use descriptive capabilities instead of the ambiguous ai alias");

                var errors = ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext(InstalledGameVersion, requireKnownGameVersion: true));
                Assert(errors.Count == 0, manifest.Id + " failed strict compatibility validation: " + string.Join("; ", errors));

                var projectPath = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.csproj").Single();
                var project = XDocument.Load(projectPath);
                var version = project.Descendants("Version").Select(element => element.Value).SingleOrDefault();
                var fileVersion = project.Descendants("FileVersion").Select(element => element.Value).SingleOrDefault();
                var informational = project.Descendants("InformationalVersion").Select(element => element.Value).SingleOrDefault();
                Assert(version == manifest.Version, manifest.Id + " project Version must match its manifest");
                var numericVersion = manifest.Version.Split('-', '+')[0] + ".0";
                Assert(fileVersion == numericVersion,
                    manifest.Id + " FileVersion must preserve the numeric release-line version");
                Assert(informational == manifest.Version, manifest.Id + " InformationalVersion must match its manifest");
                ValidateProjectAndLifecycleContract(path, manifest, project);
            }

            ValidateCatalogReferences(manifests);
            ValidateSafeTemplates(repoRoot);

            AssertCapabilities(
                manifests["io.github.furroxide.topiaforge.robotkit"],
                "network", "remote-ai", "player-token", "microphone", "speech-to-text");
            AssertCapabilities(
                manifests["io.github.furroxide.topiaforge.opposite-day"],
                "prompt-overrides", "remote-ai");
            AssertCapabilities(
                manifests["io.github.furroxide.topiaforge.prompts"],
                "prompt-overrides", "unsafe-native", "harmony-patch");
            Assert(manifests["io.github.furroxide.topiaforge.opposite-day"].Dependencies.ContainsKey(
                    "io.github.furroxide.topiaforge.prompts"),
                "Opposite Day must require the Prompts provider");
            Assert(manifests["io.github.furroxide.topiaforge.robotkit"].OptionalDependencies.ContainsKey(
                    "io.github.furroxide.topiaforge.prompts"),
                "RobotKit must declare its optional Prompts integration");
            // Sandbox and Creator Tools both host the shared Creator workbench, whose BeginConversation path
            // (mods/Shared/CreatorTools/CreatorWorkbench.Conversation.cs:78) reaches RobotKit's RoboApiClient and
            // POSTs player-typed text with the per-user Robotopia bearer token to api.tomatocake.dev. It is
            // config-gated, not absent, so PrivacyAndCapabilities.md:60 ("declare every capability its behavior
            // can exercise") requires network/remote-ai/player-token - the same set Zombies declares for its
            // opt-in JACK IN path. This assertion previously pinned that omission in place. What Sandbox
            // genuinely must not claim is voice: it has no Microphone or speech path.
            AssertLacksCapabilities(
                manifests["io.github.furroxide.topiaforge.sandbox"],
                "microphone", "speech-to-text");
            AssertCapabilities(
                manifests["io.github.furroxide.topiaforge.sandbox"],
                "network", "remote-ai", "player-token", "player-control");
            Assert(manifests["io.github.furroxide.topiaforge.creatorcontent"].Category == "Framework",
                "Creator Content must ship as a normal framework dependency");
            AssertCapabilities(
                manifests["io.github.furroxide.topiaforge.zombies"],
                "navigation", "scene-management", "network", "remote-ai", "player-token", "microphone", "speech-to-text");
            AssertAdvancedInterop(manifests, manifestPaths);
            Console.WriteLine("FirstPartyManifestTests passed.");
        }

        private static void AssertAdvancedInterop(
            IReadOnlyDictionary<string, ModManifest> manifests,
            IReadOnlyList<string> manifestPaths)
        {
            var advancedIds = new HashSet<string>(new[]
            {
                "io.github.furroxide.topiaforge.performance",
                "io.github.furroxide.topiaforge.perffixes",
                "io.github.furroxide.topiaforge.no-feedback-url",
                "io.github.furroxide.topiaforge.prompts",
                "io.github.furroxide.topiaforge.creatorcontent"
            }, StringComparer.OrdinalIgnoreCase);

            foreach (var manifest in manifests.Values)
            {
                var isAdvanced = advancedIds.Contains(manifest.Id);
                Assert(manifest.Capabilities.Contains("unsafe-native", StringComparer.OrdinalIgnoreCase) == isAdvanced,
                    manifest.Id + " must keep native access isolated to the allowlisted advanced providers and mods");
                var manifestPath = manifestPaths.Single(path => string.Equals(
                    ModManifestJson.LoadFile(path).Id,
                    manifest.Id,
                    StringComparison.OrdinalIgnoreCase));
                var projectPath = Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "*.csproj").Single();
                var project = XDocument.Load(projectPath);
                var hasInteropReference = project.Descendants("ProjectReference").Any(reference =>
                    (((string?)reference.Attribute("Include")) ?? string.Empty)
                    .Contains("TopiaForge.Mods.Interop.Unity", StringComparison.Ordinal));
                Assert(hasInteropReference == isAdvanced,
                    manifest.Id + " must " + (isAdvanced ? "use" : "not depend on")
                    + " the unstable native interop package");
            }
        }

        private static void AssertCapabilities(ModManifest manifest, params string[] required)
        {
            foreach (var capability in required)
            {
                Assert(manifest.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase),
                    manifest.Id + " must disclose capability " + capability);
            }
        }

        private static void AssertLacksCapabilities(ModManifest manifest, params string[] forbidden)
        {
            foreach (var capability in forbidden)
            {
                Assert(!manifest.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase),
                    manifest.Id + " must not disclose unused capability " + capability);
            }
        }

        /// <summary>
        /// A manifest that declares licenseFiles is promising those paths exist in the built package. No mod
        /// directory carries its own LICENSE: packing injects the shared mods/LICENSE for first-party ids
        /// (pack_helpers.dart, _packModProject). Asserting the declared string alone therefore proves nothing -
        /// if mods/LICENSE were moved or renamed, every package would ship an unsatisfied licenseFiles entry
        /// and no test would notice. This asserts the declaration actually resolves to a file on disk.
        /// </summary>
        private static void AssertDeclaredLicenseFilesResolve(
            string repoRoot,
            string manifestPath,
            ModManifest manifest)
        {
            var modDirectory = Path.GetDirectoryName(manifestPath)!;
            var sharedLicenseRoot = Path.Combine(repoRoot, "mods");
            foreach (var declared in manifest.LicenseFiles)
            {
                var local = Path.Combine(modDirectory, declared);
                var shared = Path.Combine(sharedLicenseRoot, declared);
                Assert(File.Exists(local) || File.Exists(shared),
                    manifest.Id + " declares licenseFiles entry '" + declared
                    + "' but neither " + Path.GetRelativePath(repoRoot, local).Replace('\\', '/')
                    + " nor " + Path.GetRelativePath(repoRoot, shared).Replace('\\', '/')
                    + " exists, so the packaged manifest would reference a missing file");
            }
        }

        /// <summary>
        /// Every first-party mod must build under exactly the MSBuild contract a community author gets from
        /// the analyzer package. The two buildTransitive imports carry the CompilerVisibleProperty wiring,
        /// the topiaforge.mod.json AdditionalFiles entry, and the TF1003 copied-SDK-assembly guard. Dropping
        /// either import is silent: TF1003 stops running, and without AdditionalFiles the manifest snapshot
        /// degrades to empty, which disables TF1004 and TF1006 rather than failing. Nothing else catches that,
        /// so it is asserted here.
        /// </summary>
        private static void ValidateAnalyzerContract(ModManifest manifest, XDocument project)
        {
            var imports = project.Descendants("Import")
                .Select(element => ((string?)element.Attribute("Project") ?? string.Empty).Replace('\\', '/'))
                .ToArray();
            foreach (var required in new[]
                     {
                         "buildTransitive/TopiaForge.Mods.Analyzers.props",
                         "buildTransitive/TopiaForge.Mods.Analyzers.targets"
                     })
            {
                Assert(imports.Any(import => import.EndsWith(required, StringComparison.OrdinalIgnoreCase)),
                    manifest.Id + " must import " + required
                    + " so it builds under the same analyzer contract as a community mod");
            }

            var analyzerReference = project.Descendants("ProjectReference").SingleOrDefault(reference =>
                ((string?)reference.Attribute("Include") ?? string.Empty)
                .EndsWith("TopiaForge.Mods.Analyzers.csproj", StringComparison.OrdinalIgnoreCase));
            Assert(analyzerReference != null,
                manifest.Id + " must reference the mod analyzers so TF1001-TF1008 run against it");
            Assert((string?)analyzerReference!.Attribute("OutputItemType") == "Analyzer",
                manifest.Id + " must consume TopiaForge.Mods.Analyzers as an analyzer, not as a library");
            Assert(string.Equals(
                    (string?)analyzerReference.Attribute("ReferenceOutputAssembly"),
                    "false",
                    StringComparison.OrdinalIgnoreCase),
                manifest.Id + " must not link the analyzer assembly into its own output");
        }

        private static void ValidateProjectAndLifecycleContract(
            string manifestPath,
            ModManifest manifest,
            XDocument project)
        {
            var directory = Path.GetDirectoryName(manifestPath)!;
            var assemblyName = project.Descendants("AssemblyName").Select(element => element.Value).SingleOrDefault();
            var targetFramework = project.Descendants("TargetFramework").Select(element => element.Value).SingleOrDefault();
            Assert(manifest.EntryAssembly == assemblyName + ".dll",
                manifest.Id + " entryAssembly must match the project AssemblyName");
            Assert(targetFramework == "netstandard2.1",
                manifest.Id + " must remain on Unity-compatible netstandard2.1");
            Assert(project.Descendants("ProjectReference").Any(reference =>
                    ((string?)reference.Attribute("Include") ?? string.Empty)
                    .EndsWith("TopiaForge.Mods.Abstractions.csproj", StringComparison.OrdinalIgnoreCase)),
                manifest.Id + " must compile against the public mod SDK");
            ValidateAnalyzerContract(manifest, project);

            var sourceFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var source = string.Join("\n", sourceFiles.Select(File.ReadAllText));
            var entryTypeName = manifest.EntryType.Substring(manifest.EntryType.LastIndexOf('.') + 1);
            var escapedType = Regex.Escape(entryTypeName);
            var derivesFromV1Base = Regex.IsMatch(
                source,
                @"\bclass\s+" + escapedType + @"\b[^\{]*:\s*[^\{]*\bTopiaForgeMod\b",
                RegexOptions.CultureInvariant);
            var implementsLegacyContract = Regex.IsMatch(
                source,
                @"\bclass\s+" + escapedType + @"\b[^\{]*:\s*[^\{]*\bITopiaForgeMod\b",
                RegexOptions.CultureInvariant);
            Assert(derivesFromV1Base && !implementsLegacyContract,
                manifest.Id + " entryType must derive exclusively from the V1 TopiaForgeMod base class");
            Assert(Regex.IsMatch(
                    source,
                    @"\bprotected\s+override\s+void\s+OnLoad\s*\(\s*\)",
                    RegexOptions.CultureInvariant),
                manifest.Id + " V1 entryType must override TopiaForgeMod.OnLoad()");

            if (manifest.Id == "io.github.furroxide.topiaforge.gravitygun"
                || manifest.Id == "io.github.furroxide.topiaforge.sandbox"
                || manifest.Id == "io.github.furroxide.topiaforge.zombies"
                || manifest.Id == "io.github.furroxide.topiaforge.opposite-day"
                || manifest.Id == "io.github.furroxide.topiaforge.uigallery")
            {
                // Linked sources ship inside the safe consumer's own assembly, so they must clear the same
                // bar as code that physically lives in the mod directory. The creator workbench
                // (mods/TopiaForge.Sandbox/CreatorTools) is scanned with the rest of Sandbox.
                ValidateSafeConsumerSource(
                    manifest,
                    project,
                    source + "\n" + ReadLinkedCompileSources(directory, project));
            }

            foreach (Match match in Regex.Matches(
                         source,
                         @"LoadConfig\s*\(\s*new\s+(?<type>[A-Za-z_][A-Za-z0-9_.]*)\s*\(",
                         RegexOptions.CultureInvariant))
            {
                var qualified = match.Groups["type"].Value;
                var configType = qualified.Substring(qualified.LastIndexOf('.') + 1);
                Assert(Regex.IsMatch(
                        source,
                        @"\[DataContract\][\s\S]{0,500}?\bclass\s+" + Regex.Escape(configType) + @"\b",
                        RegexOptions.CultureInvariant),
                    manifest.Id + " loaded config " + configType + " must be a DataContract in checked-in source");
            }
        }

        /// <summary>
        /// Reads every source a project pulls in by link, i.e. the &lt;Compile Include="..\..."&gt; items that
        /// resolve outside the project directory. Supports the literal and single-wildcard forms used under
        /// mods/ (..\Shared\SafeEvent.cs and ..\Shared\CreatorTools\*.cs).
        /// </summary>
        private static string ReadLinkedCompileSources(string projectDirectory, XDocument project)
        {
            var linked = new List<string>();
            foreach (var include in project.Descendants("Compile")
                         .Select(element => (string?)element.Attribute("Include"))
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => value!.Replace('\\', Path.DirectorySeparatorChar)))
            {
                var resolved = Path.GetFullPath(Path.Combine(projectDirectory, include));
                var directory = Path.GetDirectoryName(resolved);
                if (directory == null || !Directory.Exists(directory))
                {
                    continue;
                }

                var pattern = Path.GetFileName(resolved);
                if (pattern.Contains('*'))
                {
                    linked.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
                }
                else if (File.Exists(resolved))
                {
                    linked.Add(resolved);
                }
            }

            return string.Join(
                "\n",
                linked.Where(path => !IsBuildOutput(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(File.ReadAllText));
        }

        private static void ValidateSafeConsumerSource(
            ModManifest manifest,
            XDocument project,
            string source)
        {
            var forbiddenFragments = new[]
            {
                "UnityEngine",
                "GameCode",
                "Harmony",
                "System.Reflection",
                "ITopiaForgeMod",
                ".Hotkey("
            };
            foreach (var fragment in forbiddenFragments)
            {
                Assert(!source.Contains(fragment, StringComparison.Ordinal),
                    manifest.Id + " safe consumer source contains forbidden API fragment " + fragment);
            }

            Assert(!Regex.IsMatch(
                    source,
                    @"\bobject(?:\?|\s+[A-Za-z_][A-Za-z0-9_]*)",
                    RegexOptions.CultureInvariant),
                manifest.Id + " safe consumer source must not traffic in raw native object handles");
            Assert(!project.Descendants("Reference").Any(reference =>
            {
                var include = (string?)reference.Attribute("Include") ?? string.Empty;
                return include.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
                    || include.IndexOf("GameCode", StringComparison.OrdinalIgnoreCase) >= 0
                    || include.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0;
            }), manifest.Id + " safe consumer project must not reference engine, game, or patch assemblies");
        }

        private static void ValidateCatalogReferences(IReadOnlyDictionary<string, ModManifest> manifests)
        {
            foreach (var manifest in manifests.Values)
            {
                foreach (var dependency in manifest.Dependencies ?? new Dictionary<string, string>())
                {
                    Assert(manifests.TryGetValue(dependency.Key, out var target),
                        manifest.Id + " dependency " + dependency.Key + " is not a first-party catalog entry");
                    Assert(target != null && VersionUtil.AllowsRange(target.Version, dependency.Value),
                        manifest.Id + " dependency range " + dependency.Value + " excludes catalog version "
                        + target?.Version + " of " + dependency.Key);
                }

                foreach (var dependency in manifest.OptionalDependencies ?? new Dictionary<string, string>())
                {
                    Assert(manifests.TryGetValue(dependency.Key, out var target),
                        manifest.Id + " optional dependency " + dependency.Key + " is not a first-party catalog entry");
                    Assert(target != null && VersionUtil.AllowsRange(target.Version, dependency.Value),
                        manifest.Id + " optional dependency range " + dependency.Value + " excludes catalog version "
                        + target?.Version + " of " + dependency.Key);
                }

                foreach (var loadAfter in manifest.LoadAfter ?? new List<string>())
                {
                    Assert(manifests.ContainsKey(loadAfter),
                        manifest.Id + " loadAfter target " + loadAfter + " is not a first-party catalog entry");
                    Assert(!string.Equals(manifest.Id, loadAfter, StringComparison.OrdinalIgnoreCase),
                        manifest.Id + " cannot load after itself");
                }
            }
        }

        private static void ValidateSafeTemplates(string repoRoot)
        {
            var templatesRoot = Path.Combine(repoRoot, "templates", "mod");
            var templates = Directory.GetDirectories(templatesRoot)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert(templates.Length == 7,
                "V1 must ship exactly seven canonical mod templates");

            foreach (var template in templates)
            {
                var id = Path.GetFileName(template);
                var mainProject = Directory.GetFiles(template, "*.csproj", SearchOption.TopDirectoryOnly).Single();
                var projectText = File.ReadAllText(mainProject);
                var hasExpectedProjectReferences = id == "service"
                    ? projectText.Contains(
                        "ProjectReference Include=\"contracts\\{{ASSEMBLY_NAME}}.Api\\{{ASSEMBLY_NAME}}.Api.csproj\"",
                        StringComparison.Ordinal)
                    : !projectText.Contains("ProjectReference", StringComparison.Ordinal);
                Assert(hasExpectedProjectReferences
                    && projectText.Contains("TopiaForge.Mods.Abstractions", StringComparison.Ordinal)
                    && projectText.Contains("PackageReference", StringComparison.Ordinal)
                    && projectText.Contains("TopiaForgeSafeProject", StringComparison.Ordinal),
                    id + " template must use the safe exact-version SDK package contract");

                if (id == "service")
                {
                    var contractProjects = Directory.GetFiles(
                        Path.Combine(template, "contracts"),
                        "*.csproj",
                        SearchOption.AllDirectories);
                    Assert(contractProjects.Length == 1
                        && File.ReadAllText(contractProjects[0]).Contains("TopiaForgeSafeProject", StringComparison.Ordinal)
                        && !string.Equals(Path.GetFileName(mainProject), Path.GetFileName(contractProjects[0]), StringComparison.Ordinal),
                        "service template must isolate its exported API in one safe contract project");
                }

                var source = string.Join("\n", Directory.GetFiles(template, "*.cs", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(File.ReadAllText));
                foreach (var forbidden in new[]
                         {
                             "UnityEngine", "GameCode", "Harmony", "System.Reflection", "ITopiaForgeMod"
                         })
                {
                    Assert(!source.Contains(forbidden, StringComparison.Ordinal),
                        id + " template contains forbidden safe-project API " + forbidden);
                }

                Assert(source.Contains("TopiaForgeMod", StringComparison.Ordinal)
                    && source.Contains("protected override void OnLoad()", StringComparison.Ordinal),
                    id + " template must demonstrate the V1 authoring base class");
                Assert(!Regex.IsMatch(
                        source,
                        @"InputBinding\.(?:Key|GamepadButton)\s*\(\s*\""F(?:8|10)\""",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                    id + " template must not bind a reserved framework action");

                var testProjects = Directory.GetFiles(
                    Path.Combine(template, "tests"),
                    "*.csproj",
                    SearchOption.AllDirectories);
                var testSources = Directory.GetFiles(
                    Path.Combine(template, "tests"),
                    "*.cs",
                    SearchOption.AllDirectories);
                Assert(testProjects.Length == 1 && testSources.Length > 0
                    && File.ReadAllText(testProjects[0]).Contains("TopiaForge.Mods.Testing", StringComparison.Ordinal)
                    && testSources.Any(path => File.ReadAllText(path).Contains("[Test]", StringComparison.Ordinal)),
                    id + " template must include a real NUnit behavior test using TopiaForge.Mods.Testing");
            }
        }

        private static bool IsBuildOutput(string path)
        {
            var separator = Path.DirectorySeparatorChar;
            return path.Contains(separator + "bin" + separator, StringComparison.Ordinal)
                || path.Contains(separator + "obj" + separator, StringComparison.Ordinal);
        }

        private static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "TopiaForge.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate TopiaForge repository root.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("First-party manifest: " + message);
            }
        }
    }
}
