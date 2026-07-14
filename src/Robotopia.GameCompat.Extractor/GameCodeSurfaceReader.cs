using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Robotopia.GameCompat;

namespace Robotopia.GameCompat.Extractor
{
    // Turns a real Managed dir into a SurfaceSnapshot using MetadataLoadContext. Everything here is metadata-only:
    // we read Type/MethodInfo/FieldInfo shapes and enum literal values, but never invoke or instantiate (MLC
    // forbids it). Two rules learned from the feasibility spike drive the defensive coding:
    //   * enum values come from FieldInfo.GetRawConstantValue() on the literal fields, never Enum.GetValues.
    //   * a member whose signature references a MISSING assembly throws lazily at read time; that is recorded as
    //     Unreadable (could-not-verify), never as Absent (genuinely gone).
    internal sealed class GameCodeSurfaceReader : IDisposable
    {
        private readonly string _managedDir;
        private readonly MetadataLoadContext _context;
        private readonly Dictionary<string, Assembly?> _assemblies = new(StringComparer.Ordinal);

        private const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public GameCodeSurfaceReader(string managedDir)
        {
            _managedDir = managedDir;
            var dlls = Directory.GetFiles(managedDir, "*.dll");
            // netstandard is present in the self-contained Unity Managed folder and GameCode targets netstandard2.1,
            // so it is the deterministic core-assembly choice (proven in the spike).
            _context = new MetadataLoadContext(new PathAssemblyResolver(dlls), coreAssemblyName: "netstandard");
        }

        public SurfaceSnapshot Extract(
            IEnumerable<BindingManifest> manifests,
            string extractorVersion,
            string gameVersionLabel,
            string capturedUtc,
            string gameVersion = "")
        {
            var bindings = manifests.SelectMany(m => m.Bindings).ToList();

            var snapshot = new SurfaceSnapshot
            {
                ExtractorVersion = extractorVersion,
                GameVersionLabel = gameVersionLabel,
                GameVersion = gameVersion,
                CapturedUtc = capturedUtc,
            };

            foreach (var reference in BuildReferenceSet())
            {
                snapshot.ReferenceSet.Add(reference);
            }

            var gameCode = LoadAssembly("GameCode");
            if (gameCode != null)
            {
                snapshot.GameCodeMvid = SafeMvid(gameCode);
            }

            // Full-surface types (everything except pure simple-name-walk lookups).
            var typeTargets = bindings
                .Where(b => b.MatchMode != MatchMode.SimpleNameWalk && b.MatchMode != MatchMode.Uncheckable)
                .Select(b => (b.Assembly, b.DeclaringType))
                .Distinct();

            foreach (var (assemblyName, typeName) in typeTargets)
            {
                var key = assemblyName + "|" + typeName;
                if (snapshot.Types.ContainsKey(key))
                {
                    continue;
                }

                snapshot.Types[key] = ReadType(assemblyName, typeName);
            }

            // Simple-name-walk candidates: retain both the count and each matching type's complete surface.
            // A count alone can prove that a component name exists, but cannot prove that the field/method/property
            // the mod subsequently reflects still exists on that component.
            var simpleTargets = bindings
                .Where(b => b.MatchMode == MatchMode.SimpleNameWalk)
                .Select(b => (b.Assembly, Simple: SimpleName(b.DeclaringType)))
                .Distinct();

            foreach (var (assemblyName, simple) in simpleTargets)
            {
                var key = assemblyName + "|" + simple;
                if (snapshot.SimpleNameCounts.ContainsKey(key))
                {
                    continue;
                }

                var scan = ReadSimpleNameTypes(assemblyName, simple);
                snapshot.SimpleNameCounts[key] = scan.Count;
                foreach (var type in scan.Types)
                {
                    if (!snapshot.Types.ContainsKey(type.TypeKey))
                    {
                        snapshot.Types[type.TypeKey] = type;
                    }
                }
            }

            return snapshot;
        }

