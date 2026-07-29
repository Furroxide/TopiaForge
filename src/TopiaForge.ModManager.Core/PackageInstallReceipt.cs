using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Durable provenance and integrity metadata written by the installer. Receipts detect accidental or
    /// post-install byte changes; they are not signatures and therefore do not turn in-process mods into a
    /// security sandbox.
    /// </summary>
    [DataContract]
    public sealed class PackageInstallReceipt
    {
        public const int CurrentSchemaVersion = 2;
        public const int MinimumSupportedSchemaVersion = 1;
        public const string FileName = "topiaforge.install.json";
        public const string CurrentValidatorVersion = "1";
        public const string LocalUnverifiedTrust = "local-unverified";
        public const string Sha256VerifiedTrust = "sha256-verified";
        public const string LocalSource = "local";
        public const string InboxSource = "inbox";
        public const string CacheSource = "cache";
        private const int MaxFiles = 8192;
        private const int MaxSourceLength = 160;
        private const int MaxSourceFileLength = 255;
        private const int MaxSourceIdentifierLength = 128;
        private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [DataMember(Name = "modId")]
        public string ModId { get; set; } = string.Empty;

        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "sourceFile")]
        public string SourceFile { get; set; } = string.Empty;

        [DataMember(Name = "source")]
        public string Source { get; set; } = string.Empty;

        [DataMember(Name = "sourceSha256")]
        public string SourceSha256 { get; set; } = string.Empty;

        [DataMember(Name = "installedAtUtc")]
        public string InstalledAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "validatorVersion")]
        public string ValidatorVersion { get; set; } = CurrentValidatorVersion;

        [DataMember(Name = "trust")]
        public string Trust { get; set; } = LocalUnverifiedTrust;

        [DataMember(Name = "files")]
        public List<PackageFileReceipt> Files { get; set; } = new List<PackageFileReceipt>();

        public static PackageInstallReceipt Create(
            string archivePath,
            string packageRoot,
            ModManifest manifest,
            string sourceProvenance = LocalSource,
            string trust = LocalUnverifiedTrust)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (!IsValidTrust(trust))
            {
                throw new ArgumentException("The package trust result is not recognized.", nameof(trust));
            }

            var receipt = new PackageInstallReceipt
            {
                ModId = manifest.Id,
                Version = manifest.Version,
                SourceFile = SanitizeSourceFile(Path.GetFileName(archivePath), manifest),
                Source = SanitizeSourceProvenance(sourceProvenance),
                SourceSha256 = ComputeSha256(archivePath),
                InstalledAtUtc = DateTime.UtcNow.ToString("O"),
                Trust = trust
            };

            foreach (var file in EnumeratePayloadFiles(packageRoot))
            {
                receipt.Files.Add(new PackageFileReceipt
                {
                    Path = file.RelativePath,
                    Length = new FileInfo(file.FullPath).Length,
                    Sha256 = ComputeSha256(file.FullPath),
                    Critical = IsCritical(file.RelativePath, manifest)
                });
            }

            return receipt;
        }

        public static IReadOnlyList<string> Verify(string packageRoot, ModManifest manifest)
        {
            var errors = new List<string>();
            var receiptPath = Path.Combine(packageRoot, FileName);
            if (!File.Exists(receiptPath))
            {
                errors.Add("Package install receipt is missing; reinstall or repair this mod.");
                return errors;
            }

            PackageInstallReceipt receipt;
            try
            {
                receipt = JsonUtil.LoadFile(receiptPath, new PackageInstallReceipt());
            }
            catch (Exception ex)
            {
                errors.Add("Package install receipt is unreadable: " + ex.Message);
                return errors;
            }

            if (receipt.SchemaVersion < MinimumSupportedSchemaVersion ||
                receipt.SchemaVersion > CurrentSchemaVersion)
            {
                errors.Add("Package install receipt schemaVersion is unsupported.");
            }

            if (!string.Equals(receipt.ValidatorVersion, CurrentValidatorVersion, StringComparison.Ordinal))
            {
                errors.Add("Package install receipt validatorVersion is unsupported; reinstall or repair this mod.");
            }

            if (!IsLowerHexSha256(receipt.SourceSha256))
            {
                errors.Add("Package install receipt source SHA-256 is invalid.");
            }

            if (!IsValidSourceFile(receipt.SourceFile))
            {
                errors.Add("Package install receipt source file is invalid.");
            }

            if (receipt.SchemaVersion >= 2 && !IsValidSourceProvenance(receipt.Source))
            {
                errors.Add("Package install receipt source provenance is invalid.");
            }

            if (!IsValidTrust(receipt.Trust))
            {
                errors.Add("Package install receipt trust result is invalid.");
            }

            if (string.IsNullOrWhiteSpace(receipt.InstalledAtUtc) ||
                !DateTimeOffset.TryParse(
                    receipt.InstalledAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                errors.Add("Package install receipt installedAtUtc is invalid.");
            }

            if (!string.Equals(receipt.ModId, manifest.Id, StringComparison.Ordinal) ||
                !string.Equals(receipt.Version, manifest.Version, StringComparison.Ordinal))
            {
                errors.Add("Package install receipt identity does not match the manifest.");
            }

            var expected = new Dictionary<string, PackageFileReceipt>(StringComparer.Ordinal);
            if (receipt.Files == null || receipt.Files.Count > MaxFiles)
            {
                errors.Add("Package install receipt file inventory exceeds the supported limit.");
                return errors;
            }

            string? previousPath = null;
            foreach (var item in receipt.Files)
            {
                if (item == null || !TryValidateReceiptPath(item.Path, out var normalized))
                {
                    errors.Add("Package install receipt contains an invalid file path.");
                    continue;
                }

                if (previousPath != null && string.CompareOrdinal(previousPath, normalized) > 0)
                {
                    errors.Add("Package install receipt file inventory is not sorted.");
                }

                previousPath = normalized;

                if (!expected.TryAdd(normalized, item))
                {
                    errors.Add("Package install receipt contains a duplicate file path: " + normalized + ".");
                }

                if (item.Length < 0 || item.Length > MaxTotalBytes)
                {
                    errors.Add("Package install receipt contains an invalid file length: " + normalized + ".");
                }

                var shouldBeCritical = IsCritical(normalized, manifest);
                if (item.Critical != shouldBeCritical)
                {
                    errors.Add("Package install receipt critical-file classification changed: " + normalized + ".");
                }
            }

            IReadOnlyList<PayloadFile> actual;
            try
            {
                actual = EnumeratePayloadFiles(packageRoot);
            }
            catch (Exception ex)
            {
                errors.Add("Installed package files could not be enumerated safely: " + ex.Message);
                return errors;
            }

            foreach (var file in actual)
            {
                if (!expected.Remove(file.RelativePath, out var item))
                {
                    errors.Add("Installed package contains an unreceipted file: " + file.RelativePath + ".");
                    continue;
                }

                var info = new FileInfo(file.FullPath);
                if (info.Length != item.Length)
                {
                    errors.Add("Installed package file size changed: " + file.RelativePath + ".");
                    continue;
                }

                var digest = ComputeSha256(file.FullPath);
                if (!IsLowerHexSha256(item.Sha256) ||
                    !string.Equals(digest, item.Sha256, StringComparison.Ordinal))
                {
                    errors.Add("Installed package file digest changed: " + file.RelativePath + ".");
                }
            }

            foreach (var missing in expected.Keys.OrderBy(path => path, StringComparer.Ordinal))
            {
                errors.Add("Installed package file is missing: " + missing + ".");
            }

            return errors;
        }

        private static IReadOnlyList<PayloadFile> EnumeratePayloadFiles(string packageRoot)
        {
            var root = Path.GetFullPath(packageRoot);
            var files = new List<PayloadFile>();
            var directories = new Stack<string>();
            directories.Push(root);
            long totalBytes = 0;
            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException("Installed package contains a linked or special directory.");
                }

                foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        throw new InvalidDataException("Installed package contains a link or special file.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(path);
                        continue;
                    }

                    var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relative, FileName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!TryValidateReceiptPath(relative, out var normalized))
                    {
                        throw new InvalidDataException("Installed package contains an unsafe path: " + relative + ".");
                    }

                    var length = new FileInfo(path).Length;
                    if (files.Count >= MaxFiles || length < 0 || totalBytes > MaxTotalBytes - length)
                    {
                        throw new InvalidDataException("Installed package exceeds the receipt inventory limits.");
                    }

                    totalBytes += length;
                    files.Add(new PayloadFile(path, normalized));
                }
            }

            return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
        }

        private static bool TryValidateReceiptPath(string? path, out string normalized)
        {
            normalized = string.Empty;
            return !string.IsNullOrWhiteSpace(path) &&
                PortablePackagePath.TryValidate(path!, out normalized, out _, out _);
        }

        private static bool IsCritical(string path, ModManifest manifest)
        {
            return string.Equals(path, "topiaforge.mod.json", StringComparison.Ordinal) ||
                string.Equals(path, manifest.EntryAssembly.Replace('\\', '/'), StringComparison.Ordinal) ||
                (manifest.ApiAssemblies ?? new List<string>()).Any(api =>
                    string.Equals(path, api.Replace('\\', '/'), StringComparison.Ordinal)) ||
                (manifest.Multiplayer?.SynchronizedFiles ?? new List<string>()).Any(synchronized =>
                    string.Equals(path, synchronized, StringComparison.Ordinal));
        }

        private static string SanitizeSourceFile(string? sourceFile, ModManifest manifest)
        {
            var fallback = manifest.Id + "-" + manifest.Version + ".topiaforgemod";
            var candidate = string.IsNullOrWhiteSpace(sourceFile) ? fallback : sourceFile!.Trim();
            var characters = candidate.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
            {
                if (characters[index] == '/' || characters[index] == '\\' || char.IsControl(characters[index]))
                {
                    characters[index] = '_';
                }
            }

            candidate = new string(characters).Trim();
            if (candidate.Length > MaxSourceFileLength)
            {
                var length = MaxSourceFileLength;
                if (char.IsHighSurrogate(candidate[length - 1]) &&
                    char.IsLowSurrogate(candidate[length]))
                {
                    length--;
                }

                candidate = candidate.Substring(0, length);
            }

            return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        }

        private static string SanitizeSourceProvenance(string? source)
        {
            var candidate = (source ?? string.Empty).Trim();
            if (string.Equals(candidate, InboxSource, StringComparison.OrdinalIgnoreCase))
            {
                return InboxSource;
            }

            if (string.Equals(candidate, CacheSource, StringComparison.OrdinalIgnoreCase))
            {
                return CacheSource;
            }

            if (string.Equals(candidate, LocalSource, StringComparison.OrdinalIgnoreCase))
            {
                return LocalSource;
            }

            const string registryPrefix = "registry:";
            if (candidate.StartsWith(registryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identifier = candidate.Substring(registryPrefix.Length).Trim().ToLowerInvariant();
                return IsSafeSourceIdentifier(identifier) ? registryPrefix + identifier : "registry";
            }

            const string remotePrefix = "remote:";
            if (candidate.StartsWith(remotePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var host = candidate.Substring(remotePrefix.Length).Trim().ToLowerInvariant();
                return IsSafeRemoteHost(host) ? remotePrefix + host : "remote";
            }

            if (string.Equals(candidate, "registry", StringComparison.OrdinalIgnoreCase))
            {
                return "registry";
            }

            if (string.Equals(candidate, "remote", StringComparison.OrdinalIgnoreCase))
            {
                return "remote";
            }

            return LocalSource;
        }

        private static bool IsValidSourceFile(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > MaxSourceFileLength ||
                !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var character in value)
            {
                if (character == '/' || character == '\\' || char.IsControl(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidSourceProvenance(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > MaxSourceLength)
            {
                return false;
            }

            if (string.Equals(value, LocalSource, StringComparison.Ordinal) ||
                string.Equals(value, InboxSource, StringComparison.Ordinal) ||
                string.Equals(value, CacheSource, StringComparison.Ordinal) ||
                string.Equals(value, "registry", StringComparison.Ordinal) ||
                string.Equals(value, "remote", StringComparison.Ordinal))
            {
                return true;
            }

            const string registryPrefix = "registry:";
            if (value.StartsWith(registryPrefix, StringComparison.Ordinal))
            {
                return IsSafeSourceIdentifier(value.Substring(registryPrefix.Length));
            }

            const string remotePrefix = "remote:";
            return value.StartsWith(remotePrefix, StringComparison.Ordinal) &&
                IsSafeRemoteHost(value.Substring(remotePrefix.Length));
        }

        private static bool IsSafeSourceIdentifier(string value)
        {
            if (value.Length < 1 || value.Length > MaxSourceIdentifierLength ||
                !IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[value.Length - 1]) ||
                value.Contains(".."))
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
                {
                    return false;
                }
            }

            return string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
        }

        private static bool IsSafeRemoteHost(string value)
        {
            if (value.Length < 1 || value.Length > MaxSourceIdentifierLength ||
                !IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[value.Length - 1]) ||
                value.Contains(".."))
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '-')
                {
                    return false;
                }
            }

            return string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
        }

        private static bool IsValidTrust(string? value)
        {
            return string.Equals(value, LocalUnverifiedTrust, StringComparison.Ordinal) ||
                string.Equals(value, Sha256VerifiedTrust, StringComparison.Ordinal);
        }

        private static string ComputeSha256(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool IsLowerHexSha256(string? value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (var character in value)
            {
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class PayloadFile
        {
            public PayloadFile(string fullPath, string relativePath)
            {
                FullPath = fullPath;
                RelativePath = relativePath;
            }

            public string FullPath { get; }
            public string RelativePath { get; }
        }
    }

    [DataContract]
    public sealed class PackageFileReceipt
    {
        [DataMember(Name = "path")]
        public string Path { get; set; } = string.Empty;

        [DataMember(Name = "length")]
        public long Length { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [DataMember(Name = "critical")]
        public bool Critical { get; set; }
    }
}
