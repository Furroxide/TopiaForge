using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Validates managed package assemblies from metadata without loading or executing package code.</summary>
    public static class ManagedModAssemblyValidator
    {
        private const string SdkAssemblyName = "TopiaForge.Mods.Abstractions";
        private const string SupportedTargetFramework = ".NETStandard,Version=v2.1";
        private static readonly Version CompatibleSdkAssemblyVersion = new Version(1, 0, 0, 0);

        private static readonly HashSet<string> ProhibitedExactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "0Harmony.dll",
            "GameCode.dll",
            "TopiaForge.ModManager.dll",
            "TopiaForge.ModManager.Core.dll",
            "TopiaForge.Mods.Abstractions.dll",
            "TopiaForge.Mods.Analyzers.dll",
            "TopiaForge.Mods.Chronos.dll",
            "TopiaForge.Mods.Multiplayer.dll",
            "TopiaForge.Mods.Interop.Unity.dll",
            "TopiaForge.Mods.Prompts.dll",
            "TopiaForge.Mods.RobotKit.dll",
            "TopiaForge.Mods.Testing.dll",
            "TopiaForge.Mods.Ugc.dll",
            "TopiaForge.Mods.UnityUi.dll",
            "TopiaForge.Mods.Worlds.dll"
        };

        /// <summary>Validates every package DLL and the declared V1 mod entry point.</summary>
        public static IReadOnlyList<string> Validate(
            string packageRoot,
            ModManifest manifest,
            bool allowLegacyInterface = false)
        {
            if (packageRoot == null)
            {
                throw new ArgumentNullException(nameof(packageRoot));
            }

            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var errors = new List<string>();
            string entryPath;
            try
            {
                entryPath = PathSafety.CombineRelativeChild(
                    packageRoot,
                    manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
            }
            catch (Exception ex)
            {
                errors.Add("entryAssembly path is unsafe: " + ex.Message);
                return errors;
            }

            if (!File.Exists(entryPath))
            {
                errors.Add("entryAssembly was not found in package: " + manifest.EntryAssembly);
                return errors;
            }

            var apiPaths = ResolveApiAssemblies(packageRoot, manifest.ApiAssemblies, errors);
            IReadOnlyList<string> packageFiles;
            try
            {
                packageFiles = EnumerateRegularFiles(packageRoot, entryPath, apiPaths);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                errors.Add("Package DLLs could not be enumerated safely: " + ex.Message);
                return errors;
            }

            var dllPaths = packageFiles
                .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var file in packageFiles)
            {
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(name);
                var isManagedArtifact =
                    string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase);
                var assemblyName = Path.GetFileNameWithoutExtension(name) + ".dll";
                if (isManagedArtifact &&
                    (ProhibitedExactNames.Contains(assemblyName) ||
                     assemblyName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                     assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("Package must not bundle framework/runtime assembly: " + name + ".");
                }
            }

            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dll in dllPaths)
            {
                ValidatePortableExecutable(
                    packageRoot,
                    dll,
                    PathSafety.AreSame(dll, entryPath),
                    apiPaths.Any(api => PathSafety.AreSame(api, dll)),
                    manifest,
                    allowLegacyInterface,
                    identities,
                    errors);
            }

            return errors;
        }

        private static IReadOnlyList<string> ResolveApiAssemblies(
            string packageRoot,
            IEnumerable<string>? apiAssemblies,
            List<string> errors)
        {
            var paths = new List<string>();
            foreach (var relativePath in apiAssemblies ?? Array.Empty<string>())
            {
                string path;
                try
                {
                    path = PathSafety.CombineRelativeChild(
                        packageRoot,
                        (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
                }
                catch (Exception ex)
                {
                    errors.Add("apiAssemblies path '" + relativePath + "' is unsafe: " + ex.Message);
                    continue;
                }

                if (!File.Exists(path))
                {
                    errors.Add("apiAssemblies entry was not found in package: " + relativePath);
                    continue;
                }

                if (!paths.Any(existing => PathSafety.AreSame(existing, path)))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private static IReadOnlyList<string> EnumerateRegularFiles(
            string packageRoot,
            string entryPath,
            IReadOnlyList<string> apiPaths)
        {
            const int maximumEntries = 8192;
            var root = Path.GetFullPath(packageRoot);
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("Package root must be a regular directory.");
            }

            var paths = new List<string>();
            var directories = new Stack<string>();
            directories.Push(root);
            var entriesSeen = 0;
            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                             .OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (++entriesSeen > maximumEntries)
                    {
                        throw new InvalidDataException("Package contains more than 8192 filesystem entries.");
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        throw new InvalidDataException(
                            "Package contains a linked or special filesystem entry: " +
                            Path.GetRelativePath(root, path) + ".");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(path);
                    }
                    else
                    {
                        paths.Add(Path.GetFullPath(path));
                    }
                }
            }

            AddUnique(paths, entryPath);
            foreach (var apiPath in apiPaths)
            {
                AddUnique(paths, apiPath);
            }

            return paths
                .OrderBy(path => Path.GetRelativePath(packageRoot, path), StringComparer.Ordinal)
                .ToList();
        }

        private static void AddUnique(List<string> paths, string path)
        {
            if (!paths.Any(existing => PathSafety.AreSame(existing, path)))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        private static void ValidatePortableExecutable(
            string packageRoot,
            string path,
            bool isEntry,
            bool isApiAssembly,
            ModManifest manifest,
            bool allowLegacyInterface,
            Dictionary<string, string> identities,
            List<string> errors)
        {
            var displayPath = Path.GetRelativePath(packageRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata))
                {
                    // Accessing PEHeaders forces malformed DOS/COFF/PE images to fail even when they carry no CLI metadata.
                    if (peReader.PEHeaders.PEHeader == null)
                    {
                        errors.Add("Package DLL is not a valid PE image: " + displayPath + ".");
                        return;
                    }

                    if (!peReader.HasMetadata)
                    {
                        if (isEntry || isApiAssembly)
                        {
                            errors.Add(DescribeRole(isEntry, isApiAssembly) + " is not a managed .NET assembly: " + displayPath + ".");
                        }

                        // A structurally valid native PE can be a private implementation dependency. It has no
                        // managed identity to compare, and is never accepted as entry or exported API surface.
                        return;
                    }

                    var reader = peReader.GetMetadataReader();
                    if (!reader.IsAssembly)
                    {
                        errors.Add("Package DLL metadata does not define an assembly: " + displayPath + ".");
                        return;
                    }

                    var assembly = reader.GetAssemblyDefinition();
                    var assemblyName = reader.GetString(assembly.Name);
                    var expectedName = Path.GetFileNameWithoutExtension(path);
                    if (!string.Equals(assemblyName, expectedName, StringComparison.Ordinal))
                    {
                        errors.Add(
                            "Assembly identity '" + assemblyName + "' does not match file name '" +
                            expectedName + "' for " + displayPath + ".");
                    }

                    if (identities.TryGetValue(assemblyName, out var firstPath))
                    {
                        errors.Add(
                            "Duplicate managed assembly identity '" + assemblyName + "' is provided by '" +
                            firstPath + "' and '" + displayPath + "'.");
                    }
                    else
                    {
                        identities[assemblyName] = displayPath;
                    }

                    if (isEntry)
                    {
                        ValidateEntryAssembly(reader, manifest, allowLegacyInterface, errors);
                    }

                    if (isApiAssembly)
                    {
                        ValidateApiAssembly(reader, displayPath, errors);
                    }
                }
            }
            catch (BadImageFormatException ex)
            {
                errors.Add("Package DLL is not a valid PE image: " + displayPath + ". " + ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                errors.Add("Package DLL metadata could not be validated for " + displayPath + ": " + ex.Message);
            }
        }

        private static string DescribeRole(bool isEntry, bool isApiAssembly)
        {
            if (isEntry)
            {
                return "entryAssembly";
            }

            return isApiAssembly ? "apiAssemblies entry" : "Package DLL";
        }

        private static void ValidateEntryAssembly(
            MetadataReader reader,
            ModManifest manifest,
            bool allowLegacyInterface,
            List<string> errors)
        {
            var sdkReferences = reader.AssemblyReferences
                .Select(reader.GetAssemblyReference)
                .Where(reference => string.Equals(
                    reader.GetString(reference.Name),
                    SdkAssemblyName,
                    StringComparison.Ordinal))
                .ToArray();
            if (sdkReferences.Length == 0)
            {
                errors.Add("entryAssembly must reference " + SdkAssemblyName + " " + CompatibleSdkAssemblyVersion + ".");
            }
            else if (sdkReferences.Length != 1)
            {
                errors.Add("entryAssembly contains duplicate " + SdkAssemblyName + " assembly references.");
            }

            foreach (var reference in sdkReferences)
            {
                if (!IsCompatibleSdkAssemblyReference(reader, reference))
                {
                    errors.Add(
                        "entryAssembly references incompatible SDK identity '" +
                        DescribeAssemblyReference(reader, reference) +
                        "'; V1 requires unsigned " + SdkAssemblyName + " " +
                        CompatibleSdkAssemblyVersion + ".");
                }
            }

            if (!TryReadTargetFramework(reader, out var targetFramework))
            {
                errors.Add("entryAssembly must declare TargetFrameworkAttribute for " + SupportedTargetFramework + ".");
            }
            else if (!string.Equals(targetFramework, SupportedTargetFramework, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "entryAssembly target framework '" + targetFramework + "' is incompatible; V1 requires " +
                    SupportedTargetFramework + ".");
            }

            var entryHandle = FindType(reader, manifest.EntryType);
            if (entryHandle.IsNil)
            {
                errors.Add("entryType was not found in entryAssembly: " + manifest.EntryType + ".");
                return;
            }

            var entry = reader.GetTypeDefinition(entryHandle);
            if ((entry.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
            {
                errors.Add("entryType must be public.");
            }

            if ((entry.Attributes & TypeAttributes.Abstract) != 0)
            {
                errors.Add("entryType must not be abstract.");
            }

            if (!HasPublicParameterlessConstructor(reader, entry))
            {
                errors.Add("entryType must define a public parameterless constructor.");
            }

            if (!DerivesFromTopiaForgeMod(reader, entryHandle) &&
                !(allowLegacyInterface && ImplementsLegacyInterface(reader, entry)))
            {
                errors.Add(
                    "entryType must derive from TopiaForge.Mods.TopiaForgeMod resolved from " +
                    SdkAssemblyName + " " + CompatibleSdkAssemblyVersion + ".");
            }
        }

        private static void ValidateApiAssembly(
            MetadataReader reader,
            string displayPath,
            List<string> errors)
        {
            if (!TryReadTargetFramework(reader, out var targetFramework))
            {
                errors.Add("apiAssemblies entry must declare TargetFrameworkAttribute: " + displayPath + ".");
            }
            else if (!string.Equals(targetFramework, SupportedTargetFramework, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "apiAssemblies entry target framework '" + targetFramework +
                    "' is incompatible; V1 requires " + SupportedTargetFramework + ": " + displayPath + ".");
            }

            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                var name = reader.GetString(reference.Name);
                if (string.Equals(name, SdkAssemblyName, StringComparison.Ordinal)
                    && !IsCompatibleSdkAssemblyReference(reader, reference))
                {
                    errors.Add(
                        "apiAssemblies entry references incompatible SDK identity '" +
                        DescribeAssemblyReference(reader, reference) + "': " + displayPath + ".");
                }

                if (string.Equals(name, "GameCode", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "0Harmony", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "apiAssemblies entry must remain safe and cannot reference native/game/patch assembly '" +
                        name + "': " + displayPath + ".");
                }
            }
        }

        private static bool TryReadTargetFramework(MetadataReader reader, out string targetFramework)
        {
            targetFramework = string.Empty;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!TryGetAttributeTypeReference(reader, attribute.Constructor, out var typeReference))
                {
                    continue;
                }

                var type = reader.GetTypeReference(typeReference);
                if (!string.Equals(reader.GetString(type.Namespace), "System.Runtime.Versioning", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(type.Name), "TargetFrameworkAttribute", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var value = reader.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() != 1)
                    {
                        return false;
                    }

                    targetFramework = value.ReadSerializedString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(targetFramework);
                }
                catch (BadImageFormatException)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryGetAttributeTypeReference(
            MetadataReader reader,
            EntityHandle constructor,
            out TypeReferenceHandle typeReference)
        {
            typeReference = default;
            if (constructor.Kind != HandleKind.MemberReference)
            {
                return false;
            }

            var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
            if (!string.Equals(reader.GetString(member.Name), ".ctor", StringComparison.Ordinal) ||
                member.Parent.Kind != HandleKind.TypeReference)
            {
                return false;
            }

            typeReference = (TypeReferenceHandle)member.Parent;
            return true;
        }

        private static TypeDefinitionHandle FindType(MetadataReader reader, string fullName)
        {
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var name = reader.GetString(definition.Name);
                var ns = reader.GetString(definition.Namespace);
                var candidate = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (string.Equals(candidate, fullName, StringComparison.Ordinal))
                {
                    return handle;
                }
            }

            return default;
        }

        private static bool HasPublicParameterlessConstructor(MetadataReader reader, TypeDefinition type)
        {
            foreach (var handle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(handle);
                if (!string.Equals(reader.GetString(method.Name), ".ctor", StringComparison.Ordinal) ||
                    (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                    (method.Attributes & MethodAttributes.Static) != 0)
                {
                    continue;
                }

                var signature = reader.GetBlobReader(method.Signature);
                var header = signature.ReadSignatureHeader();
                if (header.IsGeneric)
                {
                    signature.ReadCompressedInteger();
                }

                if (signature.ReadCompressedInteger() == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DerivesFromTopiaForgeMod(MetadataReader reader, TypeDefinitionHandle entryHandle)
        {
            var visited = new HashSet<TypeDefinitionHandle>();
            var current = entryHandle;
            while (!current.IsNil && visited.Add(current))
            {
                var definition = reader.GetTypeDefinition(current);
                var parent = definition.BaseType;
                if (parent.Kind == HandleKind.TypeReference)
                {
                    var reference = reader.GetTypeReference((TypeReferenceHandle)parent);
                    return string.Equals(reader.GetString(reference.Namespace), "TopiaForge.Mods", StringComparison.Ordinal) &&
                        string.Equals(reader.GetString(reference.Name), "TopiaForgeMod", StringComparison.Ordinal) &&
                        ResolvesToCompatibleSdkAssembly(reader, reference.ResolutionScope);
                }

                if (parent.Kind != HandleKind.TypeDefinition)
                {
                    return false;
                }

                current = (TypeDefinitionHandle)parent;
            }

            return false;
        }

        private static bool ImplementsLegacyInterface(MetadataReader reader, TypeDefinition entry)
        {
            foreach (var handle in entry.GetInterfaceImplementations())
            {
                var implementation = reader.GetInterfaceImplementation(handle);
                if (implementation.Interface.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var reference = reader.GetTypeReference((TypeReferenceHandle)implementation.Interface);
                if (string.Equals(reader.GetString(reference.Namespace), "TopiaForge.Mods", StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(reference.Name), "ITopiaForgeMod", StringComparison.Ordinal) &&
                    ResolvesToCompatibleSdkAssembly(reader, reference.ResolutionScope))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ResolvesToCompatibleSdkAssembly(MetadataReader reader, EntityHandle scope)
        {
            if (scope.Kind != HandleKind.AssemblyReference)
            {
                return false;
            }

            var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
            return IsCompatibleSdkAssemblyReference(reader, assembly);
        }

        private static bool IsCompatibleSdkAssemblyReference(
            MetadataReader reader,
            AssemblyReference assembly)
        {
            return string.Equals(reader.GetString(assembly.Name), SdkAssemblyName, StringComparison.Ordinal) &&
                assembly.Version == CompatibleSdkAssemblyVersion &&
                string.IsNullOrEmpty(reader.GetString(assembly.Culture)) &&
                reader.GetBlobBytes(assembly.PublicKeyOrToken).Length == 0;
        }

        private static string DescribeAssemblyReference(
            MetadataReader reader,
            AssemblyReference assembly)
        {
            var culture = reader.GetString(assembly.Culture);
            var token = reader.GetBlobBytes(assembly.PublicKeyOrToken);
            return reader.GetString(assembly.Name) + ", Version=" + assembly.Version +
                ", Culture=" + (string.IsNullOrEmpty(culture) ? "neutral" : culture) +
                ", PublicKeyToken=" + (token.Length == 0
                    ? "null"
                    : string.Concat(token.Select(value => value.ToString("x2"))));
        }
    }
}
