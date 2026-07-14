using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class PackageInstaller
    {
        public const string PackageExtension = ".topiaforgemod";

        private const long MaxPackageBytes = 512L * 1024 * 1024;
        private const int MaxArchiveEntries = 8192;
        private const long MaxArchiveEntryBytes = 1024L * 1024 * 1024;
        private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
        private const long MaxManifestBytes = 1024L * 1024;
        public PackageInstallResult Install(string packagePath, ManagerPaths paths, ManagerState state, bool restartRequired)
        {
            return Install(
                packagePath,
                paths,
                state,
                restartRequired,
                ManifestValidationContext.Current);
        }

        public PackageInstallResult Install(
            string packagePath,
            ManagerPaths paths,
            ManagerState state,
            bool restartRequired,
            ManifestValidationContext validationContext)
        {
            if (validationContext == null)
            {
                throw new ArgumentNullException(nameof(validationContext));
            }

            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return PackageInstallResult.Fail("Package file does not exist: " + packagePath);
            }

            if (!string.Equals(Path.GetExtension(packagePath), PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return PackageInstallResult.Fail("Package file must use the " + PackageExtension + " extension.");
            }

            paths.EnsureCreated();
            var stagingPath = Path.Combine(paths.Staging, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingPath);

            try
            {
                EnsurePackageSize(new FileInfo(packagePath).Length);
                ExtractToSafeDirectory(packagePath, stagingPath);
                var manifestPath = Path.Combine(stagingPath, "topiaforge.mod.json");
                if (!File.Exists(manifestPath))
                {
                    return PackageInstallResult.Fail("Package is missing topiaforge.mod.json.");
                }

                var manifest = JsonUtil.LoadFile(manifestPath, new ModManifest());
                var errors = ManifestValidator.Validate(manifest, validationContext);
                if (errors.Count > 0)
                {
                    return PackageInstallResult.Fail(errors);
                }

                var entryAssemblyPath = Path.Combine(stagingPath, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
                {
                    return PackageInstallResult.Fail("entryAssembly was not found in package: " + manifest.EntryAssembly);
                }

                var targetPath = paths.GetPackagePath(manifest.Id, manifest.Version);
                var rollbackPath = CommitStagedDirectory(stagingPath, targetPath, paths.Staging);
                try
                {
                    var existing = state.Find(manifest.Id);
                    state.Upsert(manifest, enabled: existing?.Enabled ?? true, restartRequired: restartRequired);
                }
                catch (Exception stateError)
                {
                    try
                    {
                        RestoreCommittedDirectory(targetPath, rollbackPath);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new IOException(
                            "Package files were installed but state update and rollback both failed. " +
                            "The previous package remains at: " + rollbackPath,
                            new AggregateException(stateError, rollbackError));
                    }

                    throw;
                }

                TryDelete(rollbackPath);
                PruneOtherVersions(paths, manifest.Id, manifest.Version);

                return PackageInstallResult.Success(manifest, targetPath);
            }
            catch (Exception ex)
            {
                return PackageInstallResult.Fail(ex.Message);
            }
            finally
            {
                TryDelete(stagingPath);
            }
        }

        /// <summary>
        /// Installs every .topiaforgemod file waiting in the package-inbox. When the inbox holds several
        /// versions of the same mod, only the highest version is installed and the rest are marked
        /// superseded. Successfully processed files are consumed (deleted, or renamed to *.installed when
        /// the delete is blocked); failed installs leave their file in place so the user can inspect it.
        /// </summary>
        public IReadOnlyList<InboxInstallResult> InstallInbox(ManagerPaths paths, ManagerState state, bool restartRequired)
        {
            return InstallInbox(
                paths,
                state,
                restartRequired,
                ManifestValidationContext.Current);
        }

        public IReadOnlyList<InboxInstallResult> InstallInbox(
            ManagerPaths paths,
            ManagerState state,
            bool restartRequired,
            ManifestValidationContext validationContext)
        {
            if (validationContext == null)
            {
                throw new ArgumentNullException(nameof(validationContext));
            }

            var results = new List<InboxInstallResult>();
            if (!Directory.Exists(paths.PackageInbox))
            {
                return results;
            }

            var files = Directory.GetFiles(paths.PackageInbox, "*.topiaforgemod", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                return results;
            }

            // Pick one winner per mod id up front (highest parseable version); everything else for that id
            // is superseded. Files whose manifest cannot be pre-read stay winners of their own group so the
            // normal install path can produce the real, actionable error.
            var winners = new Dictionary<
                string,
                (string File, VersionUtil.ParsedSemanticVersion Version, bool HasValidVersion)>(
                StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var manifest = TryReadPackedManifest(file);
                var id = manifest != null && !string.IsNullOrWhiteSpace(manifest.Id) ? manifest.Id : file;
                fileToId[file] = id;
                if (!groups.TryGetValue(id, out var group))
                {
                    group = new List<string>();
                    groups[id] = group;
                }

                group.Add(file);
                var hasValidVersion = VersionUtil.TryParseSemantic(manifest?.Version, out var version);
                if (!winners.TryGetValue(id, out var best) ||
                    (hasValidVersion &&
                     (!best.HasValidVersion || version.CompareTo(best.Version) > 0)))
                {
                    winners[id] = (file, version, hasValidVersion);
                }
            }

            foreach (var file in files)
            {
                var groupId = fileToId[file];
                if (!string.Equals(winners[groupId].File, file, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // superseded — handled after its winner installs
                }

                var install = Install(file, paths, state, restartRequired, validationContext);
                var result = new InboxInstallResult(file, install, superseded: false);
                if (install.Ok)
                {
                    Consume(result);
                }

                results.Add(result);

                foreach (var loser in groups[groupId])
                {
                    if (string.Equals(loser, file, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Only consume superseded files once the winner actually installed; otherwise leave
                    // the whole group on disk for inspection.
                    var supersededResult = new InboxInstallResult(loser, null, superseded: true);
                    if (install.Ok)
                    {
                        Consume(supersededResult);
                    }

                    results.Add(supersededResult);
                }
            }

            return results;
        }

        // Reads just the manifest out of a packed .topiaforgemod zip; null when the file or manifest is
        // unreadable (the caller then routes the file through the normal install path for a real error).
        private static ModManifest? TryReadPackedManifest(string packagePath)
        {
            try
            {
                EnsurePackageSize(new FileInfo(packagePath).Length);
                using (var file = File.OpenRead(packagePath))
                {
                    EnsurePackageSize(file.Length);
                    PreflightArchiveDirectory(file);
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                    {
                        var entries = ValidateArchiveEntries(archive);
                        var entry = entries.SingleOrDefault(candidate =>
                            !candidate.IsDirectory &&
                            string.Equals(candidate.PortablePath, "topiaforge.mod.json", StringComparison.Ordinal));
                        if (entry == null)
                        {
                            return null;
                        }

                        using (var buffer = ReadEntryToMemory(entry.Entry, MaxManifestBytes))
                        {
                            return JsonUtil.Deserialize<ModManifest>(buffer);
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static void Consume(InboxInstallResult result)
        {
            try
            {
                File.Delete(result.FilePath);
                result.Consumed = true;
            }
            catch (Exception)
            {
                // A locked file (AV scan, Explorer preview) cannot be deleted; renaming it out of the
                // *.topiaforgemod pattern keeps it from being reprocessed while preserving the bytes.
                try
                {
                    var renamed = result.FilePath + ".installed";
                    if (File.Exists(renamed))
                    {
                        File.Delete(renamed);
                    }

                    File.Move(result.FilePath, renamed);
                    result.Consumed = true;
                }
                catch (Exception ex)
                {
                    result.ConsumeError = ex.Message;
                }
            }
        }

        // Superseded sibling versions would otherwise accumulate forever and, once their manifest schema
        // ages out, produce a startup warning per launch. Deletes are best-effort: a mid-session upgrade
        // has the old version's DLL loaded/locked, and the startup prune sweeps it next boot.
        private static void PruneOtherVersions(ManagerPaths paths, string id, string keepVersion)
        {
            var idRoot = paths.GetPackageIdPath(id);
            if (!Directory.Exists(idRoot))
            {
                return;
            }

            foreach (var versionDirectory in Directory.GetDirectories(idRoot))
            {
                if (string.Equals(Path.GetFileName(versionDirectory), keepVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(versionDirectory, true);
                }
                catch
                {
                    // Locked by a loaded assembly; the startup prune retries when nothing is loaded.
                }
            }
        }

        private static void ExtractToSafeDirectory(string zipPath, string destination)
        {
            using (var file = File.OpenRead(zipPath))
            {
                EnsurePackageSize(file.Length);
                PreflightArchiveDirectory(file);
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entries = ValidateArchiveEntries(archive);
                    var buffer = new byte[81920];
                    long extractedBytes = 0;
                    foreach (var entry in entries)
                    {
                        var targetPath = ResolveExtractionPath(destination, entry.PortablePath);

                        if (entry.IsDirectory)
                        {
                            Directory.CreateDirectory(targetPath);
                            continue;
                        }

                        var targetDirectory = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDirectory))
                        {
                            Directory.CreateDirectory(targetDirectory);
                        }

                        var entryLimit = string.Equals(
                            entry.PortablePath,
                            "topiaforge.mod.json",
                            StringComparison.Ordinal)
                            ? MaxManifestBytes
                            : MaxArchiveEntryBytes;
                        extractedBytes = ExtractEntry(
                            entry.Entry,
                            targetPath,
                            buffer,
                            extractedBytes,
                            entryLimit);
                    }
                }
            }
        }

        private static void PreflightArchiveDirectory(FileStream file)
        {
            const int endRecordBytes = 22;
            const int maxCommentBytes = ushort.MaxValue;
            const uint endRecordSignature = 0x06054b50;
            const uint centralHeaderSignature = 0x02014b50;

            var originalPosition = file.Position;
            try
            {
                if (file.Length < endRecordBytes)
                {
                    throw new InvalidDataException("Package has no valid ZIP end record.");
                }

                var tailLength = (int)Math.Min(file.Length, endRecordBytes + maxCommentBytes);
                var tail = new byte[tailLength];
                file.Position = file.Length - tailLength;
                ReadExactly(file, tail, tail.Length);

                var endOffset = -1;
                for (var offset = tail.Length - endRecordBytes; offset >= 0; offset--)
                {
                    if (ReadUInt32(tail, offset) != endRecordSignature)
                    {
                        continue;
                    }

                    var commentLength = ReadUInt16(tail, offset + 20);
                    if (offset + endRecordBytes + commentLength == tail.Length)
                    {
                        endOffset = offset;
                        break;
                    }
                }

                if (endOffset < 0)
                {
                    throw new InvalidDataException("Package has no valid ZIP end record.");
                }

                var diskNumber = ReadUInt16(tail, endOffset + 4);
                var centralDisk = ReadUInt16(tail, endOffset + 6);
                var entriesOnDisk = ReadUInt16(tail, endOffset + 8);
                var entryCount = ReadUInt16(tail, endOffset + 10);
                var centralBytes = ReadUInt32(tail, endOffset + 12);
                var centralOffset = ReadUInt32(tail, endOffset + 16);
                if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk != entryCount)
                {
                    throw new InvalidDataException("Multi-disk package archives are not supported.");
                }

                // The package caps make ZIP64 unnecessary (512 MiB compressed, <=8192 entries,
                // <=2 GiB expanded). Reject its sentinel values so entry counts are known before
                // ZipArchive allocates one object per central-directory record.
                if (entryCount == ushort.MaxValue || centralBytes == uint.MaxValue || centralOffset == uint.MaxValue)
                {
                    throw new InvalidDataException("ZIP64 package archives are not supported.");
                }

                if (entryCount > MaxArchiveEntries)
                {
                    throw new InvalidDataException(
                        "Package contains too many archive entries (maximum " + MaxArchiveEntries + ").");
                }

                var absoluteEndOffset = file.Length - tailLength + endOffset;
                var centralEnd = (long)centralOffset + centralBytes;
                if (centralEnd != absoluteEndOffset)
                {
                    throw new InvalidDataException("Package has an invalid ZIP central directory.");
                }

                file.Position = centralOffset;
                var header = new byte[46];
                long expandedBytes = 0;
                for (var index = 0; index < entryCount; index++)
                {
                    ReadExactly(file, header, header.Length);
                    if (ReadUInt32(header, 0) != centralHeaderSignature)
                    {
                        throw new InvalidDataException("Package has an invalid ZIP central-directory entry.");
                    }

                    var flags = ReadUInt16(header, 8);
                    var method = ReadUInt16(header, 10);
                    var compressedBytes = ReadUInt32(header, 20);
                    var entryBytes = ReadUInt32(header, 24);
                    var nameLength = ReadUInt16(header, 28);
                    var extraLength = ReadUInt16(header, 30);
                    var commentLength = ReadUInt16(header, 32);
                    var startDisk = ReadUInt16(header, 34);
                    var localHeaderOffset = ReadUInt32(header, 42);
                    if ((flags & 1) != 0)
                    {
                        throw new InvalidDataException("Encrypted package entries are not supported.");
                    }

                    if (method != 0 && method != 8)
                    {
                        throw new InvalidDataException(
                            "Package uses an unsupported ZIP compression method: " + method + ".");
                    }

                    if (compressedBytes == uint.MaxValue || entryBytes == uint.MaxValue ||
                        localHeaderOffset == uint.MaxValue)
                    {
                        throw new InvalidDataException("ZIP64 package entries are not supported.");
                    }

                    if (startDisk != 0)
                    {
                        throw new InvalidDataException("Multi-disk package archives are not supported.");
                    }

                    if (entryBytes > MaxArchiveEntryBytes)
                    {
                        throw new InvalidDataException(
                            "Package entry exceeds the " + MaxArchiveEntryBytes + " byte limit.");
                    }

                    if (expandedBytes > MaxExtractedBytes - entryBytes)
                    {
                        throw new InvalidDataException(
                            "Package expands beyond the " + MaxExtractedBytes + " byte limit.");
                    }

                    expandedBytes += entryBytes;
                    var variableBytes = (long)nameLength + extraLength + commentLength;
                    if (file.Position > centralEnd - variableBytes)
                    {
                        throw new InvalidDataException("Package has a truncated ZIP central directory.");
                    }

                    file.Position += variableBytes;
                }

                if (file.Position != centralEnd)
                {
                    throw new InvalidDataException("Package ZIP entry count does not match its central directory.");
                }
            }
            finally
            {
                file.Position = originalPosition;
            }
        }

        private static IReadOnlyList<ValidatedArchiveEntry> ValidateArchiveEntries(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new InvalidDataException(
                    "Package contains too many archive entries (maximum " + MaxArchiveEntries + ").");
            }

            var entries = new List<ValidatedArchiveEntry>(archive.Entries.Count);
            var pathKinds = new Dictionary<string, bool>(StringComparer.Ordinal);
            var requiredDirectories = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                var portablePath = NormalizeArchivePath(entry.FullName, out var collisionKey);
                if (pathKinds.ContainsKey(collisionKey))
                {
                    throw new InvalidDataException(
                        "Package contains a duplicate path or portable collision: " + entry.FullName);
                }

                var unixType = ((uint)entry.ExternalAttributes >> 16) & 0xF000u;
                if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    (unixType != 0 && unixType != 0x4000u && unixType != 0x8000u))
                {
                    throw new InvalidDataException(
                        "Package contains a symbolic link or special file: " + entry.FullName);
                }

                var isDirectory = unixType == 0x4000u ||
                    string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                if (isDirectory && entry.Length != 0)
                {
                    throw new InvalidDataException("Package directory entry contains data: " + entry.FullName);
                }

                if (!isDirectory)
                {
                    if (entry.Length < 0 || entry.Length > MaxArchiveEntryBytes)
                    {
                        throw new InvalidDataException(
                            "Package entry exceeds the " + MaxArchiveEntryBytes + " byte limit: " + entry.FullName);
                    }

                    if (string.Equals(portablePath, "topiaforge.mod.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(portablePath, "topiaforge.mod.json", StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "The package manifest path must be exactly topiaforge.mod.json.");
                        }

                        if (entry.Length > MaxManifestBytes)
                        {
                            throw new InvalidDataException(
                                "topiaforge.mod.json exceeds the " + MaxManifestBytes + " byte limit.");
                        }
                    }

                    if (totalBytes > MaxExtractedBytes - entry.Length)
                    {
                        throw new InvalidDataException(
                            "Package expands beyond the " + MaxExtractedBytes + " byte limit.");
                    }

                    totalBytes += entry.Length;
                }

                var parentPath = collisionKey;
                while (true)
                {
                    var separator = parentPath.LastIndexOf('/');
                    if (separator < 0)
                    {
                        break;
                    }

                    parentPath = parentPath.Substring(0, separator);
                    if (pathKinds.TryGetValue(parentPath, out var parentIsFile) && parentIsFile)
                    {
                        throw new InvalidDataException(
                            "Package path is nested beneath a file: " + entry.FullName);
                    }

                    requiredDirectories.Add(parentPath);
                }

                if (!isDirectory && requiredDirectories.Contains(collisionKey))
                {
                    throw new InvalidDataException(
                        "Package file conflicts with an existing directory path: " + entry.FullName);
                }

                pathKinds.Add(collisionKey, !isDirectory);
                entries.Add(new ValidatedArchiveEntry(entry, portablePath, isDirectory));
            }

            return entries;
        }

        private static string NormalizeArchivePath(string archivePath, out string collisionKey)
        {
            collisionKey = string.Empty;
            if (string.IsNullOrWhiteSpace(archivePath) || archivePath.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("Package contains an empty or invalid archive path.");
            }

            var portable = archivePath.Replace('\\', '/');
            while (portable.EndsWith("/", StringComparison.Ordinal))
            {
                portable = portable.Substring(0, portable.Length - 1);
            }

            if (portable.StartsWith("/", StringComparison.Ordinal) ||
                (portable.Length >= 2 && char.IsLetter(portable[0]) && portable[1] == ':'))
            {
                throw new InvalidDataException("Package contains an unsafe or non-portable path: " + archivePath);
            }

            if (!PortablePackagePath.TryValidate(
                    portable,
                    out var normalized,
                    out collisionKey,
                    out var pathError))
            {
                throw new InvalidDataException(
                    "Package contains an unsafe or non-portable path: " + archivePath + " (" + pathError + ").");
            }

            return normalized;
        }

        private static string ResolveExtractionPath(string destination, string portablePath)
        {
            var localPath = portablePath.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                return PathSafety.CombineRelativeChild(destination, localPath);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException(
                    "Package contains a path outside the install directory: " + portablePath,
                    ex);
            }
        }

        private static long ExtractEntry(
            ZipArchiveEntry entry,
            string targetPath,
            byte[] buffer,
            long extractedBytes,
            long entryLimit)
        {
            long entryBytes = 0;
            using (var input = entry.Open())
            using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (entryBytes > entryLimit - read)
                    {
                        throw new InvalidDataException(
                            "Package entry expands beyond the " + entryLimit + " byte limit: " + entry.FullName);
                    }

                    if (extractedBytes > MaxExtractedBytes - read)
                    {
                        throw new InvalidDataException(
                            "Package expands beyond the " + MaxExtractedBytes + " byte limit.");
                    }

                    output.Write(buffer, 0, read);
                    entryBytes += read;
                    extractedBytes += read;
                }
            }

            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException("Package entry size changed while extracting: " + entry.FullName);
            }

            return extractedBytes;
        }

        private static MemoryStream ReadEntryToMemory(ZipArchiveEntry entry, long maximumBytes)
        {
            if (entry.Length < 0 || entry.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    "Package entry exceeds the " + maximumBytes + " byte limit: " + entry.FullName);
            }

            var buffer = new MemoryStream((int)entry.Length);
            try
            {
                using (var input = entry.Open())
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (total > maximumBytes - read)
                        {
                            throw new InvalidDataException(
                                "Package entry expands beyond the " + maximumBytes + " byte limit: " + entry.FullName);
                        }

                        buffer.Write(chunk, 0, read);
                        total += read;
                    }

                    if (total != entry.Length)
                    {
                        throw new InvalidDataException("Package entry size changed while reading: " + entry.FullName);
                    }
                }

                buffer.Position = 0;
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        private static void EnsurePackageSize(long packageBytes)
        {
            if (packageBytes < 0 || packageBytes > MaxPackageBytes)
            {
                throw new InvalidDataException(
                    "Package exceeds the " + MaxPackageBytes + " byte compressed-size limit.");
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    throw new InvalidDataException("Package ZIP metadata is truncated.");
                }

                offset += read;
            }
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                (bytes[offset + 1] << 8) |
                (bytes[offset + 2] << 16) |
                (bytes[offset + 3] << 24));
        }

        private static string CommitStagedDirectory(string stagingPath, string targetPath, string rollbackRoot)
        {
            var targetParent = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(targetParent))
            {
                throw new InvalidDataException("Package target path has no parent directory.");
            }

            Directory.CreateDirectory(targetParent);
            var rollbackPath = string.Empty;
            if (Directory.Exists(targetPath))
            {
                rollbackPath = Path.Combine(rollbackRoot, "rollback-" + Guid.NewGuid().ToString("N"));
                Directory.Move(targetPath, rollbackPath);
            }

            try
            {
                // Staging and Packages are siblings under the same manager root, so this is an
                // atomic directory rename on supported filesystems rather than a destructive copy.
                Directory.Move(stagingPath, targetPath);
                return rollbackPath;
            }
            catch (Exception commitError)
            {
                if (string.IsNullOrEmpty(rollbackPath) || !Directory.Exists(rollbackPath))
                {
                    throw;
                }

                try
                {
                    Directory.Move(rollbackPath, targetPath);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException(
                        "Package replacement and rollback both failed. The previous package remains at: " + rollbackPath,
                        new AggregateException(commitError, rollbackError));
                }

                throw;
            }
        }

        private static void RestoreCommittedDirectory(string targetPath, string rollbackPath)
        {
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, true);
            }

            if (!string.IsNullOrEmpty(rollbackPath) && Directory.Exists(rollbackPath))
            {
                Directory.Move(rollbackPath, targetPath);
            }
        }

        private sealed class ValidatedArchiveEntry
        {
            public ValidatedArchiveEntry(ZipArchiveEntry entry, string portablePath, bool isDirectory)
            {
                Entry = entry;
                PortablePath = portablePath;
                IsDirectory = isDirectory;
            }

            public ZipArchiveEntry Entry { get; }

            public string PortablePath { get; }

            public bool IsDirectory { get; }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Staging cleanup failure should not hide the install result.
            }
        }
    }

    /// <summary>One inbox file's outcome from <see cref="PackageInstaller.InstallInbox"/>.</summary>
    public sealed class InboxInstallResult
    {
        public InboxInstallResult(string filePath, PackageInstallResult? install, bool superseded)
        {
            FilePath = filePath;
            Install = install;
            Superseded = superseded;
        }

        public string FilePath { get; }

        /// <summary>Null when the file was skipped as superseded by a newer version in the same inbox.</summary>
        public PackageInstallResult? Install { get; }

        public bool Superseded { get; }

        public bool Consumed { get; internal set; }

        public string? ConsumeError { get; internal set; }
    }

    public sealed class PackageInstallResult
    {
        private PackageInstallResult(bool ok, ModManifest? manifest, string? installPath, IReadOnlyList<string> errors)
        {
            Ok = ok;
            Manifest = manifest;
            InstallPath = installPath;
            Errors = errors;
        }

        public bool Ok { get; }
        public ModManifest? Manifest { get; }
        public string? InstallPath { get; }
        public IReadOnlyList<string> Errors { get; }

        public static PackageInstallResult Success(ModManifest manifest, string installPath)
        {
            return new PackageInstallResult(true, manifest, installPath, Array.Empty<string>());
        }

        public static PackageInstallResult Fail(string error)
        {
            return new PackageInstallResult(false, null, null, new[] { error });
        }

        public static PackageInstallResult Fail(IReadOnlyList<string> errors)
        {
            return new PackageInstallResult(false, null, null, errors);
        }
    }
}
