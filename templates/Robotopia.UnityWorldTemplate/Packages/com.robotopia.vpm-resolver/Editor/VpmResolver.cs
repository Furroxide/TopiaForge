// Robotopia VPM resolver (editor-only). On project open it compares Packages/vpm-manifest.json against the
// installed packages and restores any that are missing — the git-clone self-heal. Downloads come from the
// listings in Packages/vpm-resolver-repos.json (a JSON array of index.json locations: file paths, file://, or
// https URLs); the QuantumWorks launcher/CLI write that file. If no listing is configured it points the user at
// the launcher. Mirrors VRChat's com.vrchat.core.vpm-resolver.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace Robotopia.VpmResolver
{
    [InitializeOnLoad]
    internal static class VpmResolver
    {
        private const string SessionKey = "Robotopia.VpmResolver.Checked";

        static VpmResolver()
        {
            // Run once per editor session, after asset import settles.
            if (!SessionState.GetBool(SessionKey, false))
            {
                SessionState.SetBool(SessionKey, true);
                EditorApplication.delayCall += () => Check(prompt: true);
            }
        }

        [MenuItem("Robotopia/Resolve VPM Packages")]
        private static void ResolveMenu() => Check(prompt: false, force: true);

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string PackagesDir => Path.Combine(ProjectRoot, "Packages");

        // Reads vpm-manifest.json, finds locked packages whose folder is missing, and restores them.
        private static void Check(bool prompt, bool force = false)
        {
            try
            {
                var manifestPath = Path.Combine(PackagesDir, "vpm-manifest.json");
                if (!File.Exists(manifestPath))
                {
                    if (force)
                    {
                        Debug.Log("[Robotopia VPM] No Packages/vpm-manifest.json — nothing to resolve.");
                    }

                    return;
                }

                var manifest = MiniJson.Parse(File.ReadAllText(manifestPath)) as Dictionary<string, object>;
                var locked = manifest != null && manifest.TryGetValue("locked", out var l)
                    ? l as Dictionary<string, object>
                    : null;
                if (locked == null || locked.Count == 0)
                {
                    if (force)
                    {
                        Debug.Log("[Robotopia VPM] vpm-manifest.json has no locked packages.");
                    }

                    return;
                }

                var missing = new List<string>();
                foreach (var id in locked.Keys)
                {
                    if (!File.Exists(Path.Combine(PackagesDir, id, "package.json")))
                    {
                        missing.Add(id);
                    }
                }

                if (missing.Count == 0)
                {
                    if (force)
                    {
                        EditorUtility.DisplayDialog(
                            "QuantumWorks VPM",
                            "All packages are present — nothing to restore.",
                            "OK");
                    }

                    return;
                }

                var repos = ReadRepos();
                if (repos.Count == 0)
                {
                    EditorUtility.DisplayDialog(
                        "QuantumWorks VPM",
                        $"{missing.Count} package(s) are missing:\n  {string.Join("\n  ", missing)}\n\n" +
                        "No package listings are configured. Resolve them from the QuantumWorks launcher " +
                        "(Developer → Manage packages) or run `robotopia unity resolve`.",
                        "OK");
                    return;
                }

                if (prompt && !Application.isBatchMode && !EditorUtility.DisplayDialog(
                        "QuantumWorks VPM",
                        $"Restore {missing.Count} missing package(s)?\n  {string.Join("\n  ", missing)}",
                        "Restore",
                        "Later"))
                {
                    return;
                }

                Restore(missing, locked, repos);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Robotopia VPM] Resolve check failed: " + ex.Message);
            }
        }

        private static List<string> ReadRepos()
        {
            var result = new List<string>();
            var reposPath = Path.Combine(PackagesDir, "vpm-resolver-repos.json");
            if (!File.Exists(reposPath))
            {
                return result;
            }

            try
            {
                if (MiniJson.Parse(File.ReadAllText(reposPath)) is List<object> list)
                {
                    foreach (var item in list)
                    {
                        if (item is string url && !string.IsNullOrWhiteSpace(url))
                        {
                            result.Add(url.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Robotopia VPM] Could not read vpm-resolver-repos.json: " + ex.Message);
            }

            return result;
        }

        private static void Restore(
            List<string> missing,
            Dictionary<string, object> locked,
            List<string> repos)
        {
            // Merge the package -> version -> url catalog from every configured listing.
            var catalog = new Dictionary<string, Dictionary<string, string>>(); // id -> (version -> url)
            foreach (var repo in repos)
            {
                try
                {
                    MergeListing(LoadText(repo), repo, catalog);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Robotopia VPM] Could not read listing {repo}: {ex.Message}");
                }
            }

            var restored = 0;
            foreach (var id in missing)
            {
                var version = locked[id] is Dictionary<string, object> entry
                    && entry.TryGetValue("version", out var v)
                        ? v as string
                        : null;

                if (!catalog.TryGetValue(id, out var versions) || versions.Count == 0)
                {
                    Debug.LogWarning($"[Robotopia VPM] {id} not found in any configured listing.");
                    continue;
                }

                string url = null;
                if (version != null && versions.TryGetValue(version, out var exact))
                {
                    url = exact;
                }
                else
                {
                    // Fall back to any available version.
                    foreach (var pair in versions)
                    {
                        url = pair.Value;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                try
                {
                    InstallPackage(id, url);
                    restored++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Robotopia VPM] Failed to install {id}: {ex.Message}");
                }
            }

            if (restored > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[Robotopia VPM] Restored {restored} package(s).");
            }
        }

        private static void MergeListing(
            string json,
            string listingLocation,
            Dictionary<string, Dictionary<string, string>> catalog)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
            {
                return;
            }

            if (!(root.TryGetValue("packages", out var p) && p is Dictionary<string, object> packages))
            {
                return;
            }

            foreach (var packageEntry in packages)
            {
                if (!(packageEntry.Value is Dictionary<string, object> package))
                {
                    continue;
                }

                if (!(package.TryGetValue("versions", out var vs) && vs is Dictionary<string, object> versions))
                {
                    continue;
                }

                if (!catalog.TryGetValue(packageEntry.Key, out var versionMap))
                {
                    versionMap = new Dictionary<string, string>();
                    catalog[packageEntry.Key] = versionMap;
                }

                foreach (var versionEntry in versions)
                {
                    if (versionEntry.Value is Dictionary<string, object> manifest
                        && manifest.TryGetValue("url", out var url)
                        && url is string urlString)
                    {
                        versionMap[versionEntry.Key] = ResolvePackageLocation(urlString, listingLocation);
                    }
                }
            }
        }

        private static void InstallPackage(string id, string url)
        {
            var target = Path.Combine(PackagesDir, id);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
            Directory.CreateDirectory(target);
            var targetFull = Path.GetFullPath(target);

            // Extract via ZipArchive (System.IO.Compression core) rather than ZipFile.ExtractToDirectory — the
            // latter lives in System.IO.Compression.FileSystem, which is not always referenced under Unity.
            using (var stream = new MemoryStream(LoadBytes(url)))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/"))
                    {
                        continue; // directory entry
                    }

                    var destPath = Path.GetFullPath(Path.Combine(target, name));
                    // Zip-slip guard: never write outside the target package folder.
                    if (destPath != targetFull &&
                        !destPath.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        throw new Exception("Zip entry escapes the target: " + entry.FullName);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    using (var entryStream = entry.Open())
                    using (var fileStream = File.Create(destPath))
                    {
                        entryStream.CopyTo(fileStream);
                    }
                }
            }

            Debug.Log($"[Robotopia VPM] Installed {id} -> {target}");
        }

        private static string LoadText(string location)
        {
            if (IsHttp(location))
            {
                using (var client = new TimedWebClient())
                {
                    return client.DownloadString(location);
                }
            }

            return File.ReadAllText(LocalPath(location));
        }

        private static byte[] LoadBytes(string location)
        {
            if (IsHttp(location))
            {
                using (var client = new TimedWebClient())
                {
                    return client.DownloadData(location);
                }
            }

            return File.ReadAllBytes(LocalPath(location));
        }

        private static string ResolvePackageLocation(string location, string listingLocation)
        {
            if (string.IsNullOrWhiteSpace(location) ||
                IsHttp(location) ||
                location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(location))
            {
                return location;
            }

            if (IsHttp(listingLocation))
            {
                return new Uri(new Uri(listingLocation), location).ToString();
            }

            var listingPath = Path.GetFullPath(LocalPath(listingLocation));
            var baseDir = Path.GetDirectoryName(listingPath);
            return Path.Combine(baseDir ?? ProjectRoot, location);
        }

        // A WebClient with a finite timeout so a hung/slow listing host can't freeze the editor indefinitely.
        private sealed class TimedWebClient : WebClient
        {
            protected override System.Net.WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = 30000; // 30s
                }

                return request;
            }
        }

        private static bool IsHttp(string location) =>
            location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static string LocalPath(string location)
        {
            if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(location).LocalPath;
            }

            return location;
        }
    }
}
