using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ManagedModAssemblyValidatorTests
    {
        private const string FixtureAssembly = "TopiaForge.ValidTestMod.dll";
        private const string SdkAssemblyName = "TopiaForge.Mods.Abstractions";
        private static readonly Version CompatibleSdkVersion = new Version(0, 1, 0, 0);

        public static void Run(string root)
        {
            TestEntryPointValidation(root);
            TestApiAssemblyValidation(root);
            TestPrivateDllValidation(root);
            TestSdkAndTargetFrameworkCompatibility(root);
            TestBaseTypeResolutionScope(root);
            TestDuplicateAssemblyIdentities(root);
            TestBundledFrameworkAssemblies(root);
            Console.WriteLine("ManagedModAssemblyValidatorTests passed.");
        }

        private static void TestEntryPointValidation(string root)
        {
            var package = CreateFixturePackage(root, "entry-valid");
            var manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            Assert(ManagedModAssemblyValidator.Validate(package, manifest).Count == 0,
                "a valid TopiaForgeMod assembly must pass metadata validation");

            manifest.EntryType = "TopiaForge.ValidTestMod.MissingMod";
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "was not found");

            manifest.EntryType = "TopiaForge.ValidTestMod.AbstractMod";
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "must not be abstract");

            manifest.EntryType = "TopiaForge.ValidTestMod.NoDefaultConstructorMod";
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "parameterless constructor");

            manifest.EntryType = "TopiaForge.ValidTestMod.InternalMod";
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "must be public");

            package = CreateEmptyPackage(root, "entry-bad-pe");
            File.WriteAllText(Path.Combine(package, "Broken.dll"), "not a portable executable");
            manifest = NewManifest("Broken.dll", "Broken.Mod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "not a valid PE image");

            package = CreateEmptyPackage(root, "entry-renamed");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, FixtureAssembly),
                Path.Combine(package, "Renamed.dll"),
                true);
            manifest = NewManifest("Renamed.dll", "TopiaForge.ValidTestMod.ValidMod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "does not match file name");
        }

        private static void TestApiAssemblyValidation(string root)
        {
            var package = CreateFixturePackage(root, "api-valid");
            WriteSyntheticAssembly(
                Path.Combine(package, "Example.Api.dll"),
                "Example.Api",
                "Example.Api.Marker",
                CompatibleSdkVersion,
                ".NETStandard,Version=v2.1",
                SdkAssemblyName,
                addCompatibleSdkReference: false);
            var manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            manifest.ApiAssemblies.Add("Example.Api.dll");
            Assert(ManagedModAssemblyValidator.Validate(package, manifest).Count == 0,
                "a valid declared API assembly must pass metadata validation");

            package = CreateFixturePackage(root, "api-missing");
            manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            manifest.ApiAssemblies.Add("Missing.Api.dll");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "apiAssemblies entry was not found");

            package = CreateFixturePackage(root, "api-bad-pe");
            File.WriteAllText(Path.Combine(package, "Broken.Api.dll"), "not a portable executable");
            manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            manifest.ApiAssemblies.Add("Broken.Api.dll");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "not a valid PE image");

            package = CreateFixturePackage(root, "api-renamed");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, FixtureAssembly),
                Path.Combine(package, "Renamed.Api.dll"),
                true);
            manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            manifest.ApiAssemblies.Add("Renamed.Api.dll");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "does not match file name");
        }

        private static void TestPrivateDllValidation(string root)
        {
            var package = CreateFixturePackage(root, "private-bad-pe");
            File.WriteAllText(Path.Combine(package, "Private.Helper.dll"), "not a portable executable");
            var manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "Private.Helper.dll");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "not a valid PE image");
        }

        private static void TestSdkAndTargetFrameworkCompatibility(string root)
        {
            var package = CreateEmptyPackage(root, "sdk-incompatible");
            WriteSyntheticAssembly(
                Path.Combine(package, "IncompatibleSdk.dll"),
                "IncompatibleSdk",
                "Synthetic.IncompatibleSdkMod",
                new Version(2, 0, 0, 0),
                ".NETStandard,Version=v2.1",
                SdkAssemblyName,
                addCompatibleSdkReference: false);
            var manifest = NewManifest("IncompatibleSdk.dll", "Synthetic.IncompatibleSdkMod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "the loader requires unsigned TopiaForge.Mods.Abstractions 0.1.0.0");

            package = CreateEmptyPackage(root, "tfm-incompatible");
            WriteSyntheticAssembly(
                Path.Combine(package, "IncompatibleTfm.dll"),
                "IncompatibleTfm",
                "Synthetic.IncompatibleTfmMod",
                CompatibleSdkVersion,
                ".NETCoreApp,Version=v8.0",
                SdkAssemblyName,
                addCompatibleSdkReference: false);
            manifest = NewManifest("IncompatibleTfm.dll", "Synthetic.IncompatibleTfmMod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "target framework");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), ".NETStandard,Version=v2.1");

            package = CreateFixturePackage(root, "api-tfm-incompatible");
            WriteSyntheticAssembly(
                Path.Combine(package, "Incompatible.Api.dll"),
                "Incompatible.Api",
                "Synthetic.ApiMarker",
                CompatibleSdkVersion,
                ".NETCoreApp,Version=v8.0",
                SdkAssemblyName,
                addCompatibleSdkReference: false);
            manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            manifest.ApiAssemblies.Add("Incompatible.Api.dll");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest),
                "apiAssemblies entry target framework");
        }

        private static void TestBaseTypeResolutionScope(string root)
        {
            var package = CreateEmptyPackage(root, "spoof-base");
            WriteSyntheticAssembly(
                Path.Combine(package, "SpoofBase.dll"),
                "SpoofBase",
                "Synthetic.SpoofBaseMod",
                CompatibleSdkVersion,
                ".NETStandard,Version=v2.1",
                "Spoof.Framework",
                addCompatibleSdkReference: true);
            var manifest = NewManifest("SpoofBase.dll", "Synthetic.SpoofBaseMod");
            var errors = ManagedModAssemblyValidator.Validate(package, manifest);
            AssertNotContains(errors, "must reference " + SdkAssemblyName,
                "the spoof fixture deliberately includes a valid but unused SDK reference");
            AssertContains(errors, "resolved from " + SdkAssemblyName);
        }

        private static void TestDuplicateAssemblyIdentities(string root)
        {
            var package = CreateFixturePackage(root, "duplicate-identity");
            var privateDirectory = Directory.CreateDirectory(Path.Combine(package, "private"));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, FixtureAssembly),
                Path.Combine(privateDirectory.FullName, FixtureAssembly),
                true);
            var manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "Duplicate managed assembly identity");
        }

        private static void TestBundledFrameworkAssemblies(string root)
        {
            foreach (var frameworkAssembly in new[]
                     {
                         "TopiaForge.Mods.Abstractions.dll",
                         "TopiaForge.Mods.Analyzers.dll",
                         "TopiaForge.Mods.Chronos.dll",
                         "TopiaForge.Mods.CreatorContent.dll",
                         "TopiaForge.Mods.Multiplayer.dll",
                         "TopiaForge.Mods.Interop.Unity.dll",
                         "TopiaForge.Mods.Prompts.dll",
                         "TopiaForge.Mods.RobotKit.dll",
                         "TopiaForge.Mods.Testing.dll",
                         "TopiaForge.Mods.Ugc.dll",
                         "TopiaForge.Mods.UnityUi.dll",
                         "TopiaForge.Mods.Worlds.dll"
                     })
            {
                var package = CreateFixturePackage(root, "framework-" + frameworkAssembly.Replace('.', '-'));
                File.WriteAllText(Path.Combine(package, frameworkAssembly), "bundled SDK contract");
                var manifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
                AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), frameworkAssembly);
                AssertContains(ManagedModAssemblyValidator.Validate(package, manifest), "must not bundle");
            }

            var symbolsPackage = CreateFixturePackage(root, "framework-symbols");
            var symbolsDirectory = Directory.CreateDirectory(Path.Combine(symbolsPackage, "symbols"));
            const string symbolsName = "TOPIAFORGE.MODS.ABSTRACTIONS.PDB";
            File.WriteAllText(Path.Combine(symbolsDirectory.FullName, symbolsName), "bundled SDK symbols");
            var symbolsManifest = NewManifest(FixtureAssembly, "TopiaForge.ValidTestMod.ValidMod");
            AssertContains(ManagedModAssemblyValidator.Validate(symbolsPackage, symbolsManifest), symbolsName);
            AssertContains(ManagedModAssemblyValidator.Validate(symbolsPackage, symbolsManifest), "must not bundle");
        }

        private static string CreateFixturePackage(string root, string name)
        {
            var package = CreateEmptyPackage(root, name);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, FixtureAssembly),
                Path.Combine(package, FixtureAssembly),
                true);
            return package;
        }

        private static string CreateEmptyPackage(string root, string name)
        {
            var package = Path.Combine(root, "assembly-validation", name);
            if (Directory.Exists(package))
            {
                Directory.Delete(package, true);
            }

            Directory.CreateDirectory(package);
            return package;
        }

        private static ModManifest NewManifest(string assembly, string type)
        {
            return new ModManifest
            {
                SchemaVersion = 5,
                Id = "tests.valid-mod",
                Name = "Valid test mod",
                Author = new ModAuthor { Name = "TopiaForge" },
                Version = "1.0.0",
                EntryAssembly = assembly,
                EntryType = type,
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
                SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0"
            };
        }

        private static void WriteSyntheticAssembly(
            string path,
            string assemblyName,
            string entryType,
            Version baseAssemblyVersion,
            string targetFramework,
            string baseAssemblyName,
            bool addCompatibleSdkReference)
        {
            var metadata = new MetadataBuilder();
            metadata.AddModule(
                0,
                metadata.GetOrAddString(Path.GetFileName(path)),
                metadata.GetOrAddGuid(Guid.NewGuid()),
                default,
                default);
            metadata.AddAssembly(
                metadata.GetOrAddString(assemblyName),
                new Version(1, 0, 0, 0),
                default,
                default,
                default,
                AssemblyHashAlgorithm.None);

            var systemRuntime = metadata.AddAssemblyReference(
                metadata.GetOrAddString("System.Runtime"),
                new Version(4, 2, 2, 0),
                default,
                default,
                default,
                default);
            if (addCompatibleSdkReference && !string.Equals(baseAssemblyName, SdkAssemblyName, StringComparison.Ordinal))
            {
                metadata.AddAssemblyReference(
                    metadata.GetOrAddString(SdkAssemblyName),
                    CompatibleSdkVersion,
                    default,
                    default,
                    default,
                    default);
            }

            var baseAssembly = metadata.AddAssemblyReference(
                metadata.GetOrAddString(baseAssemblyName),
                baseAssemblyVersion,
                default,
                default,
                default,
                default);
            var baseType = metadata.AddTypeReference(
                baseAssembly,
                metadata.GetOrAddString("TopiaForge.Mods"),
                metadata.GetOrAddString("TopiaForgeMod"));

            var constructorSignature = new BlobBuilder();
            constructorSignature.WriteByte(0x20); // instance method
            constructorSignature.WriteByte(0x00); // zero parameters
            constructorSignature.WriteByte(0x01); // void
            var constructor = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.Runtime,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(constructorSignature),
                0,
                MetadataTokens.ParameterHandle(1));

            metadata.AddTypeDefinition(
                TypeAttributes.NotPublic,
                default,
                metadata.GetOrAddString("<Module>"),
                default,
                MetadataTokens.FieldDefinitionHandle(1),
                constructor);

            var separator = entryType.LastIndexOf('.');
            var entryNamespace = separator < 0 ? string.Empty : entryType.Substring(0, separator);
            var entryName = separator < 0 ? entryType : entryType.Substring(separator + 1);
            metadata.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
                metadata.GetOrAddString(entryNamespace),
                metadata.GetOrAddString(entryName),
                baseType,
                MetadataTokens.FieldDefinitionHandle(1),
                constructor);

            var targetAttributeType = metadata.AddTypeReference(
                systemRuntime,
                metadata.GetOrAddString("System.Runtime.Versioning"),
                metadata.GetOrAddString("TargetFrameworkAttribute"));
            var targetAttributeSignature = new BlobBuilder();
            targetAttributeSignature.WriteByte(0x20); // instance method
            targetAttributeSignature.WriteByte(0x01); // one parameter
            targetAttributeSignature.WriteByte(0x01); // void
            targetAttributeSignature.WriteByte(0x0e); // string
            var targetAttributeConstructor = metadata.AddMemberReference(
                targetAttributeType,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(targetAttributeSignature));

            var targetFrameworkBytes = Encoding.UTF8.GetBytes(targetFramework);
            if (targetFrameworkBytes.Length >= 0x80)
            {
                throw new InvalidOperationException("The synthetic target framework string is unexpectedly long.");
            }

            var targetAttributeValue = new BlobBuilder();
            targetAttributeValue.WriteByte(0x01); // custom-attribute prolog, little endian
            targetAttributeValue.WriteByte(0x00);
            targetAttributeValue.WriteByte((byte)targetFrameworkBytes.Length);
            targetAttributeValue.WriteBytes(targetFrameworkBytes);
            targetAttributeValue.WriteByte(0x00); // zero named arguments, little endian
            targetAttributeValue.WriteByte(0x00);
            metadata.AddCustomAttribute(
                MetadataTokens.EntityHandle(TableIndex.Assembly, 1),
                targetAttributeConstructor,
                metadata.GetOrAddBlob(targetAttributeValue));

            var peBuilder = new ManagedPEBuilder(
                new PEHeaderBuilder(
                    imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
                new MetadataRootBuilder(metadata),
                new BlobBuilder(),
                flags: CorFlags.ILOnly);
            var image = new BlobBuilder();
            peBuilder.Serialize(image);
            File.WriteAllBytes(path, image.ToArray());
        }

        private static void AssertContains(IEnumerable<string> errors, string text)
        {
            if (!errors.Any(error => error.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Expected assembly validation error containing '" + text + "', got: " + string.Join("; ", errors));
            }
        }

        private static void AssertNotContains(IEnumerable<string> errors, string text, string message)
        {
            if (errors.Any(error => error.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(message + ": " + string.Join("; ", errors));
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
