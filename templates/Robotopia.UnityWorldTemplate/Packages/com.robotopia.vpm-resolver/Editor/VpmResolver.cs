// Robotopia VPM recovery bridge (editor-only).
//
// This embedded package deliberately performs read-only checks. It never reads package listings, chooses
// versions, downloads archives, or changes Packages/. Missing or mismatched packages are restored explicitly
// through the hardened Robotopia launcher/CLI, which owns integrity checks, bounded extraction, staging, and
// rollback. Keeping the embedded code detection-only prevents project-open code from becoming a supply-chain
// or data-loss boundary.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Robotopia.VpmResolver
{
    [InitializeOnLoad]
    internal static class VpmResolver
    {
        private const string SessionKey = "Robotopia.VpmResolver.Checked";
        private const long MaxManifestBytes = 1024 * 1024;
        private const int MaxDisplayedProblems = 12;

        private static readonly Regex PackageIdPattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$",
            RegexOptions.CultureInvariant);

        private static readonly Regex SemanticVersionPattern = new Regex(
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
            "(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)" +
            "(?:\\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?" +
            "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
            RegexOptions.CultureInvariant);

        static VpmResolver()
        {
            // Run the read-only drift check once per editor session, after asset import settles.
            if (!SessionState.GetBool(SessionKey, false))
            {
                SessionState.SetBool(SessionKey, true);
                EditorApplication.delayCall += () => Check(prompt: true);
            }
        }

        [MenuItem("Robotopia/Resolve VPM Packages")]
        private static void ResolveMenu() => Check(prompt: true, force: true);

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string PackagesDir => Path.Combine(ProjectRoot, "Packages");

        private static void Check(bool prompt, bool force = false)
        {
            var manifestPath = Path.Combine(PackagesDir, "vpm-manifest.json");
            try
            {
                Dictionary<string, object> manifest;
                try
                {
                    manifest = ReadJsonObjectBounded(manifestPath, "vpm-manifest.json");
                }
                catch (FileNotFoundException)
                {
                    if (force)
                    {
                        Debug.Log("[Robotopia VPM] No Packages/vpm-manifest.json — nothing to inspect.");
                    }

                    return;
                }

                var declared = ReadDeclaredDependencies(manifest);
                Dictionary<string, object> locked = null;
                if (manifest.TryGetValue("locked", out var lockedValue))
                {
                    locked = lockedValue as Dictionary<string, object>;
                    if (locked == null)
                    {
                        throw new InvalidDataException("vpm-manifest.json 'locked' must be a JSON object.");
                    }
                }

                if (locked == null || locked.Count == 0)
                {
                    if (declared.Count > 0)
                    {
                        OfferSafeRecovery(declared, prompt);
                        return;
                    }

                    if (force)
                    {
                        Debug.Log("[Robotopia VPM] vpm-manifest.json has no locked packages.");
                    }

                    return;
                }

                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var problems = new List<string>();
                foreach (var package in locked)
                {
                    var id = package.Key;
                    if (!PackageIdPattern.IsMatch(id) || !seenIds.Add(id))
                    {
                        throw new InvalidDataException(
                            "vpm-manifest.json contains an invalid or case-colliding locked package id: " +
                            SafeDiagnostic(id));
                    }

                    if (!(package.Value is Dictionary<string, object> entry)
                        || !entry.TryGetValue("version", out var versionValue)
                        || !(versionValue is string lockedVersion)
                        || !IsSemanticVersion(lockedVersion))
                    {
                        throw new InvalidDataException(
                            "vpm-manifest.json has no valid exact semantic version for package " + id + ".");
                    }

                    if (!TryReadInstalledVersion(id, out var installedVersion, out var problem))
                    {
                        problems.Add(problem ?? $"{id} {lockedVersion} (missing)");
                    }
                    else if (!string.Equals(installedVersion, lockedVersion, StringComparison.Ordinal))
                    {
                        problems.Add($"{id} (locked {lockedVersion}, installed {installedVersion})");
                    }
                }

                if (problems.Count == 0)
                {
                    if (force)
                    {
                        EditorUtility.DisplayDialog(
                            "Robotopia VPM",
                            "All locked packages are present at their exact locked versions.",
                            "OK");
                    }

                    return;
                }

                OfferSafeRecovery(problems, prompt);
            }
            catch (Exception ex)
            {
                ReportInspectionFailure(ex, prompt);
            }
        }

        private static Dictionary<string, object> ReadJsonObjectBounded(string path, string label)
        {
            var json = VpmSafeFileReader.ReadStableUtf8(path, MaxManifestBytes, label);
            var parsed = MiniJson.Parse(json) as Dictionary<string, object>;
            if (parsed == null)
            {
                throw new InvalidDataException(label + " must contain a JSON object.");
            }

            return parsed;
        }

        private static List<string> ReadDeclaredDependencies(Dictionary<string, object> manifest)
        {
            var result = new List<string>();
            if (!manifest.TryGetValue("dependencies", out var dependenciesValue))
            {
                return result;
            }

            if (!(dependenciesValue is Dictionary<string, object> dependencies))
            {
                throw new InvalidDataException("vpm-manifest.json 'dependencies' must be a JSON object.");
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in dependencies)
            {
                if (!PackageIdPattern.IsMatch(dependency.Key) || !seenIds.Add(dependency.Key))
                {
                    throw new InvalidDataException(
                        "vpm-manifest.json contains an invalid or case-colliding dependency id: " +
                        SafeDiagnostic(dependency.Key));
                }

                if (!(dependency.Value is string range) || string.IsNullOrWhiteSpace(range))
                {
                    throw new InvalidDataException(
                        "vpm-manifest.json has no version range for dependency " + dependency.Key + ".");
                }

                result.Add(dependency.Key + " (declared but not locked)");
            }

            return result;
        }

        private static bool TryReadInstalledVersion(
            string id,
            out string version,
            out string problem)
        {
            version = null;
            problem = null;
            var packageJsonPath = Path.Combine(PackagesDir, id, "package.json");
            try
            {
                var packageJson = ReadJsonObjectBounded(packageJsonPath, id + "/package.json");
                if (!packageJson.TryGetValue("name", out var nameValue)
                    || !(nameValue is string packageName)
                    || !string.Equals(packageName, id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("package identity does not match its locked id");
                }

                if (!packageJson.TryGetValue("version", out var versionValue)
                    || !(versionValue is string packageVersion)
                    || !IsSemanticVersion(packageVersion))
                {
                    throw new InvalidDataException("package.json has no valid semantic version");
                }

                version = packageVersion;
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (Exception ex)
            {
                problem = $"{id} (installed package is invalid: {ex.Message})";
                return false;
            }
        }

        private static void OfferSafeRecovery(List<string> problems, bool prompt)
        {
            var command = BuildCliCommand();
            var visibleCount = Math.Min(problems.Count, MaxDisplayedProblems);
            var visible = problems.GetRange(0, visibleCount);
            var remainder = problems.Count - visibleCount;
            var problemText = "  " + string.Join("\n  ", visible);
            if (remainder > 0)
            {
                problemText += $"\n  … and {remainder} more";
            }

            var message =
                $"{problems.Count} locked package(s) are missing, invalid, or at the wrong version:\n" +
                problemText +
                "\n\nFor safety, this embedded package never downloads or extracts archives and never changes " +
                "Packages/. Use the launcher (Developer → Packages → Resolve All) or run:\n\n" +
                command +
                "\n\nThe CLI re-resolves declared dependency ranges and writes exact locked versions. Review " +
                "Packages/vpm-manifest.json after recovery.";

            Debug.LogWarning("[Robotopia VPM] " + message);
            if (!prompt || Application.isBatchMode)
            {
                return;
            }

            if (EditorUtility.DisplayDialog(
                    "Robotopia VPM recovery required",
                    message,
                    "Copy CLI command",
                    "Later"))
            {
                EditorGUIUtility.systemCopyBuffer = command;
                Debug.Log("[Robotopia VPM] Copied the safe resolve command to the clipboard.");
            }
        }

        private static void ReportInspectionFailure(Exception exception, bool showDialog)
        {
            var message =
                "The VPM lock could not be inspected safely: " + exception.Message +
                " No files were changed. Repair Packages/vpm-manifest.json or use launcher diagnostics before " +
                "attempting a restore.";
            Debug.LogError("[Robotopia VPM] " + message);
            if (showDialog && !Application.isBatchMode)
            {
                EditorUtility.DisplayDialog("Robotopia VPM inspection failed", message, "OK");
            }
        }

        private static bool IsSemanticVersion(string value) =>
            value != null && value.Length <= 128 && SemanticVersionPattern.IsMatch(value);

        private static string SafeDiagnostic(string value) =>
            value == null || value.Length <= 80 ? value : value.Substring(0, 80) + "…";

        private static string BuildCliCommand()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Double quotes are sufficient because Windows file names cannot contain a quote character.
                return "robotopia unity resolve \"" + ProjectRoot + "\"";
            }

            // POSIX single-quote escaping makes the copied command safe even when a project path has spaces,
            // dollar signs, backticks, or a literal single quote.
            var escapedRoot = ProjectRoot.Replace("'", "'\\''");
            return "robotopia unity resolve '" + escapedRoot + "'";
        }
    }
}
