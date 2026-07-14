using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.ModManager.Core;

namespace Robotopia.ModManager.Tests
{
    internal static class ManifestPathValidationTests
    {
        public static void Run()
        {
            AcceptsNestedPortablePaths();
            RejectsUnsafeAssemblyPaths();
            RejectsUnsafeAndCollidingLists();
            Console.WriteLine("ManifestPathValidationTests passed.");
        }

        private static void AcceptsNestedPortablePaths()
        {
            var manifest = ValidManifest();
            manifest.EntryAssembly = "lib/Robotopia.Valid.dll";
            manifest.ApiAssemblies = new List<string> { "api/Robotopia.Contracts.dll" };
            manifest.LicenseFiles = new List<string> { "licenses/LICENSE.txt", "licenses/rocket-\U0001F680.txt" };

            Assert(ManifestValidator.Validate(manifest).Count == 0,
                "canonical nested package paths should remain valid");
        }

        private static void RejectsUnsafeAssemblyPaths()
        {
            foreach (var path in new[]
                     {
                         "../escape.dll",
                         "dir\\escape.dll",
                         "/absolute.dll",
                         "C:/absolute.dll",
                         "CON.dll",
                         "assembly.txt",
                         "dir//assembly.dll",
                         "dir/assembly.dll.",
                         "dir/cafe\u0301.dll",
                         "dir/fullwidth\uFF1Aname.dll",
                         "dir/" + new string('a', 256) + ".dll"
                     })
            {
                var manifest = ValidManifest();
                manifest.EntryAssembly = path;
                Assert(ManifestValidator.Validate(manifest)
                        .Any(error => error.Contains("entryAssembly", StringComparison.Ordinal)),
                    "entryAssembly should reject unsafe/non-assembly path: " + path);
            }
        }

        private static void RejectsUnsafeAndCollidingLists()
        {
            var manifest = ValidManifest();
            manifest.ApiAssemblies = new List<string>
            {
                "api/ff.dll",
                "api/\uFB00.dll",
                "not-an-assembly.txt",
                "../escape.dll"
            };
            manifest.LicenseFiles = new List<string>
            {
                "licenses/fullwidth-A.txt",
                "licenses/fullwidth-\uFF21.txt",
                "licenses/../escape.txt",
                "licenses/cafe\u0301.txt"
            };

            var errors = ManifestValidator.Validate(manifest);
            Assert(errors.Any(error => error.Contains("apiAssemblies contains duplicate or portable-collision")),
                "apiAssemblies should reject Unicode/case-fold duplicates");
            Assert(errors.Any(error => error.Contains("apiAssemblies") && error.Contains(".dll")),
                "apiAssemblies should require assembly file names");
            Assert(errors.Any(error => error.Contains("licenseFiles contains duplicate or portable-collision")),
                "licenseFiles should reject Unicode/case-fold duplicates");
            Assert(errors.Count(error => error.Contains("safe portable relative path")) >= 2,
                "unsafe list paths should produce actionable validation findings");

            var crossFieldCollision = ValidManifest();
            crossFieldCollision.EntryAssembly = "lib/ff.dll";
            crossFieldCollision.ApiAssemblies = new List<string> { "lib/\uFB00.dll" };
            Assert(ManifestValidator.Validate(crossFieldCollision)
                    .Any(error => error.Contains("apiAssemblies contains duplicate or portable-collision")),
                "entryAssembly and apiAssemblies should share one portable collision namespace");
        }

        private static ModManifest ValidManifest()
        {
            return new ModManifest
            {
                SchemaVersion = 2,
                Id = "manifest.paths",
                Name = "Manifest Paths",
                Version = "1.0.0",
                Author = new ModAuthor { Name = "Test Author" },
                EntryAssembly = "Robotopia.Paths.dll",
                EntryType = "Robotopia.Paths.Entry"
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Manifest path validation: " + message);
            }
        }
    }
}
