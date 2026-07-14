using System;
using System.IO;

namespace Robotopia
{
    /// <summary>Bounded, regular-file assembly reads for the exact-editor lifecycle smoke.</summary>
    internal static class UiSmokeAssemblyFileIo
    {
        private const int BufferBytes = 64 * 1024;
        private static readonly Action<string> Noop = delegate { };

        internal static byte[] ReadStableBytes(string path, int maximumBytes, string label)
        {
            return ReadStableBytes(path, maximumBytes, label, Noop);
        }

        internal static byte[] ReadStableBytes(
            string path,
            int maximumBytes,
            string label,
            Action<string> inspectPath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An assembly path is required.", nameof(path));
            }

            if (maximumBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            if (inspectPath == null)
            {
                throw new ArgumentNullException(nameof(inspectPath));
            }

            var fullPath = Path.GetFullPath(path);
            var before = InspectRegularFile(fullPath, label);
            if (before.Length == 0 || before.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    label + " must contain between 1 and " + maximumBytes + " bytes: " + fullPath);
            }

            var bytes = new byte[checked((int)before.Length)];
            using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != before.Length)
                {
                    throw new IOException(label + " changed before it could be read: " + fullPath);
                }

                ReadExactly(input, bytes, label);
                if (input.ReadByte() != -1 || input.Length != before.Length)
                {
                    throw new IOException(label + " changed while it was being read: " + fullPath);
                }
            }

            // AssemblyName.GetAssemblyName must inspect a path. Keep that inspection inside the stable-read
            // window, then compare both metadata and every byte before callers use the captured byte array.
            inspectPath(fullPath);

            var after = InspectRegularFile(fullPath, label);
            if (!before.Matches(after))
            {
                throw new IOException(label + " was replaced during assembly inspection: " + fullPath);
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
                throw new FileNotFoundException(label + " is missing.", path);
            }

            return new FileSnapshot(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }

        private static void VerifyExactContent(string path, byte[] expected, string label)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != expected.Length)
                {
                    throw new IOException(label + " was replaced before verification: " + path);
                }

                var buffer = new byte[Math.Min(BufferBytes, expected.Length)];
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
                            throw new IOException(label + " bytes were replaced during inspection: " + path);
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

        private struct FileSnapshot
        {
            internal FileSnapshot(long length, DateTime lastWriteUtc, DateTime creationUtc)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                CreationUtc = creationUtc;
            }

            internal long Length { get; private set; }
            private DateTime LastWriteUtc { get; set; }
            private DateTime CreationUtc { get; set; }

            internal bool Matches(FileSnapshot other)
            {
                return Length == other.Length
                    && LastWriteUtc == other.LastWriteUtc
                    && CreationUtc == other.CreationUtc;
            }
        }
    }
}
