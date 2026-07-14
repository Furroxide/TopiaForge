using System;
using System.IO;
using System.Text;

namespace Robotopia.VpmResolver
{
    /// <summary>Bounded, strict, race-stable regular-file reader for the read-only recovery bridge.</summary>
    internal static class VpmSafeFileReader
    {
        private const int BufferBytes = 16 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Action Noop = delegate { };

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
                    label + " is larger than the " + maximumBytes + "-byte inspection limit.");
            }

            var bytes = new byte[(int)before.Length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != before.Length)
                {
                    throw new IOException(label + " changed before it could be inspected.");
                }

                ReadExactly(stream, bytes, label);
                if (stream.ReadByte() != -1 || stream.Length != before.Length)
                {
                    throw new IOException(label + " changed while it was being inspected.");
                }
            }

            afterInitialRead();

            var after = InspectRegularFile(path, label);
            if (!before.Matches(after))
            {
                throw new IOException(label + " was replaced while it was being inspected.");
            }

            VerifyUnchanged(path, bytes, label);
            try
            {
                var json = StrictUtf8.GetString(bytes);
                return json.Length > 0 && json[0] == '\uFEFF' ? json.Substring(1) : json;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(label + " is not strict UTF-8.", exception);
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
                            throw new IOException(label + " content was replaced during inspection.");
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
