using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Versions against which a manifest is validated. Authoring tools may omit the game version;
    /// production install/scan paths should set <see cref="RequireKnownGameVersion"/>.
    /// </summary>
    public sealed class ManifestValidationContext
    {
        public ManifestValidationContext(
            string? gameVersion = null,
            string? loaderVersion = null,
            string? sdkVersion = null,
            bool requireKnownGameVersion = false,
            string? platform = null,
            string? architecture = null,
            IEnumerable<string>? contentTargets = null,
            bool enforceRuntimeCompatibility = false)
        {
            GameVersion = gameVersion;
            LoaderVersion = loaderVersion ?? TopiaForgeVersions.LoaderVersion;
            SdkVersion = sdkVersion ?? TopiaForgeVersions.SdkVersion;
            RequireKnownGameVersion = requireKnownGameVersion;
            EnforceRuntimeCompatibility = enforceRuntimeCompatibility;
            Platform = NormalizePlatform(
                enforceRuntimeCompatibility && platform == null ? DetectPlatform() : platform);
            Architecture = NormalizeArchitecture(
                enforceRuntimeCompatibility && architecture == null ? DetectArchitecture() : architecture);
            ContentTargets = NormalizeContentTargets(
                enforceRuntimeCompatibility && contentTargets == null
                    ? DefaultContentTargets(Platform)
                    : contentTargets);
        }

        public string? GameVersion { get; }
        public string LoaderVersion { get; }
        public string SdkVersion { get; }
        public bool RequireKnownGameVersion { get; }
        public string Platform { get; }
        public string Architecture { get; }
        public IReadOnlyList<string> ContentTargets { get; }
        public bool EnforceRuntimeCompatibility { get; }

        /// <summary>
        /// Validation for authoring tools that have no installed-game context. Loader and SDK constraints
        /// are still checked; a constrained game range is syntax-checked but not rejected as unknown.
        /// </summary>
        public static ManifestValidationContext Current { get; } = new ManifestValidationContext();

        /// <summary>Creates a strict context for selecting and loading packages in the current game process.</summary>
        public static ManifestValidationContext ForCurrentRuntime(
            string? gameVersion = null,
            string? loaderVersion = null,
            string? sdkVersion = null,
            bool requireKnownGameVersion = false)
        {
            return new ManifestValidationContext(
                gameVersion,
                loaderVersion,
                sdkVersion,
                requireKnownGameVersion,
                enforceRuntimeCompatibility: true);
        }

        /// <summary>
        /// Normalizes host/runtime platform labels. Proton and Wine run Robotopia's Windows player and therefore
        /// use the Windows package platform, matching the launcher's existing install-layout model.
        /// </summary>
        public static string NormalizePlatform(string? value)
        {
            var normalized = NormalizeToken(value);
            switch (normalized)
            {
                case "win":
                case "win32":
                case "win64":
                case "windows":
                case "windowsnative":
                case "proton":
                case "wine":
                case "linuxproton":
                case "linux-proton":
                    return "windows";
                case "mac":
                case "macos":
                case "osx":
                case "darwin":
                    return "macos";
                case "linux":
                    return "linux";
                default:
                    return normalized;
            }
        }

        /// <summary>Normalizes common process-architecture aliases to manifest architecture ids.</summary>
        public static string NormalizeArchitecture(string? value)
        {
            var normalized = NormalizeToken(value);
            switch (normalized)
            {
                case "amd64":
                case "x86-64":
                case "x86_64":
                case "x64":
                    return "x64";
                case "aarch64":
                case "arm64":
                    return "arm64";
                default:
                    return normalized;
            }
        }

        /// <summary>Normalizes, de-duplicates, and sorts host-supported content target ids.</summary>
        public static IReadOnlyList<string> NormalizeContentTargets(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Select(NormalizeToken)
                .Where(value => value.Length != 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static IEnumerable<string> DefaultContentTargets(string platform)
        {
            // Managed-only mods can explicitly declare code on every supported runtime.
            yield return "code";
            switch (platform)
            {
                case "windows":
                    // Proton/Wine intentionally reaches this Windows-player target too.
                    yield return "standalonewindows64";
                    break;
                case "macos":
                    yield return "standaloneosx";
                    break;
                case "linux":
                    yield return "standalonelinux64";
                    break;
            }
        }

        private static string DetectPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
            return string.Empty;
        }

        private static string DetectArchitecture()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case System.Runtime.InteropServices.Architecture.X64:
                    return "x64";
                case System.Runtime.InteropServices.Architecture.Arm64:
                    return "arm64";
                case System.Runtime.InteropServices.Architecture.X86:
                    return "x86";
                case System.Runtime.InteropServices.Architecture.Arm:
                    return "arm";
                default:
                    return RuntimeInformation.ProcessArchitecture.ToString();
            }
        }

        private static string NormalizeToken(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
