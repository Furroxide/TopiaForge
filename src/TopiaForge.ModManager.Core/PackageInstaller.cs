using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
        private const int MaxInboxEntries = 1024;
        private const int MaxInboxCandidates = 256;

        internal Action<string>? BeforeInboxInstallForTesting { get; set; }

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
            return InstallWithSource(
                packagePath,
                paths,
                state,
                restartRequired,
                validationContext,
                PackageInstallReceipt.LocalSource,
                expectedSourceSha256: null);
        }

        private PackageInstallResult InstallWithSource(
            string packagePath,
            ManagerPaths paths,
            ManagerState state,
            bool restartRequired,
            ManifestValidationContext validationContext,
            string sourceProvenance,
            string? expectedSourceSha256)
        {
            if (validationContext == null)
            {
                throw new ArgumentNullException(nameof(validationContext));
            }

            using (var preflight = PreflightPackage(packagePath, paths, validationContext))
            {
                if (!preflight.Ok)
                {
                    return PackageInstallResult.Fail(preflight.Errors);
                }

                if (expectedSourceSha256 != null &&
                    !string.Equals(preflight.SourceSha256, expectedSourceSha256, StringComparison.Ordinal))
                {
                    return PackageInstallResult.Fail(
                        "Package bytes changed after inbox preflight; the candidate was retained for inspection.");
                }

                var manifest = preflight.Manifest!;
                var stagingPath = preflight.StagingPath!;
                try
                {
                    state.Normalize();
                    var receipt = PackageInstallReceipt.Create(
                        packagePath,
                        stagingPath,
                        manifest,
                        sourceProvenance);
                    if (!string.Equals(receipt.SourceSha256, preflight.SourceSha256, StringComparison.Ordinal))
                    {
                        return PackageInstallResult.Fail(
                            "Package bytes changed while the validated package was being installed.");
                    }

                    JsonUtil.SaveFile(Path.Combine(stagingPath, PackageInstallReceipt.FileName), receipt);

                    var targetPath = paths.GetPackagePath(manifest.Id, manifest.Version);
                    var rollbackPath = CommitStagedDirectory(stagingPath, targetPath, paths.Staging);
                    try
                    {
                        var existing = state.Find(manifest.Id);
                        state.Upsert(
                            manifest,
                            enabled: existing?.Enabled ?? ModActivationPolicy.IsEnabledByDefault(manifest),
                            restartRequired: restartRequired);
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

                    return PackageInstallResult.Success(manifest, targetPath);
                }
                catch (Exception ex)
                {
                    return PackageInstallResult.Fail(ex.Message);
                }
            }
        }

        private static PackagePreflightResult PreflightPackage(
            string packagePath,
            ManagerPaths paths,
            ManifestValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !IsRegularFile(packagePath))
            {
                return PackagePreflightResult.Fail(
                    null,
                    null,
                    "Package file does not exist or is not a regular file: " + packagePath);
            }

            if (!string.Equals(Path.GetExtension(packagePath), PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return PackagePreflightResult.Fail(
                    null,
                    null,
                    "Package file must use the " + PackageExtension + " extension.");
            }

            string? stagingPath = null;
            ModManifest? manifest = null;
            try
            {
                paths.EnsureCreated();
                stagingPath = Path.Combine(paths.Staging, "install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingPath);
                EnsurePackageSize(new FileInfo(packagePath).Length);
                var sourceSha256 = ExtractToSafeDirectory(packagePath, stagingPath);
                var manifestPath = Path.Combine(stagingPath, "topiaforge.mod.json");
                if (!File.Exists(manifestPath))
                {
                    return PackagePreflightResult.Fail(
                        stagingPath,
                        null,
                        "Package is missing topiaforge.mod.json.");
                }

                manifest = ModManifestJson.LoadFile(manifestPath);
                var errors = ManifestValidator.Validate(manifest, validationContext);
                if (errors.Count > 0)
                {
                    return PackagePreflightResult.Fail(stagingPath, manifest, errors);
                }

                var contentErrors = ManifestContentValidator.Validate(stagingPath, manifest);
                if (contentErrors.Count > 0)
                {
                    return PackagePreflightResult.Fail(stagingPath, manifest, contentErrors);
                }

                var entryAssemblyPath = Path.Combine(stagingPath, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
                {
                    return PackagePreflightResult.Fail(
                        stagingPath,
                        manifest,
                        "entryAssembly was not found in package: " + manifest.EntryAssembly);
                }

                var assemblyErrors = ManagedModAssemblyValidator.Validate(stagingPath, manifest);
                if (assemblyErrors.Count > 0)
                {
                    return PackagePreflightResult.Fail(stagingPath, manifest, assemblyErrors);
                }

                return PackagePreflightResult.Success(stagingPath, manifest, sourceSha256);
            }
            catch (Exception ex)
            {
                return PackagePreflightResult.Fail(stagingPath, manifest, ex.Message);
            }
        }

        /// <summary>
        /// Installs every .topiaforgemod file waiting in the package-inbox. Every candidate is fully
        /// preflighted without loading its code. When several versions share a mod id, the highest valid,
        /// compatible version is installed; invalid candidates are reported and left for inspection, while
        /// lower valid candidates are consumed as superseded after the selected package installs.
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

            FileAttributes inboxAttributes;
            try
            {
                inboxAttributes = File.GetAttributes(paths.PackageInbox);
            }
            catch (Exception ex)
            {
                return new[]
                {
                    new InboxInstallResult(
                        paths.PackageInbox,
                        PackageInstallResult.Fail("Package inbox could not be inspected safely: " + ex.Message),
                        superseded: false)
                };
            }

            if ((inboxAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return new[]
                {
                    new InboxInstallResult(
                        paths.PackageInbox,
                        PackageInstallResult.Fail("Package inbox must be a regular local directory."),
                        superseded: false)
                };
            }

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(paths.PackageInbox, "*", SearchOption.TopDirectoryOnly)
                    .Take(MaxInboxEntries + 1)
                    .ToList();
            }
            catch (Exception ex)
            {
                return new[]
                {
                    new InboxInstallResult(
                        paths.PackageInbox,
                        PackageInstallResult.Fail("Package inbox could not be enumerated safely: " + ex.Message),
                        superseded: false)
                };
            }

            if (entries.Count > MaxInboxEntries)
            {
                return new[]
                {
                    new InboxInstallResult(
                        paths.PackageInbox,
                        PackageInstallResult.Fail(
                            "Package inbox exceeds the " + MaxInboxEntries + " entry limit; no files were processed."),
                        superseded: false)
                };
            }

            var files = entries
                .Where(path => string.Equals(Path.GetExtension(path), PackageExtension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(NormalizeSelectionPath, StringComparer.Ordinal)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                return results;
            }

            if (files.Count > MaxInboxCandidates)
            {
                return new[]
                {
                    new InboxInstallResult(
                        paths.PackageInbox,
                        PackageInstallResult.Fail(
                            "Package inbox exceeds the " + MaxInboxCandidates + " package limit; no files were processed."),
                        superseded: false)
                };
            }

            var candidates = new List<InboxCandidate>(files.Count);
            foreach (var file in files)
            {
                if (!IsRegularFile(file))
                {
                    candidates.Add(new InboxCandidate(
                        filePath: file,
                        manifest: null,
                        preflightOk: false,
                        preflightErrors: new[] { "Package inbox candidate is a link, directory, or special file." },
                        sourceSha256: string.Empty));
                    continue;
                }

                using (var preflight = PreflightPackage(file, paths, validationContext))
                {
                    candidates.Add(new InboxCandidate(
                        file,
                        preflight.Manifest,
                        preflight.Ok,
                        preflight.Errors,
                        preflight.SourceSha256));
                }
            }

            foreach (var group in candidates
                         .GroupBy(candidate => candidate.GroupKey, StringComparer.Ordinal)
                         .OrderBy(candidateGroup => candidateGroup.Key, StringComparer.Ordinal))
            {
                var ordered = group
                    .OrderBy(candidate => candidate.NormalizedPath, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.FilePath, StringComparer.Ordinal)
                    .ToList();
                var selectable = ordered
                    .Where(candidate => candidate.IsValid)
                    .OrderByDescending(candidate => candidate.Version)
                    .ThenBy(candidate => candidate.NormalizedPath, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.FilePath, StringComparer.Ordinal)
                    .ToList();

                if (selectable.Count == 0)
                {
                    foreach (var rejected in ordered)
                    {
                        results.Add(new InboxInstallResult(
                            rejected.FilePath,
                            PackageInstallResult.Fail(rejected.Errors),
                            superseded: false));
                    }

                    continue;
                }

                var winner = selectable[0];
                BeforeInboxInstallForTesting?.Invoke(winner.FilePath);
                var install = InstallWithSource(
                    winner.FilePath,
                    paths,
                    state,
                    restartRequired,
                    validationContext,
                    PackageInstallReceipt.InboxSource,
                    winner.SourceSha256);
                var result = new InboxInstallResult(winner.FilePath, install, superseded: false);
                if (install.Ok)
                {
                    Consume(result, winner.SourceSha256);
                }

                results.Add(result);

                foreach (var candidate in ordered)
                {
                    if (ReferenceEquals(candidate, winner))
                    {
                        continue;
                    }

                    if (!candidate.IsValid)
                    {
                        results.Add(new InboxInstallResult(
                            candidate.FilePath,
                            PackageInstallResult.Fail(candidate.Errors),
                            superseded: false));
                        continue;
                    }

                    // Only consume valid superseded files once the selected package actually installed;
                    // otherwise leave the whole selectable set on disk for a retry.
                    var supersededResult = new InboxInstallResult(
                        candidate.FilePath,
                        null,
                        superseded: true);
                    if (install.Ok)
                    {
                        Consume(supersededResult, candidate.SourceSha256);
                    }
                    results.Add(supersededResult);
                }
            }

            return results;
        }

        private static string NormalizeSelectionPath(string path)
        {
            return Path.GetFullPath(path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Normalize(NormalizationForm.FormC)
                .ToLowerInvariant();
        }

        private static void Consume(InboxInstallResult result, string expectedSourceSha256)
        {
            try
            {
                if (!IsRegularFile(result.FilePath))
                {
                    result.ConsumeError =
                        "Package inbox candidate disappeared or is no longer a regular file; it was not consumed.";
                    return;
                }

                var actualSourceSha256 = ComputeSha256(result.FilePath);
                if (!string.Equals(actualSourceSha256, expectedSourceSha256, StringComparison.Ordinal))
                {
                    result.ConsumeError =
                        "Package inbox candidate bytes changed after preflight; the replacement was retained.";
                    return;
                }

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
                        result.ConsumeError =
                            "The package was installed, but the retained .installed path already exists.";
                        return;
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

        private static string ExtractToSafeDirectory(string zipPath, string destination)
        {
            using (var file = new FileStream(
                       zipPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            {
                EnsurePackageSize(file.Length);
                string sourceSha256;
                using (var sha256 = SHA256.Create())
                {
                    sourceSha256 = ToLowerHex(sha256.ComputeHash(file));
                }

                file.Position = 0;
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

                return sourceSha256;
            }
        }

        private static bool IsRegularFile(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var input = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            using (var sha256 = SHA256.Create())
            {
                return ToLowerHex(sha256.ComputeHash(input));
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
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

        private sealed class PackagePreflightResult : IDisposable
        {
            private PackagePreflightResult(
                string? stagingPath,
                ModManifest? manifest,
                IReadOnlyList<string> errors,
                string sourceSha256)
            {
                StagingPath = stagingPath;
                Manifest = manifest;
                Errors = errors;
                SourceSha256 = sourceSha256;
            }

            public string? StagingPath { get; }

            public ModManifest? Manifest { get; }

            public IReadOnlyList<string> Errors { get; }

            public string SourceSha256 { get; }

            public bool Ok => StagingPath != null && Manifest != null && Errors.Count == 0;

            public static PackagePreflightResult Success(
                string stagingPath,
                ModManifest manifest,
                string sourceSha256)
            {
                return new PackagePreflightResult(
                    stagingPath,
                    manifest,
                    Array.Empty<string>(),
                    sourceSha256);
            }

            public static PackagePreflightResult Fail(
                string? stagingPath,
                ModManifest? manifest,
                string error)
            {
                return new PackagePreflightResult(stagingPath, manifest, new[] { error }, string.Empty);
            }

            public static PackagePreflightResult Fail(
                string? stagingPath,
                ModManifest? manifest,
                IReadOnlyList<string> errors)
            {
                return new PackagePreflightResult(stagingPath, manifest, errors.ToArray(), string.Empty);
            }

            public void Dispose()
            {
                if (StagingPath != null)
                {
                    TryDelete(StagingPath);
                }
            }
        }

        private sealed class InboxCandidate
        {
            public InboxCandidate(
                string filePath,
                ModManifest? manifest,
                bool preflightOk,
                IReadOnlyList<string> preflightErrors,
                string sourceSha256)
            {
                FilePath = filePath;
                NormalizedPath = NormalizeSelectionPath(filePath);
                var hasVersion = VersionUtil.TryParseSemantic(manifest?.Version, out var version);
                Version = version;
                IsValid = preflightOk && hasVersion;
                SourceSha256 = IsValid ? sourceSha256 : string.Empty;
                if (IsValid)
                {
                    Errors = Array.Empty<string>();
                }
                else if (preflightErrors.Count > 0)
                {
                    Errors = preflightErrors.ToArray();
                }
                else
                {
                    Errors = new[]
                    {
                        "Package candidate failed preflight because its version could not be ordered as SemVer."
                    };
                }

                GroupKey = manifest != null && !string.IsNullOrWhiteSpace(manifest.Id)
                    ? "id:" + manifest.Id.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()
                    : "path:" + NormalizedPath;
            }

            public string FilePath { get; }

            public string NormalizedPath { get; }

            public string GroupKey { get; }

            public VersionUtil.ParsedSemanticVersion Version { get; }

            public bool IsValid { get; }

            public string SourceSha256 { get; }

            public IReadOnlyList<string> Errors { get; }
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

        /// <summary>
        /// The install or rejected-preflight outcome. Null only when a valid candidate was skipped as
        /// superseded by the selected version in the same inbox.
        /// </summary>
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
