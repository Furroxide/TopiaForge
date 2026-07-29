using System;
using System.IO;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class BoundedTextFileTests
    {
        internal static void Run(string root)
        {
            var directory = Path.Combine(root, "bounded-text");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "manager.log");
            File.WriteAllText(path, "one\ntwo\nthree\nfour\n");

            var lines = BoundedTextFile.ReadTail(path, maxLines: 2, maxBytes: 1024);
            Assert(lines.Text == "three" + Environment.NewLine + "four" && lines.Truncated,
                "line cap should return only the newest complete lines and report truncation");

            File.WriteAllText(path, new string('x', 128) + "\nlast\n");
            var bytes = BoundedTextFile.ReadTail(path, maxLines: 10, maxBytes: 32);
            Assert(bytes.Text == "last" && bytes.Truncated,
                "byte cap should discard a partial leading line and retain complete tail lines");

            File.WriteAllBytes(path, new byte[] { 0xff, (byte)'\n' });
            var invalidRejected = false;
            try
            {
                BoundedTextFile.ReadTail(path, maxLines: 10, maxBytes: 32);
            }
            catch (InvalidDataException)
            {
                invalidRejected = true;
            }

            Assert(invalidRejected, "invalid UTF-8 logs should surface an actionable read failure");
            Console.WriteLine("BoundedTextFileTests passed.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Bounded text: " + message);
            }
        }
    }
}
