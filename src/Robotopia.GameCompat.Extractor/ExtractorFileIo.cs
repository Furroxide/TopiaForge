using System;
using System.IO;
using System.Text;

namespace Robotopia.GameCompat.Extractor
{
    /// <summary>
    /// Bounded, strict, regular-file reads for extractor inputs. Files are read twice so a same-size
    /// replacement cannot silently mix metadata from one file with content from another.
    /// </summary>
    internal static class ExtractorFileIo
    {
        private const int BufferBytes = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Action Noop = delegate { };

        internal static string ReadStableUtf8(string path, int maximumBytes, string label)
        {
            var bytes = ReadStableBytes(path, maximumBytes, label, Noop);
            try
            {
                var text = StrictUtf8.GetString(bytes);
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(label + " is not strict UTF-8: " + path, exception);
            }
        }

        internal static byte[] ReadStableBytes(string path, int maximumBytes, string label)
        {
            return ReadStableBytes(path, maximumBytes, label, Noop);
        }

        internal static byte[] ReadStableBytes(
            string path,
            int maximumBytes,
            string label,
            Action afterInitialRead)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file path is required.", nameof(path));
            }

            if (maximumBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            if (afterInitialRead == null)
            {
                throw new ArgumentNullException(nameof(afterInitialRead));
            }

            var fullPath = Path.GetFullPath(path);
            var before = InspectRegularFile(fullPath, label);
            if (before.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    label + " exceeds the " + maximumBytes + " byte safety limit: " + fullPath);
            }

            var bytes = new byte[checked((int)before.Length)];
            ReadExactFile(fullPath, bytes, before.Length, label);

            afterInitialRead();

            var after = InspectRegularFile(fullPath, label);
            if (!before.Matches(after))
            {
                throw new IOException(label + " was replaced while it was being read: " + fullPath);
            }

            VerifyExactContent(fullPath, bytes, label);
            return bytes;
        }

        private static FileSnapshot InspectRegularFile(string path, string label)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    label + " must be a regular file; links and special files are rejected: " + path);
            }

            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
            {
                throw new FileNotFoundException(label + " does not exist.", path);
            }

            return new FileSnapshot(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }

        private static void ReadExactFile(string path, byte[] bytes, long expectedLength, string label)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != expectedLength)
                {
                    throw new IOException(label + " changed before it could be read: " + path);
                }

                ReadExactly(input, bytes, label);
                if (input.ReadByte() != -1 || input.Length != expectedLength)
                {
                    throw new IOException(label + " changed while it was being read: " + path);
                }
            }
        }

        private static void VerifyExactContent(string path, byte[] expected, string label)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != expected.Length)
                {
                    throw new IOException(label + " was replaced before verification: " + path);
                }

                var buffer = new byte[Math.Min(BufferBytes, Math.Max(1, expected.Length))];
                var offset = 0;
                while (offset < expected.Length)
                {
                    var wanted = Math.Min(buffer.Length, expected.Length - offset);
                    var read = input.Read(buffer, 0, wanted);
                    if (read != wanted)
                    {
                        throw new IOException(label + " changed during verification: " + path);
                    }

                    for (var index = 0; index < read; index++)
                    {
                        if (buffer[index] != expected[offset + index])
                        {
                            throw new IOException(label + " content was replaced during the read: " + path);
                        }
                    }

                    offset += read;
                }

                if (input.ReadByte() != -1)
                {
                    throw new IOException(label + " grew during verification: " + path);
                }
            }
        }

        private static void ReadExactly(Stream input, byte[] bytes, string label)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = input.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(label + " was truncated while it was being read.");
                }

                offset += read;
            }
        }

        private readonly struct FileSnapshot
        {
            internal FileSnapshot(long length, DateTime lastWriteUtc, DateTime creationUtc)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                CreationUtc = creationUtc;
            }

            internal long Length { get; }
            private DateTime LastWriteUtc { get; }
            private DateTime CreationUtc { get; }

            internal bool Matches(FileSnapshot other)
            {
                return Length == other.Length
                    && LastWriteUtc == other.LastWriteUtc
                    && CreationUtc == other.CreationUtc;
            }
        }
    }
}
