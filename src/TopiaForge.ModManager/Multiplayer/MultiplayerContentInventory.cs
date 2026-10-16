using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The packages this process runs, as multiplayer admission sees them. Each loaded package is described once, from its
    /// manifest and the SHA-256 of the archive its install receipt records. A package whose receipt cannot be read gets a
    /// deterministic hash of its installed files instead, so a hand-copied development build still has a stable identity
    /// that matches only the identical files elsewhere. Main thread only.
    /// </summary>
    internal sealed class MultiplayerContentInventory
    {
        private readonly List<LoadedPackage> packages = new List<LoadedPackage>();
        private IReadOnlyList<InternalContentDescriptor>? descriptors;
        private IReadOnlyList<MultiplayerAdmissionMod>? admissionMods;

        /// <summary>Gets the loaded packages, in load order.</summary>
        internal IReadOnlyList<LoadedPackage> Packages => packages;

        /// <summary>Gets the ids of packages the last description left out because their manifests exceed its bounds.</summary>
        internal IReadOnlyList<string> Skipped { get; private set; } = Array.Empty<string>();

        /// <summary>Records a package that loaded. A package loaded again under the same id replaces the earlier record.</summary>
        internal void Add(ModManifest manifest, string packagePath)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(packagePath)) throw new ArgumentException("A package path is required.", nameof(packagePath));
            Remove(manifest.Id);
            packages.Add(new LoadedPackage(manifest, packagePath));
            descriptors = null;
            admissionMods = null;
        }

        /// <summary>Forgets a package, as when its load failed after it was recorded.</summary>
        internal void Remove(string modId)
        {
            if (packages.RemoveAll(item => string.Equals(item.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase)) == 0) return;
            descriptors = null;
            admissionMods = null;
        }

        /// <summary>Gets one descriptor per loaded package, sorted by id; computed once and kept until the packages change.</summary>
        internal IReadOnlyList<InternalContentDescriptor> Descriptors
        {
            get
            {
                if (descriptors != null) return descriptors;
                var list = new List<InternalContentDescriptor>(packages.Count);
                var skipped = new List<string>();
                foreach (var package in packages)
                {
                    try
                    {
                        list.Add(Describe(package.Manifest, package.Sha256));
                    }
                    catch (ArgumentException)
                    {
                        // Validation keeps ids and versions within the descriptor's bounds; a package outside them is not shared.
                        skipped.Add(package.Manifest.Id);
                    }
                }

                list.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                Skipped = skipped.AsReadOnly();
                descriptors = list.AsReadOnly();
                return descriptors;
            }
        }

        /// <summary>Gets the server profile's mods for the admission planner: the real manifests with their archive hashes.</summary>
        internal IReadOnlyList<MultiplayerAdmissionMod> AdmissionMods
        {
            get
            {
                if (admissionMods != null) return admissionMods;
                var list = new List<MultiplayerAdmissionMod>(packages.Count);
                foreach (var package in packages) list.Add(new MultiplayerAdmissionMod(package.Manifest, package.Sha256));
                admissionMods = list.AsReadOnly();
                return admissionMods;
            }
        }

        /// <summary>
        /// Describes one manifest. A session mod without a readable protocol, or an unknown mode, is described as
        /// standalone: such a mod can run offline but never takes part in, or silently joins, a session.
        /// </summary>
        internal static InternalContentDescriptor Describe(ModManifest manifest, string packageSha256)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim();
            var mode = manifest.EffectiveMultiplayerMode;
            switch (mode)
            {
                case ModMultiplayerEffectiveMode.ImplicitShared:
                    return new InternalContentDescriptor(manifest.Id, manifest.Version, packageSha256, InternalContentMode.ImplicitShared, displayName: name);
                case ModMultiplayerEffectiveMode.ClientLocal:
                    return new InternalContentDescriptor(manifest.Id, manifest.Version, packageSha256, InternalContentMode.ClientLocal, displayName: name);
                case ModMultiplayerEffectiveMode.ServerOnly:
                    return new InternalContentDescriptor(manifest.Id, manifest.Version, packageSha256, InternalContentMode.ServerOnly, displayName: name);
                case ModMultiplayerEffectiveMode.Session:
                    var described = DescribeSession(manifest, packageSha256, name);
                    if (described != null) return described;
                    break;
            }

            return new InternalContentDescriptor(manifest.Id, manifest.Version, packageSha256, InternalContentMode.Standalone, displayName: name);
        }

        /// <summary>
        /// Gets a package's archive hash from its install receipt, or a deterministic hash of its files when the receipt is
        /// missing or unreadable.
        /// </summary>
        internal static string PackageSha256(string packagePath)
        {
            try
            {
                var receipt = JsonUtil.LoadFile<PackageInstallReceipt?>(Path.Combine(packagePath, PackageInstallReceipt.FileName), null);
                if (receipt != null && InternalText.IsSha256(receipt.SourceSha256)) return receipt.SourceSha256;
            }
            catch (Exception)
            {
                // An unreadable receipt, whatever the reason, falls back to the files themselves.
            }

            return PayloadSha256(packagePath);
        }

        /// <summary>
        /// Hashes the installed files, receipt excluded, in ordinal order of their portable paths: for each file its path,
        /// a zero byte, its length and its SHA-256, all under a label so the value can pass for no other hash. Links are
        /// not followed, as the receipt's own inventory refuses them, so nothing outside the package is ever read.
        /// </summary>
        internal static string PayloadSha256(string packagePath)
        {
            var root = Path.GetFullPath(packagePath);
            var files = new List<string>();
            if (Directory.Exists(root)) CollectFiles(root, root, files);
            files.Sort(StringComparer.Ordinal);
            using (var outer = SHA256.Create())
            using (var inner = SHA256.Create())
            {
                var label = Encoding.UTF8.GetBytes("TopiaForge package files v1\n");
                outer.TransformBlock(label, 0, label.Length, null, 0);
                foreach (var relative in files)
                {
                    var path = Encoding.UTF8.GetBytes(relative);
                    outer.TransformBlock(path, 0, path.Length, null, 0);
                    outer.TransformBlock(new byte[1], 0, 1, null, 0);
                    byte[] digest;
                    long length;
                    using (var stream = File.OpenRead(Path.Combine(root, relative)))
                    {
                        length = stream.Length;
                        digest = inner.ComputeHash(stream);
                    }

                    var lengthBytes = BitConverter.GetBytes(length);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
                    outer.TransformBlock(lengthBytes, 0, lengthBytes.Length, null, 0);
                    outer.TransformBlock(digest, 0, digest.Length, null, 0);
                }

                outer.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(outer.Hash!);
            }
        }

        private static void CollectFiles(string root, string directory, List<string> files)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    CollectFiles(root, entry, files);
                    continue;
                }

                var relative = entry.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                if (!string.Equals(relative, PackageInstallReceipt.FileName, StringComparison.Ordinal)) files.Add(relative);
            }
        }

        private static InternalContentDescriptor? DescribeSession(ModManifest manifest, string packageSha256, string name)
        {
            var multiplayer = manifest.Multiplayer!;
            var protocol = multiplayer.Protocol;
            if (protocol == null || string.IsNullOrEmpty(protocol.Version) || protocol.Version.Length > InternalContentLimits.MaximumVersionLength) return null;
            var presence = string.Equals(multiplayer.Presence, ModMultiplayerMetadata.OptionalPresence, StringComparison.Ordinal)
                ? InternalContentPresence.Optional
                : InternalContentPresence.Required;
            var range = string.IsNullOrEmpty(protocol.PeerVersionRange) ? null : protocol.PeerVersionRange;
            if (range != null && range.Length > InternalContentLimits.MaximumRangeLength) return null;
            var files = new List<InternalSynchronizedFile>();
            foreach (var path in multiplayer.SynchronizedFiles ?? new List<string>())
            {
                // A file without a packed hash is left out; the host's planner then refuses the copy, which is safe.
                if (string.IsNullOrEmpty(path) || path.Length > InternalContentLimits.MaximumPathLength) continue;
                if (manifest.Hashes == null || !manifest.Hashes.TryGetValue(path, out var hash) || hash == null) continue;
                var lower = hash.ToLowerInvariant();
                if (InternalText.IsSha256(lower) && files.Count < InternalContentLimits.MaximumSynchronizedFiles) files.Add(new InternalSynchronizedFile(path, lower));
            }

            return new InternalContentDescriptor(manifest.Id, manifest.Version, packageSha256, InternalContentMode.Session, presence, protocol.Version, range, files, name);
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>One loaded package: its manifest and the hash multiplayer compares it by.</summary>
        internal sealed class LoadedPackage
        {
            private string? sha256;

            internal LoadedPackage(ModManifest manifest, string packagePath)
            {
                Manifest = manifest;
                PackagePath = packagePath;
            }

            internal ModManifest Manifest { get; }

            internal string PackagePath { get; }

            /// <summary>Gets the package hash, read when first needed.</summary>
            internal string Sha256 => sha256 ??= PackageSha256(PackagePath);
        }
    }
}
