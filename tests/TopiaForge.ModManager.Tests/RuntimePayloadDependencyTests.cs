using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimePayloadDependencyTests
    {
        private const string MetadataSha256 =
            "a0f6273f959a1ae587de408464aaad8cab9b6ae262a650d7b33a87208052ad3b";
        private const string ImmutableSha256 =
            "98de9f34c748b709f26c07fd4df54b2509218511dfaf741fa61aa2adb74e8c8c";

        public static void Run()
        {
            var repositoryRoot = FindRepositoryRoot();
            var loaderOutput = Path.Combine(
                repositoryRoot,
                "src",
                "TopiaForge.ModManager",
                "bin",
                "Release",
                "netstandard2.1");
            var corePath = Path.Combine(loaderOutput, "TopiaForge.ModManager.Core.dll");
            var metadataPath = Path.Combine(loaderOutput, "System.Reflection.Metadata.dll");
            var immutablePath = Path.Combine(loaderOutput, "System.Collections.Immutable.dll");

            AssertFileHash(metadataPath, MetadataSha256);
            AssertFileHash(immutablePath, ImmutableSha256);
            var core = ReadAssembly(corePath);
            var metadata = ReadAssembly(metadataPath);
            var immutable = ReadAssembly(immutablePath);

            AssertIdentity(metadata, "System.Reflection.Metadata", new Version(10, 0, 0, 0));
            AssertIdentity(immutable, "System.Collections.Immutable", new Version(10, 0, 0, 0));
            AssertReference(core, "System.Reflection.Metadata", new Version(10, 0, 0, 0));
            AssertReference(metadata, "System.Collections.Immutable", new Version(10, 0, 0, 0));
            AssertReference(metadata, "System.Memory", new Version(4, 0, 2, 0));
            AssertReference(metadata, "System.Buffers", new Version(4, 0, 2, 0));
            AssertReference(metadata, "System.Runtime.CompilerServices.Unsafe", new Version(6, 0, 0, 0));
            AssertReference(immutable, "System.Memory", new Version(4, 0, 2, 0));
            AssertReference(immutable, "System.Buffers", new Version(4, 0, 2, 0));
            AssertReference(immutable, "System.Runtime.CompilerServices.Unsafe", new Version(6, 0, 0, 0));

            var managedDirectory = GetRobotopiaManagedDirectory();
            Assert(!File.Exists(Path.Combine(managedDirectory, "System.Reflection.Metadata.dll")),
                "Robotopia must not be assumed to provide System.Reflection.Metadata");
            Assert(!File.Exists(Path.Combine(managedDirectory, "System.Collections.Immutable.dll")),
                "Robotopia must not be assumed to provide System.Collections.Immutable");
            var profile = new[]
            {
                new ProfileExpectation(
                    "System.Buffers.dll",
                    new Version(4, 0, 99, 0),
                    "762f8fdbe975e05b76be5fe996c53ce7c75e4a2830f2f50b02a5948ef6ba0aeb"),
                new ProfileExpectation(
                    "System.Memory.dll",
                    new Version(4, 0, 99, 0),
                    "c4f030a2cba7da7cdcf493257c24560e203d355904aee490d645a935842f834a"),
                new ProfileExpectation(
                    "System.Runtime.CompilerServices.Unsafe.dll",
                    new Version(6, 0, 0, 0),
                    "c0c628ecea65b4261cb88a1c322a3596bbde1dc2df102b88d63bab8c1a48d57a")
            };
            var available = new Dictionary<string, AssemblyInfo>(StringComparer.Ordinal);
            available.Add(metadata.Name, metadata);
            available.Add(immutable.Name, immutable);
            foreach (var expected in profile)
            {
                var path = Path.Combine(managedDirectory, expected.FileName);
                AssertFileHash(path, expected.Sha256);
                var assembly = ReadAssembly(path);
                Assert(assembly.Version == expected.Version,
                    expected.FileName + " identity version drifted: " + assembly.Version + ".");
                available.Add(assembly.Name, assembly);
            }

            AssertProfileResolves(metadata, available);
            AssertProfileResolves(immutable, available);
            Console.WriteLine("RuntimePayloadDependencyTests passed (12-DLL payload; Unity profile closure verified).");
        }

        private static void AssertProfileResolves(
            AssemblyInfo assembly,
            IReadOnlyDictionary<string, AssemblyInfo> available)
        {
            foreach (var dependencyName in new[]
                     {
                         "System.Collections.Immutable",
                         "System.Memory",
                         "System.Buffers",
                         "System.Runtime.CompilerServices.Unsafe"
                     })
            {
                if (!assembly.References.TryGetValue(dependencyName, out var required))
                {
                    continue;
                }

                if (!available.TryGetValue(dependencyName, out var supplied))
                {
                    throw new InvalidOperationException(
                        assembly.Name + " dependency is absent from the clean payload/profile: " +
                        dependencyName + ".");
                }
                Assert(supplied.Version >= required,
                    assembly.Name + " requires " + dependencyName + " " + required +
                    " but the clean profile supplies " + supplied.Version + ".");
            }
        }

        private static AssemblyInfo ReadAssembly(string path)
        {
            Assert(File.Exists(path), "Required managed assembly is missing: " + path);
            using (var stream = File.OpenRead(path))
            using (var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata))
            {
                Assert(pe.HasMetadata, "File has no managed metadata: " + path);
                var reader = pe.GetMetadataReader();
                Assert(reader.IsAssembly, "Managed metadata is not an assembly: " + path);
                var definition = reader.GetAssemblyDefinition();
                var references = new Dictionary<string, Version>(StringComparer.Ordinal);
                foreach (var handle in reader.AssemblyReferences)
                {
                    var reference = reader.GetAssemblyReference(handle);
                    references.Add(reader.GetString(reference.Name), reference.Version);
                }

                return new AssemblyInfo(
                    reader.GetString(definition.Name),
                    definition.Version,
                    references);
            }
        }

        private static void AssertIdentity(AssemblyInfo assembly, string name, Version version)
        {
            Assert(assembly.Name == name && assembly.Version == version,
                name + " identity drifted: " + assembly.Name + " " + assembly.Version + ".");
        }

        private static void AssertReference(AssemblyInfo assembly, string name, Version version)
        {
            Assert(assembly.References.TryGetValue(name, out var actual) && actual == version,
                assembly.Name + " must reference " + name + " " + version + ".");
        }

        private static void AssertFileHash(string path, string expected)
        {
            Assert(File.Exists(path), "Required managed assembly is missing: " + path);
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                var actual = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
                Assert(actual == expected,
                    Path.GetFileName(path) + " SHA-256 drifted. Expected " + expected + " but got " + actual + ".");
            }
        }

        private static string GetRobotopiaManagedDirectory()
        {
            var value = typeof(RuntimePayloadDependencyTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "TopiaForge.RobotopiaManagedDir")
                .Value;
            Assert(!string.IsNullOrWhiteSpace(value) && Directory.Exists(value),
                "TopiaForge.RobotopiaManagedDir must identify the restored build-2227 Managed directory.");
            return Path.GetFullPath(value!);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TopiaForge.slnx")))
            {
                directory = directory.Parent;
            }

            Assert(directory != null, "Could not locate the repository root for runtime payload validation.");
            return directory!.FullName;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class AssemblyInfo
        {
            public AssemblyInfo(string name, Version version, IReadOnlyDictionary<string, Version> references)
            {
                Name = name;
                Version = version;
                References = references;
            }

            public string Name { get; }
            public Version Version { get; }
            public IReadOnlyDictionary<string, Version> References { get; }
        }

        private sealed class ProfileExpectation
        {
            public ProfileExpectation(string fileName, Version version, string sha256)
            {
                FileName = fileName;
                Version = version;
                Sha256 = sha256;
            }

            public string FileName { get; }
            public Version Version { get; }
            public string Sha256 { get; }
        }
    }
}