        private TypeSurface ReadType(string assemblyName, string typeName)
        {
            var surface = new TypeSurface
            {
                TypeKey = assemblyName + "|" + typeName,
                Assembly = assemblyName,
                FullName = typeName,
                SimpleName = SimpleName(typeName),
                Status = SurfaceStatus.Absent,
            };

            var assembly = LoadAssembly(assemblyName);
            if (assembly == null)
            {
                // The whole owning assembly is missing from this environment: cannot say the type is gone.
                surface.Status = SurfaceStatus.Unreadable;
                return surface;
            }

            Type? type;
            try
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            }
            catch (FileNotFoundException)
            {
                surface.Status = SurfaceStatus.Unreadable;
                return surface;
            }
            catch (TypeLoadException)
            {
                surface.Status = SurfaceStatus.Unreadable;
                return surface;
            }

            if (type == null)
            {
                surface.Status = SurfaceStatus.Absent;
                return surface;
            }

            try
            {
                surface.FullName = type.FullName ?? typeName;
                surface.SimpleName = type.Name;
                surface.IsEnum = type.IsEnum;
                surface.IsAbstract = type.IsAbstract;
                surface.IsInterface = type.IsInterface;
                PopulateBaseChain(type, surface);

                if (type.IsEnum)
                {
                    ReadEnumMembers(type, surface);
                }
                else
                {
                    ReadMembers(type, surface);
                }

                surface.Status = SurfaceStatus.Resolved;
            }
            catch (FileNotFoundException)
            {
                // A member/base signature referenced an assembly absent from this environment. Could-not-verify.
                surface.Status = SurfaceStatus.Unreadable;
            }
            catch (TypeLoadException)
            {
                surface.Status = SurfaceStatus.Unreadable;
            }

            return surface;
        }

        private void PopulateBaseChain(Type type, TypeSurface surface)
        {
            var cursor = type.BaseType;
            var guard = 0;
            while (cursor != null && guard++ < 32)
            {
                surface.BaseChainSimpleNames.Add(cursor.Name);
                cursor = cursor.BaseType;
            }
        }

