using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Bounded, strict-UTF-8 reads for user-visible logs and diagnostics.</summary>
    public static class BoundedTextFile
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static TextTailResult ReadTail(string path, int maxLines, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file path is required.", nameof(path));
            }

            if (maxLines < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLines));
            }

            if (maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            if (!File.Exists(path))
            {
                return new TextTailResult(string.Empty, truncated: false);
            }

            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("Text diagnostics must be regular files: " + path);
            }

            byte[] bytes;
            bool truncated;
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                truncated = input.Length > maxBytes;
                var count = checked((int)Math.Min(input.Length, maxBytes));
                var start = input.Length - count;
                input.Position = start;
                bytes = new byte[count];
                ReadExactly(input, bytes);
            }

            var offset = 0;
            if (truncated)
            {
                // The byte window may start in the middle of a UTF-8 code point or a huge line. Discard that
                // partial line at the byte level, then decode only a complete line boundary.
                while (offset < bytes.Length && bytes[offset] != (byte)'\n' && bytes[offset] != (byte)'\r')
                {
                    offset++;
                }

                while (offset < bytes.Length && (bytes[offset] == (byte)'\n' || bytes[offset] == (byte)'\r'))
                {
                    offset++;
                }
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Text diagnostics are not valid UTF-8: " + path, ex);
            }

            var tail = new Queue<string>(maxLines);
            using (var reader = new StringReader(text))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (tail.Count == maxLines)
                    {
                        tail.Dequeue();
                        truncated = true;
                    }

                    tail.Enqueue(line);
                }
            }

            return new TextTailResult(string.Join(Environment.NewLine, tail), truncated);
        }

        private static void ReadExactly(Stream input, byte[] bytes)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = input.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                {
                    throw new EndOfStreamException("Text file changed while it was being read.");
                }

                offset += count;
            }
        }
    }

    public readonly struct TextTailResult
    {
        public TextTailResult(string text, bool truncated)
        {
            Text = text ?? string.Empty;
            Truncated = truncated;
        }

        public string Text { get; }
        public bool Truncated { get; }
    }
}
