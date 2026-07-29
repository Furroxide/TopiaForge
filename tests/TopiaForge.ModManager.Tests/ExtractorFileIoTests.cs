using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TopiaForge.GameCompat.Extractor;

namespace TopiaForge.ModManager.Tests
{
    internal static class ExtractorFileIoTests
    {
        internal static void Run(string root)
        {
            var directory = Path.Combine(root, "extractor-file-io");
            Directory.CreateDirectory(directory);

            ExtractorReadsAreStrictBoundedAndStable(directory);
            ManifestLoadingUsesTheBoundedReader(directory);
            UiSmokeAssemblyReadsAreBoundedAndStable(directory);
            ProductionSourcesHaveNoReadAllFallback();
            Console.WriteLine("ExtractorFileIoTests passed.");
        }

        private static void ExtractorReadsAreStrictBoundedAndStable(string directory)
        {
            var valid = Path.Combine(directory, "valid.json");
            File.WriteAllBytes(valid, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' });
            Assert(ExtractorFileIo.ReadStableUtf8(valid, 16, "fixture") == "{}",
                "strict reader should accept UTF-8 and remove one optional BOM");

            var invalid = Path.Combine(directory, "invalid.json");
            File.WriteAllBytes(invalid, new byte[] { 0xC3, 0x28 });
            AssertThrows<InvalidDataException>(
                () => ExtractorFileIo.ReadStableUtf8(invalid, 16, "fixture"),
                "malformed UTF-8 must be rejected");

            var oversized = Path.Combine(directory, "oversized.json");
            File.WriteAllBytes(oversized, new byte[17]);
            AssertThrows<InvalidDataException>(
                () => ExtractorFileIo.ReadStableBytes(oversized, 16, "fixture"),
                "size must be rejected before allocating the file payload");

            AssertThrows<InvalidDataException>(
                () => ExtractorFileIo.ReadStableBytes(directory, 16, "fixture"),
                "directories must not be treated as files");

            var link = Path.Combine(directory, "extractor-link.json");
            if (TryCreateSymbolicLink(link, valid))
            {
                AssertThrows<InvalidDataException>(
                    () => ExtractorFileIo.ReadStableBytes(link, 16, "fixture"),
                    "symbolic links must be rejected");
            }

            var race = Path.Combine(directory, "extractor-race.json");
            File.WriteAllText(race, "first", new UTF8Encoding(false));
            AssertThrows<IOException>(
                () => ExtractorFileIo.ReadStableBytes(
                    race,
                    16,
                    "fixture",
                    () => File.WriteAllText(race, "other", new UTF8Encoding(false))),
                "same-length replacement races must be detected by content verification");
        }

        private static void ManifestLoadingUsesTheBoundedReader(string directory)
        {
            var repo = Path.Combine(directory, "manifest-repo");
            var bindings = Path.Combine(repo, "bindings");
            Directory.CreateDirectory(bindings);
            File.WriteAllBytes(
                Path.Combine(bindings, "oversized.gamebindings.json"),
                new byte[1024 * 1024 + 1]);

            AssertThrows<InvalidDataException>(
                () => ManifestLoader.LoadAll(repo),
                "manifest loading must enforce its input bound before parsing");
        }

        private static void UiSmokeAssemblyReadsAreBoundedAndStable(string directory)
        {
            var assembly = Path.Combine(directory, "smoke.dll");
            File.WriteAllBytes(assembly, new byte[] { 1, 2, 3, 4 });
            Assert(UiSmokeAssemblyFileIo.ReadStableBytes(assembly, 8, "smoke").SequenceEqual(
                    new byte[] { 1, 2, 3, 4 }),
                "Unity smoke reader should return the exact verified bytes");

            AssertThrows<InvalidDataException>(
                () => UiSmokeAssemblyFileIo.ReadStableBytes(assembly, 3, "smoke"),
                "Unity smoke assemblies must be size-bounded");

            var link = Path.Combine(directory, "smoke-link.dll");
            if (TryCreateSymbolicLink(link, assembly))
            {
                AssertThrows<InvalidDataException>(
                    () => UiSmokeAssemblyFileIo.ReadStableBytes(link, 8, "smoke"),
                    "Unity smoke assembly links must be rejected");
            }

            AssertThrows<IOException>(
                () => UiSmokeAssemblyFileIo.ReadStableBytes(
                    assembly,
                    8,
                    "smoke",
                    path => File.WriteAllBytes(path, new byte[] { 4, 3, 2, 1 })),
                "assembly replacement during identity inspection must be detected");
        }

        private static void ProductionSourcesHaveNoReadAllFallback()
        {
            var root = Program.FindRepoRoot();
            var productionRoots = new[]
            {
                Path.Combine(root, "src"),
                Path.Combine(root, "mods"),
                Path.Combine(root, "tools"),
                Path.Combine(root, "templates"),
            };
            var offenders = new List<string>();
            foreach (var productionRoot in productionRoots)
            {
                foreach (var path in Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative.Split('/').Any(part => part == "bin" || part == "obj" || part == "Library"))
                    {
                        continue;
                    }

                    var source = File.ReadAllText(path);
                    if (source.Contains("File.ReadAllText(", StringComparison.Ordinal)
                        || source.Contains("File.ReadAllBytes(", StringComparison.Ordinal)
                        || source.Contains("File.ReadAllLines(", StringComparison.Ordinal))
                    {
                        offenders.Add(relative);
                    }
                }
            }

            Assert(offenders.Count == 0,
                "production C# must not reintroduce unbounded File.ReadAll* calls: "
                + string.Join(", ", offenders));

            var auditor = File.ReadAllText(Path.Combine(
                root,
                "src",
                "TopiaForge.GameCompat.Extractor",
                "GameReflectionAuditor.cs"));
            Assert(!auditor.Contains("XDocument.Load(project", StringComparison.Ordinal),
                "project XML must be bounded before it is parsed");
        }

        private static bool TryCreateSymbolicLink(string link, string target)
        {
            try
            {
                File.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                                              || exception is UnauthorizedAccessException
                                              || exception is IOException)
            {
                return false;
            }
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

            throw new InvalidOperationException("Extractor file I/O: " + message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Extractor file I/O: " + message);
            }
        }
    }
}