        private void ReadEnumMembers(Type type, TypeSurface surface)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral)
                {
                    continue;
                }

                try
                {
                    var raw = field.GetRawConstantValue();
                    surface.EnumMembers[field.Name] = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    // A single unreadable literal shouldn't poison the whole enum; skip it.
                }
            }
        }

        private void ReadMembers(Type type, TypeSurface surface)
        {
            foreach (var method in type.GetMethods(AllMembers))
            {
                if (method.IsSpecialName || !IsGameRelevant(method.DeclaringType))
                {
                    continue;
                }

                var entry = new MethodSurface
                {
                    Name = method.Name,
                    ReturnType = NormalizeTypeName(method.ReturnType),
                    IsPublic = method.IsPublic,
                    IsStatic = method.IsStatic,
                };

                foreach (var parameter in method.GetParameters())
                {
                    entry.Parameters.Add(NormalizeTypeName(parameter.ParameterType));
                }

                surface.Methods.Add(entry);
            }

            foreach (var field in type.GetFields(AllMembers))
            {
                if (!IsGameRelevant(field.DeclaringType))
                {
                    continue;
                }

                surface.Fields.Add(new FieldSurface
                {
                    Name = field.Name,
                    Type = NormalizeTypeName(field.FieldType),
                    IsPublic = field.IsPublic,
                    IsStatic = field.IsStatic,
                });
            }

            foreach (var property in type.GetProperties(AllMembers))
            {
                if (!IsGameRelevant(property.DeclaringType))
                {
                    continue;
                }

                surface.Properties.Add(new PropertySurface
                {
                    Name = property.Name,
                    Type = NormalizeTypeName(property.PropertyType),
                    CanRead = property.CanRead,
                    CanWrite = property.CanWrite,
                });
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var entry = new ConstructorSurface { IsPublic = ctor.IsPublic };
                foreach (var parameter in ctor.GetParameters())
                {
                    entry.Parameters.Add(NormalizeTypeName(parameter.ParameterType));
                }

                surface.Constructors.Add(entry);
            }
        }

        // Keep the type's own members plus inherited GAME members (so a member pushed to a game base class is still
        // seen), but drop framework noise inherited from System/Unity — those are never the target of these bindings
        // and would bloat the snapshot and its diffs.
        private static bool IsGameRelevant(Type? declaringType)
        {
            if (declaringType == null)
            {
                return true;
            }

            var ns = declaringType.Namespace ?? string.Empty;
            if (ns.StartsWith("System", StringComparison.Ordinal) ||
                ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                ns.StartsWith("Unity.", StringComparison.Ordinal) ||
                ns.StartsWith("Cysharp", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private (long Count, IReadOnlyList<TypeSurface> Types) ReadSimpleNameTypes(
            string assemblyName,
            string simpleName)
        {
            var assembly = LoadAssembly(assemblyName);
            if (assembly == null)
            {
                return (-1, Array.Empty<TypeSurface>()); // unknown: assembly missing from this environment
            }

            var matches = new List<TypeSurface>();
            var types = SafeGetTypes(assembly, out var complete);
            foreach (var type in types)
            {
                if (type != null && string.Equals(type.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(ReadType(assemblyName, type.FullName ?? type.Name));
                }
            }

            return (complete ? matches.Count : -1, matches);
        }

        private static Type?[] SafeGetTypes(Assembly assembly, out bool complete)
        {
            try
            {
                complete = true;
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                complete = false;
                return ex.Types;
            }
            catch (FileNotFoundException)
            {
                complete = false;
                return Array.Empty<Type?>();
            }
        }

        private Assembly? LoadAssembly(string assemblyName)
        {
            if (_assemblies.TryGetValue(assemblyName, out var cached))
            {
                return cached;
            }

            Assembly? assembly = null;
            try
            {
                assembly = _context.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                // Fall back to a direct file path (assembly simple name == file stem in the Unity Managed layout).
                var path = Path.Combine(_managedDir, assemblyName + ".dll");
                if (File.Exists(path))
                {
                    try
                    {
                        assembly = _context.LoadFromAssemblyPath(path);
                    }
                    catch
                    {
                        assembly = null;
                    }
                }
            }

            _assemblies[assemblyName] = assembly;
            return assembly;
        }

        private IEnumerable<string> BuildReferenceSet()
        {
            var references = new List<string>();
            foreach (var dll in Directory.GetFiles(_managedDir, "*.dll"))
            {
                try
                {
                    var name = AssemblyName.GetAssemblyName(dll);
                    references.Add(name.Name + " " + (name.Version?.ToString() ?? "0.0.0.0"));
                }
                catch
                {
                    // Native or non-.NET dll in the Managed folder; ignore for the reference set.
                }
            }

            references.Sort(StringComparer.Ordinal);
            return references;
        }

        private static string SafeMvid(Assembly assembly)
        {
            try
            {
                return assembly.ManifestModule.ModuleVersionId.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        // Resolver-independent type name: namespace-qualified, no assembly identity, generics expanded by name.
        // This defuses the "FullName embeds assembly version -> false signature diffs across environments" hazard.
        private static string NormalizeTypeName(Type type)
        {
            if (type.IsByRef)
            {
                return NormalizeTypeName(type.GetElementType()!) + "&";
            }

            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                return NormalizeTypeName(type.GetElementType()!) + "[" + new string(',', rank - 1) + "]";
            }

            if (type.IsPointer)
            {
                return NormalizeTypeName(type.GetElementType()!) + "*";
            }

            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var name = StripArity(definition.FullName ?? definition.Name);
                var args = type.GetGenericArguments().Select(NormalizeTypeName);
                return name + "<" + string.Join(", ", args) + ">";
            }

            return type.FullName ?? (type.Namespace != null ? type.Namespace + "." + type.Name : type.Name);
        }

        private static string StripArity(string name)
        {
            var tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        private static string SimpleName(string typeName)
        {
            var text = typeName;
            var dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                text = text.Substring(dot + 1);
            }

            var nested = text.LastIndexOf('+');
            if (nested >= 0)
            {
                text = text.Substring(nested + 1);
            }

            return text;
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
