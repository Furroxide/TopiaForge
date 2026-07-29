using System;
using System.IO;
using System.Text;

namespace TopiaForge.UgcCompanion.Editor
{
    /// <summary>Unity-free bounded reader and atomic writer for the one-shot companion seed.</summary>
    internal static class UgcCompanionSeedFileIo
    {
        internal const int CurrentSeedSchemaVersion = 2;
        private const int BufferBytes = 16 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Action Noop = delegate { };

        internal static void RequireCurrentSeedSchema(int schemaVersion)
        {
            if (schemaVersion != CurrentSeedSchemaVersion)
            {
                throw new InvalidDataException(
                    "TopiaForgeUgcCompanion.json must use schemaVersion "
                    + CurrentSeedSchemaVersion + ".");
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
                throw new InvalidDataException(label + " exceeds the " + maximumBytes + " byte safety limit.");
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

        public static void RewriteAtomicUtf8(
            string path,
            string expectedText,
            string replacementText,
            int maximumBytes,
            string label)
        {
            RewriteAtomicUtf8(path, expectedText, replacementText, maximumBytes, label, Noop, Noop);
        }

        internal static void RewriteAtomicUtf8(
            string path,
            string expectedText,
            string replacementText,
            int maximumBytes,
            string label,
            Action beforeValidation,
            Action afterCommit)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(replacementText ?? string.Empty);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException(label + " contains invalid Unicode.", exception);
            }

            if (bytes.Length > maximumBytes)
            {
                throw new InvalidDataException(label + " exceeds the " + maximumBytes + " byte safety limit.");
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(label + " directory does not exist: " + directory);
            }

            EnsureReplaceable(fullPath, label);
            var temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = fullPath + ".bak-" + Guid.NewGuid().ToString("N");
            var hadOriginal = File.Exists(fullPath);
            var committed = false;
            try
            {
                using (var stream = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    Math.Min(BufferBytes, Math.Max(1, bytes.Length)), FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                beforeValidation();
                var currentText = ReadStableUtf8(fullPath, maximumBytes, label);
                if (!string.Equals(currentText, expectedText, StringComparison.Ordinal))
                {
                    throw new IOException(label + " changed after it was read; refusing to overwrite a newer seed.");
                }

                Commit(temp, fullPath, backup, hadOriginal);
                committed = true;
                afterCommit();

                DeleteIfPresent(backup);
            }
            catch
            {
                if (committed)
                {
                    Rollback(fullPath, backup, hadOriginal);
                    committed = false;
                }

                throw;
            }
            finally
            {
                DeleteIfPresent(temp);
                if (!committed)
                {
                    DeleteIfPresent(backup);
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

                var buffer = new byte[Math.Min(BufferBytes, Math.Max(1, expected.Length))];
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

        private static void Commit(string temp, string target, string backup, bool hadOriginal)
        {
            EnsureReplaceable(target, "UGC companion seed destination");
            if (!hadOriginal)
            {
                File.Move(temp, target);
                return;
            }

            try
            {
                File.Replace(temp, target, backup);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(target, backup);
                try
                {
                    File.Move(temp, target);
                }
                catch
                {
                    File.Move(backup, target);
                    throw;
                }
            }
        }

        private static void Rollback(string target, string backup, bool hadOriginal)
        {
            if (!hadOriginal)
            {
                File.Delete(target);
                return;
            }

            if (!File.Exists(backup))
            {
                throw new IOException("UGC companion seed rollback backup is missing.");
            }

            if (!File.Exists(target))
            {
                File.Move(backup, target);
                return;
            }

            try
            {
                File.Replace(backup, target, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(target);
                File.Move(backup, target);
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
                // Missing destinations are created with a same-directory rename.
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

        private static void DeleteIfPresent(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Unique orphan files cannot be interpreted as the live seed. Preserve the primary error.
            }
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
