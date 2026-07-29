using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.WorldCompanion.Editor
{
    /// <summary>Unity-free, bounded file I/O used by the editor world builder.</summary>
    internal static class WorldCompanionFileIo
    {
        internal const int CurrentWorldConfigSchemaVersion = 2;
        private const int CopyBufferBytes = 64 * 1024;
        private const int MaxProvenanceBytes = 4 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Action Noop = delegate { };

        internal static void RequireCurrentWorldConfigSchema(int schemaVersion)
        {
            if (schemaVersion != CurrentWorldConfigSchemaVersion)
            {
                throw new InvalidDataException(
                    "topiaforge.world.json must use schemaVersion "
                    + CurrentWorldConfigSchemaVersion + ".");
            }
        }

        public static string ReadStableUtf8(string path, long maximumBytes, string label)
        {
            return ReadStableUtf8(path, maximumBytes, label, Noop);
        }

        internal static string ReadStableUtf8(
            string path,
            long maximumBytes,
            string label,
            Action afterInitialRead)
        {
            if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            var before = InspectRegularFile(path, label);
            if (before.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    label + " exceeds the " + maximumBytes + " byte safety limit.");
            }

            var bytes = new byte[(int)before.Length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != before.Length)
                {
                    throw new IOException(label + " changed before it could be read.");
                }

                ReadExactly(stream, bytes, label);
                if (stream.ReadByte() != -1 || stream.Length != before.Length)
                {
                    throw new IOException(label + " changed while it was being read.");
                }
            }

            afterInitialRead();

            var after = InspectRegularFile(path, label);
            if (!before.Matches(after))
            {
                throw new IOException(label + " was replaced while it was being read.");
            }

            VerifyUnchanged(path, bytes, label);
            try
            {
                var text = StrictUtf8.GetString(bytes);
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(label + " is not strict UTF-8.", exception);
            }
        }

        public static string PublishPairAtomic(
            string sourceBundle,
            string bundleTarget,
            string manifestTarget,
            Func<string, string> buildManifest)
        {
            return PublishPairAtomic(sourceBundle, bundleTarget, manifestTarget, buildManifest, Noop);
        }

        internal static string PublishPairAtomic(
            string sourceBundle,
            string bundleTarget,
            string manifestTarget,
            Func<string, string> buildManifest,
            Action beforeManifestCommit)
        {
            if (buildManifest == null)
            {
                throw new ArgumentNullException(nameof(buildManifest));
            }

            EnsureSameExistingDirectory(bundleTarget, manifestTarget);
            EnsureReplaceable(bundleTarget, "world bundle destination");
            EnsureReplaceable(manifestTarget, "world provenance destination");

            var bundleTemp = UniqueSibling(bundleTarget, ".tmp-");
            var manifestTemp = UniqueSibling(manifestTarget, ".tmp-");
            var bundleState = new ReplacementState(bundleTarget);
            var manifestState = new ReplacementState(manifestTarget);
            try
            {
                CopyToNewFile(sourceBundle, bundleTemp);
                var sha256 = ComputeSha256(bundleTemp);
                WriteTextToNewFile(manifestTemp, buildManifest(sha256), MaxProvenanceBytes);

                Commit(bundleTemp, bundleState);
                beforeManifestCommit();

                Commit(manifestTemp, manifestState);
                DeleteIfPresent(bundleState.BackupPath);
                DeleteIfPresent(manifestState.BackupPath);
                return sha256;
            }
            catch (Exception publishError)
            {
                try
                {
                    Rollback(manifestState);
                    Rollback(bundleState);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException(
                        "World bundle publication failed and rollback was incomplete.",
                        new AggregateException(publishError, rollbackError));
                }

                throw;
            }
            finally
            {
                DeleteIfPresent(bundleTemp);
                DeleteIfPresent(manifestTemp);
                if (!bundleState.Committed)
                {
                    DeleteIfPresent(bundleState.BackupPath);
                }

                if (!manifestState.Committed)
                {
                    DeleteIfPresent(manifestState.BackupPath);
                }
            }
        }

        private static FileSnapshot InspectRegularFile(string path, string label)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(label + " must be a regular file; links and special files are rejected.");
            }

            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
            {
                throw new FileNotFoundException(label + " does not exist.", path);
            }

            return new FileSnapshot(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }

        private static void VerifyUnchanged(string path, byte[] expected, string label)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != expected.Length)
                {
                    throw new IOException(label + " was replaced before verification.");
                }

                var buffer = new byte[Math.Min(CopyBufferBytes, Math.Max(1, expected.Length))];
                var offset = 0;
                while (offset < expected.Length)
                {
                    var wanted = Math.Min(buffer.Length, expected.Length - offset);
                    var read = stream.Read(buffer, 0, wanted);
                    if (read != wanted)
                    {
                        throw new IOException(label + " changed during verification.");
                    }

                    for (var index = 0; index < read; index++)
                    {
                        if (buffer[index] != expected[offset + index])
                        {
                            throw new IOException(label + " content was replaced during the read.");
                        }
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new IOException(label + " grew during verification.");
                }
            }
        }

        private static void CopyToNewFile(string source, string destination)
        {
            InspectRegularFile(source, "built world bundle");
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferBytes, FileOptions.WriteThrough))
            {
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }

                output.Flush(true);
            }
        }

        private static void WriteTextToNewFile(string path, string text, int maximumBytes)
        {
            var bytes = StrictUtf8.GetBytes(text ?? string.Empty);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidDataException("World provenance exceeds " + maximumBytes + " bytes.");
            }

            using (var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                Math.Min(CopyBufferBytes, Math.Max(1, bytes.Length)), FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void Commit(string tempPath, ReplacementState state)
        {
            EnsureReplaceable(state.TargetPath, "publication destination");
            state.HadOriginal = File.Exists(state.TargetPath);
            if (state.HadOriginal)
            {
                try
                {
                    File.Replace(tempPath, state.TargetPath, state.BackupPath);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(state.TargetPath, state.BackupPath);
                    try
                    {
                        File.Move(tempPath, state.TargetPath);
                    }
                    catch
                    {
                        File.Move(state.BackupPath, state.TargetPath);
                        throw;
                    }
                }
            }
            else
            {
                File.Move(tempPath, state.TargetPath);
            }

            state.Committed = true;
        }

        private static void Rollback(ReplacementState state)
        {
            if (!state.Committed)
            {
                return;
            }

            if (!state.HadOriginal)
            {
                File.Delete(state.TargetPath);
            }
            else if (File.Exists(state.BackupPath))
            {
                if (File.Exists(state.TargetPath))
                {
                    try
                    {
                        File.Replace(state.BackupPath, state.TargetPath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(state.TargetPath);
                        File.Move(state.BackupPath, state.TargetPath);
                    }
                }
                else
                {
                    File.Move(state.BackupPath, state.TargetPath);
                }
            }
            else
            {
                throw new IOException("Rollback backup is missing for " + state.TargetPath + ".");
            }

            state.Committed = false;
        }

        private static void EnsureSameExistingDirectory(string first, string second)
        {
            var firstDirectory = Path.GetDirectoryName(Path.GetFullPath(first));
            var secondDirectory = Path.GetDirectoryName(Path.GetFullPath(second));
            if (string.IsNullOrEmpty(firstDirectory) || !Directory.Exists(firstDirectory)
                || !string.Equals(firstDirectory, secondDirectory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("World bundle and provenance must share one existing output directory.");
            }
        }

        private static void EnsureReplaceable(string path, string label)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(label + " must be a regular file; links and special files are rejected.");
                }
            }
            catch (FileNotFoundException)
            {
                // Missing destinations are created by an atomic same-directory rename.
            }
            catch (DirectoryNotFoundException)
            {
                // The caller validates the output directory separately.
            }
        }

        private static void ReadExactly(Stream stream, byte[] bytes, string label)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(label + " was truncated while reading.");
                }

                offset += read;
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string UniqueSibling(string target, string marker)
        {
            return Path.GetFullPath(target) + marker + Guid.NewGuid().ToString("N");
        }

        private static void DeleteIfPresent(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Unique orphan files cannot be interpreted as live output. Preserve the primary result/error.
            }
        }

        private sealed class ReplacementState
        {
            public ReplacementState(string targetPath)
            {
                TargetPath = Path.GetFullPath(targetPath);
                BackupPath = UniqueSibling(TargetPath, ".bak-");
            }

            public string TargetPath { get; private set; }
            public string BackupPath { get; private set; }
            public bool HadOriginal { get; set; }
            public bool Committed { get; set; }
        }

        private struct FileSnapshot
        {
            public FileSnapshot(long length, DateTime lastWriteUtc, DateTime creationUtc)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                CreationUtc = creationUtc;
            }

            public long Length { get; private set; }
            private DateTime LastWriteUtc { get; set; }
            private DateTime CreationUtc { get; set; }

            public bool Matches(FileSnapshot other)
            {
                return Length == other.Length && LastWriteUtc == other.LastWriteUtc
                    && CreationUtc == other.CreationUtc;
            }
        }
    }
}
