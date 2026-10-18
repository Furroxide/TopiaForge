using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The packages this process runs, as multiplayer admission sees them. Each loaded package is described once, from its
    /// manifest and the SHA-256 of the archive its install receipt records. The first look reads the receipts, one small
    /// file each, on the calling thread. A package whose receipt cannot be read gets a deterministic hash of its installed
    /// files instead, so a hand-copied development build still has a stable identity that matches only the identical files
    /// elsewhere. That hashing can take seconds, so it never runs on the calling thread: <see cref="Readiness"/> starts a
    /// <see cref="MultiplayerContentIdentification"/> on the scheduler, and the description answers once it finishes. Main
    /// thread only.
    /// </summary>
    internal sealed class MultiplayerContentInventory
    {
        private const int ChunkBytes = 64 * 1024;

        private readonly List<LoadedPackage> packages = new List<LoadedPackage>();
        private readonly IMultiplayerWorkScheduler scheduler;
        private IReadOnlyList<InternalContentDescriptor>? descriptors;
        private IReadOnlyList<MultiplayerAdmissionMod>? admissionMods;
        private MultiplayerContentIdentification? running;
        private IOException? lastFailure;
        private int generation;

        /// <param name="scheduler">Where identification runs: a background thread in the game.</param>
        internal MultiplayerContentInventory(IMultiplayerWorkScheduler scheduler)
        {
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>Gets the loaded packages, in load order.</summary>
        internal IReadOnlyList<LoadedPackage> Packages => packages;

        /// <summary>Gets the ids of packages the last description left out because their manifests exceed its bounds.</summary>
        internal IReadOnlyList<string> Skipped { get; private set; } = Array.Empty<string>();

        /// <summary>Gets how many identifications have started, for tests and diagnostics.</summary>
        internal int IdentificationsStarted { get; private set; }

        /// <summary>
        /// Gets whether every package is identified. Reading it starts identifying the packages that are not, when nothing
        /// has started yet: the first read after the packages change reads their receipts, and only packages without one
        /// are hashed, off this thread. A failure stays <see cref="InternalContentState.Unreadable"/> until
        /// <see cref="Descriptors"/> is read again or <see cref="RetryIdentification"/> runs. It never hashes or throws, and
        /// a settled read allocates nothing.
        /// </summary>
        internal InternalContentReadiness Readiness
        {
            get
            {
                if (descriptors != null) return InternalContentReadiness.Ready;
                Adopt();
                if (lastFailure != null) return Unreadable();
                IdentifyReceipted();
                if (AllIdentified()) return InternalContentReadiness.Ready;
                if (running == null) StartIdentification();
                Adopt();
                if (lastFailure != null) return Unreadable();
                if (AllIdentified()) return InternalContentReadiness.Ready;
                return running?.Progress ?? new InternalContentReadiness(InternalContentState.Pending, 0, 0, 0, 0);
            }
        }

        /// <summary>Records a package that loaded. A package loaded again under the same id replaces the earlier record.</summary>
        internal void Add(ModManifest manifest, string packagePath)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(packagePath)) throw new ArgumentException("A package path is required.", nameof(packagePath));
            Remove(manifest.Id);
            packages.Add(new LoadedPackage(manifest, packagePath));
            Changed();
        }

        /// <summary>Forgets a package, as when its load failed after it was recorded.</summary>
        internal void Remove(string modId)
        {
            if (packages.RemoveAll(item => string.Equals(item.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase)) == 0) return;
            Changed();
        }

        /// <summary>Forgets an identification failure and identifies again, receipts included, as the player's retry asks.</summary>
        internal void RetryIdentification()
        {
            lastFailure = null;
            if (descriptors != null || running != null) return;
            foreach (var package in packages) package.ReceiptTried = package.IsIdentified;
            IdentifyReceipted();
            if (!AllIdentified()) StartIdentification();
        }

        /// <summary>
        /// Gets one descriptor per loaded package, sorted by id; computed once every package is identified and kept until the
        /// packages change. Until then it throws <see cref="InternalContentNotReadyException"/>, never hashing on this thread.
        /// After a failed identification it throws an <see cref="IOException"/> that names the package and not its folder,
        /// once: the next read starts identifying again.
        /// </summary>
        internal IReadOnlyList<InternalContentDescriptor> Descriptors
        {
            get
            {
                if (descriptors != null) return descriptors;
                RequireIdentified();
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

        /// <summary>
        /// Gets the server profile's mods for the admission planner: the real manifests with their archive hashes. Like
        /// <see cref="Descriptors"/>, it answers only once every package is identified.
        /// </summary>
        internal IReadOnlyList<MultiplayerAdmissionMod> AdmissionMods
        {
            get
            {
                if (admissionMods != null) return admissionMods;
                RequireIdentified();
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
        /// missing or unreadable. It runs on the calling thread, so the runtime calls it only from identification.
        /// </summary>
        internal static string PackageSha256(string packagePath)
        {
            string? receipted = null;
            try
            {
                receipted = ReceiptSha256(packagePath);
            }
            catch (Exception)
            {
                // An unreadable receipt, whatever the reason, falls back to the files themselves.
            }

            return receipted ?? PayloadSha256(packagePath);
        }

        /// <summary>Gets the archive hash a package's install receipt records, or null when there is no usable receipt.</summary>
        internal static string? ReceiptSha256(string packagePath)
        {
            var receipt = JsonUtil.LoadFile<PackageInstallReceipt?>(Path.Combine(packagePath, PackageInstallReceipt.FileName), null);
            return receipt != null && InternalText.IsSha256(receipt.SourceSha256) ? receipt.SourceSha256 : null;
        }

        /// <summary>
        /// Hashes the installed files, receipt excluded, in ordinal order of their portable paths: for each file its path,
        /// a zero byte, its length and its SHA-256, all under a label so the value can pass for no other hash. Links are
        /// not followed, as the receipt's own inventory refuses them, so nothing outside the package is ever read.
        /// </summary>
        internal static string PayloadSha256(string packagePath) => PayloadSha256(packagePath, ListPayloadFiles(packagePath), null, null);

        /// <summary>Lists the files <see cref="PayloadSha256(string)"/> hashes, in its order, with their lengths for progress.</summary>
        internal static IReadOnlyList<PayloadFile> ListPayloadFiles(string packagePath)
        {
            var root = Path.GetFullPath(packagePath);
            var relatives = new List<string>();
            if (Directory.Exists(root)) CollectFiles(root, root, relatives);
            relatives.Sort(StringComparer.Ordinal);
            var files = new PayloadFile[relatives.Count];
            for (var index = 0; index < files.Length; index++)
            {
                var info = new FileInfo(Path.Combine(root, relatives[index]));
                files[index] = new PayloadFile(relatives[index], info.Exists ? info.Length : 0);
            }

            return files;
        }

        /// <summary>
        /// Hashes <paramref name="files"/> as <see cref="PayloadSha256(string)"/> does, a chunk at a time, reporting each
        /// chunk's bytes to <paramref name="progress"/> and stopping with <see cref="OperationCanceledException"/> once
        /// <paramref name="cancelled"/> says so.
        /// </summary>
        internal static string PayloadSha256(string packagePath, IReadOnlyList<PayloadFile> files, Action<long>? progress, Func<bool>? cancelled)
        {
            var root = Path.GetFullPath(packagePath);
            var buffer = new byte[ChunkBytes];
            using (var outer = SHA256.Create())
            using (var inner = SHA256.Create())
            {
                var label = Encoding.UTF8.GetBytes("TopiaForge package files v1\n");
                outer.TransformBlock(label, 0, label.Length, null, 0);
                foreach (var file in files)
                {
                    var path = Encoding.UTF8.GetBytes(file.RelativePath);
                    outer.TransformBlock(path, 0, path.Length, null, 0);
                    outer.TransformBlock(new byte[1], 0, 1, null, 0);
                    long length;
                    inner.Initialize();
                    using (var stream = File.OpenRead(Path.Combine(root, file.RelativePath)))
                    {
                        length = stream.Length;
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException();
                            inner.TransformBlock(buffer, 0, read, null, 0);
                            progress?.Invoke(read);
                        }

                        inner.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    }

                    var lengthBytes = BitConverter.GetBytes(length);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
                    outer.TransformBlock(lengthBytes, 0, lengthBytes.Length, null, 0);
                    var digest = inner.Hash!;
                    outer.TransformBlock(digest, 0, digest.Length, null, 0);
                }

                outer.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(outer.Hash!);
            }
        }

        private void Changed()
        {
            descriptors = null;
            admissionMods = null;
            lastFailure = null;
            generation++;
            running?.Cancel();
            running = null;
        }

        /// <summary>
        /// Identifies, on this thread, the packages whose install receipt records their archive hash: one small file each,
        /// read once per package, as the description always read it. Only packages without a usable receipt are left for
        /// <see cref="StartIdentification"/>. A settled pass allocates nothing.
        /// </summary>
        private void IdentifyReceipted()
        {
            for (var index = 0; index < packages.Count; index++)
            {
                var package = packages[index];
                if (package.IsIdentified || package.ReceiptTried) continue;
                package.ReceiptTried = true;
                string? hash = null;
                try
                {
                    hash = ReceiptSha256(package.PackagePath);
                }
                catch (Exception)
                {
                    // An unreadable receipt, whatever the reason, falls back to the files themselves.
                }

                if (hash != null) package.Identify(hash);
            }
        }

        private bool AllIdentified()
        {
            for (var index = 0; index < packages.Count; index++)
            {
                if (!packages[index].IsIdentified) return false;
            }

            return true;
        }

        /// <summary>Takes the finished identification's results on this thread; a stale or cancelled run is dropped.</summary>
        private void Adopt()
        {
            var finished = running;
            if (finished == null || !finished.IsCompleted) return;
            running = null;
            if (finished.Generation != generation || finished.IsCancelled) return;
            var pending = PendingPackages();
            if (pending.Count != finished.Count) return;
            for (var index = 0; index < pending.Count; index++)
            {
                var hash = finished.HashOf(index);
                if (hash != null) pending[index].Identify(hash);
            }

            lastFailure = finished.Failure;
        }

        private void StartIdentification()
        {
            var pending = PendingPackages();
            if (pending.Count == 0) return;
            var paths = new string[pending.Count];
            var names = new string[pending.Count];
            for (var index = 0; index < pending.Count; index++)
            {
                paths[index] = pending[index].PackagePath;
                names[index] = pending[index].DisplayName + " " + pending[index].Manifest.Version;
            }

            var identification = new MultiplayerContentIdentification(generation, paths, names);
            running = identification;
            IdentificationsStarted++;
            try
            {
                scheduler.Schedule(identification.Run);
            }
            catch (Exception exception)
            {
                // Nothing may hash on this thread instead; the content is unreadable until a retry can start the work.
                running = null;
                lastFailure = new IOException("This game's mods could not be identified for multiplayer (" + exception.GetType().Name + ").", exception);
            }
        }

        /// <summary>Throws unless every package is identified, starting or adopting identification on the way.</summary>
        private void RequireIdentified()
        {
            Adopt();
            ThrowFailure();
            IdentifyReceipted();
            if (AllIdentified()) return;
            if (running == null) StartIdentification();
            Adopt();
            ThrowFailure();
            if (!AllIdentified()) throw new InternalContentNotReadyException();
        }

        /// <summary>Throws a recorded failure once, as a fresh exception, so the next read identifies again.</summary>
        private void ThrowFailure()
        {
            var failure = lastFailure;
            if (failure == null) return;
            lastFailure = null;
            throw new IOException(failure.Message, failure.InnerException ?? failure);
        }

        private List<LoadedPackage> PendingPackages()
        {
            var pending = new List<LoadedPackage>();
            foreach (var package in packages)
            {
                if (!package.IsIdentified) pending.Add(package);
            }

            return pending;
        }

        private InternalContentReadiness Unreadable() =>
            new InternalContentReadiness(InternalContentState.Unreadable, 0, 0, 0, 0);

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

        /// <summary>One file a payload hash covers: its portable path and its length when listed.</summary>
        internal readonly struct PayloadFile
        {
            internal PayloadFile(string relativePath, long length)
            {
                RelativePath = relativePath;
                Length = length;
            }

            internal string RelativePath { get; }

            internal long Length { get; }
        }

        /// <summary>
        /// One loaded package: its manifest, its network class and, once identified, the hash multiplayer compares it by.
        /// </summary>
        internal sealed class LoadedPackage
        {
            private string? sha256;

            internal LoadedPackage(ModManifest manifest, string packagePath)
            {
                Manifest = manifest;
                PackagePath = packagePath;
                NetworkClass = ModNetworkClasses.Classify(manifest);
            }

            internal ModManifest Manifest { get; }

            internal string PackagePath { get; }

            /// <summary>
            /// Gets how the package takes part in sessions, from its effective manifest: no multiplayer block is shared
            /// implicitly, and a mode that cannot be read is standalone, so such a mod never goes online.
            /// </summary>
            internal ModNetworkClass NetworkClass { get; }

            /// <summary>
            /// Gets the name players see for the package, which a failure also gives it: its manifest's name, or its id
            /// without one. Never its folder, which names this player's own folders.
            /// </summary>
            internal string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name.Trim();

            /// <summary>Gets whether identification found the package's hash.</summary>
            internal bool IsIdentified => sha256 != null;

            /// <summary>Gets or sets whether the package's receipt was read on the main thread since it was last asked to.</summary>
            internal bool ReceiptTried { get; set; }

            /// <summary>Gets the package hash. Only identified packages have one; the inventory checks before it asks.</summary>
            internal string Sha256 => sha256 ?? throw new InternalContentNotReadyException();

            internal void Identify(string hash) => sha256 = hash;
        }
    }
}
