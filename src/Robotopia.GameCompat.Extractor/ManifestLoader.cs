using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robotopia.GameCompat;

namespace Robotopia.GameCompat.Extractor
{
    internal static class ManifestLoader
    {
        public const string BaselineRelativePath = "baselines/gamecode.surface.baseline.json";
        private const int MaxManifestBytes = 1024 * 1024;
        private const int MaxManifestFiles = 256;

        public static List<(BindingManifest manifest, string path)> LoadAll(string repoRoot)
        {
            var result = new List<(BindingManifest, string)>();
            var dir = Path.Combine(repoRoot, "bindings");
            if (!Directory.Exists(dir))
            {
                return result;
            }

            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The GameCompat bindings directory must not be a symbolic link: " + dir);
            }

            var files = Directory.GetFiles(dir, "*.gamebindings.json")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (files.Count > MaxManifestFiles)
            {
                throw new InvalidDataException(
                    "The GameCompat binding set exceeds the " + MaxManifestFiles + " file safety limit.");
            }

            foreach (var file in files)
            {
                var json = ExtractorFileIo.ReadStableUtf8(
                    file,
                    MaxManifestBytes,
                    "GameCompat binding manifest");
                result.Add((BindingManifest.Parse(json), file));
            }

            return result;
        }

        public static IReadOnlyList<BindingManifest> Manifests(string repoRoot) =>
            LoadAll(repoRoot).Select(x => x.manifest).ToList();

        // The directory that holds bindings/ + baselines/. In the dev repo that is the repo root (found by walking
        // up to the .slnx). In a shipped/consumer install there is no repo, so the launcher bundles bindings/ and
        // baselines/ next to the extractor exe — fall back to that layout so `verify` works with no source tree.
        public static string? FindDataRoot()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "RobotopiaModManager.slnx")))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }

            // Bundled layout: bindings/ sits beside the exe.
            if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "bindings")))
            {
                return AppContext.BaseDirectory;
            }

            return null;
        }
    }
}
