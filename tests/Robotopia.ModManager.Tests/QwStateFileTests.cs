using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager.Tests
{
    internal static class QwStateFileTests
    {
        public static void Run(string root)
        {
            var directory = Path.Combine(root, "qw-state-file");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "qwui.state");

            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["z-last"] = "line1\nline2",
                ["a-first"] = "tab\tbackslash\\"
            };
            QwStateFileCodec.Save(path, expected);
            var firstBytes = File.ReadAllBytes(path);
            var actual = QwStateFileCodec.Load(path);
            Assert(actual.Count == expected.Count && actual.All(pair => expected[pair.Key] == pair.Value),
                "escaped state should round-trip exactly");
            Assert(File.ReadAllText(path).StartsWith("a-first\t", StringComparison.Ordinal),
                "state output should be deterministic by ordinal key");

            QwStateFileCodec.Save(path, expected);
            Assert(firstBytes.SequenceEqual(File.ReadAllBytes(path)),
                "identical state writes should produce identical bytes");
            Assert(!Directory.EnumerateFiles(directory).Any(file => file.Contains(".tmp-", StringComparison.Ordinal)
                || file.Contains(".bak-", StringComparison.Ordinal)),
                "successful atomic writes should not leave temporary or rollback files");

            var oversized = new Dictionary<string, string>(expected, StringComparer.Ordinal)
            {
                ["too-large"] = new string('x', QwStateFileCodec.MaxValueChars + 1)
            };
            AssertThrows<InvalidDataException>(() => QwStateFileCodec.Save(path, oversized),
                "oversized values must fail before replacement");
            Assert(firstBytes.SequenceEqual(File.ReadAllBytes(path)),
                "a rejected write must preserve the previous state bytes");

            File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
            AssertThrows<InvalidDataException>(() => QwStateFileCodec.Load(path),
                "malformed UTF-8 state must fail closed");
            File.WriteAllBytes(path, new byte[QwStateFileCodec.MaxFileBytes + 1]);
            AssertThrows<InvalidDataException>(() => QwStateFileCodec.Load(path),
                "oversized state must be rejected before decoding");

            Console.WriteLine("QwStateFileTests passed.");
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException("Qw state file: " + message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Qw state file: " + message);
            }
        }
    }
}
