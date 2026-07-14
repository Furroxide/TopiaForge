using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Robotopia.ModManager.Core;

namespace Robotopia.ModManager.Tests
{
    internal static class FirstPartyManifestTests
    {
        private const string GameRange = "0.0.2227";
        private const string LoaderRange = ">=0.2.0 <0.3.0";

        internal static void Run()
        {
            var repoRoot = FindRepoRoot();
            var manifestPaths = Directory.GetFiles(
                    Path.Combine(repoRoot, "mods"),
                    "robotopia.mod.json",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            Assert(manifestPaths.Count == 13, "exactly 13 first-party manifests should be release-audited");

            var manifests = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in manifestPaths)
            {
                var manifest = JsonUtil.LoadFile(path, new ModManifest());
                manifests.Add(manifest.Id, manifest);
                Assert(manifest.SupportedGameVersionRange == GameRange,
                    manifest.Id + " must pin the audited Robotopia build 2227");
                Assert(manifest.SupportedLoaderVersionRange == LoaderRange,
                    manifest.Id + " must declare the compatible 0.2 loader line");
                Assert(manifest.License == "NOASSERTION",
                    manifest.Id + " must use the fail-closed SPDX sentinel until owner licensing is resolved");
                Assert(!manifest.Permissions.Contains("ai", StringComparer.OrdinalIgnoreCase),
                    manifest.Id + " must use descriptive capabilities instead of the ambiguous ai alias");

                var errors = ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext("0.0.2227", requireKnownGameVersion: true));
                Assert(errors.Count == 0, manifest.Id + " failed strict compatibility validation: " + string.Join("; ", errors));

                var projectPath = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.csproj").Single();
                var project = XDocument.Load(projectPath);
                var version = project.Descendants("Version").Select(element => element.Value).SingleOrDefault();
                var fileVersion = project.Descendants("FileVersion").Select(element => element.Value).SingleOrDefault();
                var informational = project.Descendants("InformationalVersion").Select(element => element.Value).SingleOrDefault();
                Assert(version == manifest.Version, manifest.Id + " project Version must match its manifest");
                Assert(fileVersion == manifest.Version + ".0", manifest.Id + " FileVersion must match its manifest");
                Assert(informational == manifest.Version, manifest.Id + " InformationalVersion must match its manifest");
                ValidateProjectAndLifecycleContract(path, manifest, project);
            }

            ValidateCatalogReferences(manifests);

            AssertCapabilities(
                manifests["robotopia.robotkit"],
                "network", "remote-ai", "player-token", "microphone", "speech-to-text");
            AssertCapabilities(
                manifests["robotopia.sandbox"],
                "network", "remote-ai", "player-token", "microphone", "speech-to-text");
            AssertCapabilities(
                manifests["robotopia.zombies"],
                "remote-ai", "player-token", "microphone", "speech-to-text");
            Console.WriteLine("FirstPartyManifestTests passed.");
        }

        private static void AssertCapabilities(ModManifest manifest, params string[] required)
        {
            foreach (var capability in required)
            {
                Assert(manifest.Permissions.Contains(capability, StringComparer.OrdinalIgnoreCase),
                    manifest.Id + " must disclose capability " + capability);
            }
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
                    .EndsWith("Robotopia.Mods.Abstractions.csproj", StringComparison.OrdinalIgnoreCase)),
                manifest.Id + " must compile against the public mod SDK");

            var sourceFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var source = string.Join("\n", sourceFiles.Select(File.ReadAllText));
            var entryTypeName = manifest.EntryType.Substring(manifest.EntryType.LastIndexOf('.') + 1);
            var escapedType = Regex.Escape(entryTypeName);
            Assert(Regex.IsMatch(
                    source,
                    @"\bclass\s+" + escapedType + @"\b[^\{]*:\s*[^\{]*\bIRobotopiaMod\b",
                    RegexOptions.CultureInvariant),
                manifest.Id + " entryType must implement IRobotopiaMod in checked-in source");
            Assert(Regex.IsMatch(
                    source,
                    @"\bpublic\s+void\s+OnLoad\s*\(\s*IModContext\s+\w+\s*\)",
                    RegexOptions.CultureInvariant),
                manifest.Id + " entryType must expose OnLoad(IModContext)");
            Assert(Regex.IsMatch(
                    source,
                    @"\bpublic\s+void\s+OnUnload\s*\(\s*\)",
                    RegexOptions.CultureInvariant),
                manifest.Id + " entryType must expose OnUnload()");

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

        private static void ValidateCatalogReferences(IReadOnlyDictionary<string, ModManifest> manifests)
        {
            foreach (var manifest in manifests.Values)
            {
                foreach (var dependency in manifest.VpmDependencies ?? new Dictionary<string, string>())
                {
                    Assert(manifests.TryGetValue(dependency.Key, out var target),
                        manifest.Id + " dependency " + dependency.Key + " is not a first-party catalog entry");
                    Assert(target != null && VersionUtil.AllowsRange(target.Version, dependency.Value),
                        manifest.Id + " dependency range " + dependency.Value + " excludes catalog version "
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
                if (File.Exists(Path.Combine(current.FullName, "RobotopiaModManager.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate Robotopia repository root.");
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
