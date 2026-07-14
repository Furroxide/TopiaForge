using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Robotopia.GameCompat
{
    // Did a type/member resolve in the game metadata, genuinely not exist, or fail to read because the capture
    // environment was missing a referenced assembly? The last case must NEVER be reported to a user as "broken";
    // it means "could not verify here". Keeping the three apart is what stops false red pills on a partial install.
    public enum SurfaceStatus
    {
        Resolved = 0,
        Absent = 1,
        Unreadable = 2,
    }

    public sealed class MethodSurface
    {
        public string Name { get; set; } = string.Empty;
        public IList<string> Parameters { get; } = new List<string>();
        public string ReturnType { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }

        public string Signature => Name + "(" + string.Join(", ", Parameters) + ")";

        public JsonObject ToJson()
        {
            var parameters = new JsonArray();
            foreach (var parameter in Parameters)
            {
                parameters.Add(new JsonString(parameter));
            }

            return new JsonObject()
                .Set("name", Name)
                .Set("parameters", parameters)
                .Set("returnType", ReturnType)
                .Set("isPublic", IsPublic)
                .Set("isStatic", IsStatic);
        }

        public static MethodSurface FromJson(JsonObject json)
        {
            var method = new MethodSurface
            {
                Name = json.GetString("name"),
                ReturnType = json.GetString("returnType"),
                IsPublic = json.GetBool("isPublic"),
                IsStatic = json.GetBool("isStatic"),
            };

            foreach (var item in json.GetArray("parameters").Items)
            {
                method.Parameters.Add(item.AsString());
            }

            return method;
        }
    }

    public sealed class FieldSurface
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }

        public JsonObject ToJson() => new JsonObject()
            .Set("name", Name)
            .Set("type", Type)
            .Set("isPublic", IsPublic)
            .Set("isStatic", IsStatic);

        public static FieldSurface FromJson(JsonObject json) => new FieldSurface
        {
            Name = json.GetString("name"),
            Type = json.GetString("type"),
            IsPublic = json.GetBool("isPublic"),
            IsStatic = json.GetBool("isStatic"),
        };
    }

    public sealed class PropertySurface
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }

        public JsonObject ToJson() => new JsonObject()
            .Set("name", Name)
            .Set("type", Type)
            .Set("canRead", CanRead)
            .Set("canWrite", CanWrite);

        public static PropertySurface FromJson(JsonObject json) => new PropertySurface
        {
            Name = json.GetString("name"),
            Type = json.GetString("type"),
            CanRead = json.GetBool("canRead"),
            CanWrite = json.GetBool("canWrite"),
        };
    }

    public sealed class ConstructorSurface
    {
        public IList<string> Parameters { get; } = new List<string>();
        public bool IsPublic { get; set; }

        public JsonObject ToJson()
        {
            var parameters = new JsonArray();
            foreach (var parameter in Parameters)
            {
                parameters.Add(new JsonString(parameter));
            }

            return new JsonObject().Set("parameters", parameters).Set("isPublic", IsPublic);
        }

        public static ConstructorSurface FromJson(JsonObject json)
        {
            var ctor = new ConstructorSurface { IsPublic = json.GetBool("isPublic") };
            foreach (var item in json.GetArray("parameters").Items)
            {
                ctor.Parameters.Add(item.AsString());
            }

            return ctor;
        }
    }

    // The full metadata surface of one type the mods reach into: enough to answer "does the member the mod binds
    // still exist with a compatible shape?" for every match mode.
    public sealed class TypeSurface
    {
        public string TypeKey { get; set; } = string.Empty; // "<Assembly>|<FullName>"
        public string Assembly { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string SimpleName { get; set; } = string.Empty;
        public SurfaceStatus Status { get; set; } = SurfaceStatus.Absent;
        public bool IsEnum { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsInterface { get; set; }

        public IList<MethodSurface> Methods { get; } = new List<MethodSurface>();
        public IList<FieldSurface> Fields { get; } = new List<FieldSurface>();
        public IList<PropertySurface> Properties { get; } = new List<PropertySurface>();
        public IList<ConstructorSurface> Constructors { get; } = new List<ConstructorSurface>();

        // Enum member name -> integer ordinal (read via GetRawConstantValue). The heart of ordinal-drift detection.
        public IDictionary<string, long> EnumMembers { get; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

        // Simple names of the base-type chain (for reasoning about SimpleNameWalk matches).
        public IList<string> BaseChainSimpleNames { get; } = new List<string>();

        public JsonObject ToJson()
        {
            var methods = new JsonArray();
            var sortedMethods = new List<MethodSurface>(Methods);
            sortedMethods.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
            foreach (var method in sortedMethods)
            {
                methods.Add(method.ToJson());
            }

            var fields = new JsonArray();
            var sortedFields = new List<FieldSurface>(Fields);
            sortedFields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var field in sortedFields)
            {
                fields.Add(field.ToJson());
            }

            var properties = new JsonArray();
            var sortedProperties = new List<PropertySurface>(Properties);
            sortedProperties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var property in sortedProperties)
            {
                properties.Add(property.ToJson());
            }

            var constructors = new JsonArray();
            var sortedCtors = new List<ConstructorSurface>(Constructors);
            sortedCtors.Sort((a, b) => string.CompareOrdinal(
                string.Join(",", a.Parameters), string.Join(",", b.Parameters)));
            foreach (var ctor in sortedCtors)
            {
                constructors.Add(ctor.ToJson());
            }

            var enumMembers = new JsonObject();
            foreach (var member in EnumMembers)
            {
                enumMembers.Set(member.Key, member.Value);
            }

            var baseChain = new JsonArray();
            foreach (var name in BaseChainSimpleNames)
            {
                baseChain.Add(new JsonString(name));
            }

            return new JsonObject()
                .Set("typeKey", TypeKey)
                .Set("assembly", Assembly)
                .Set("fullName", FullName)
                .Set("simpleName", SimpleName)
                .Set("status", Status.ToString())
                .Set("isEnum", IsEnum)
                .Set("isAbstract", IsAbstract)
                .Set("isInterface", IsInterface)
                .Set("baseChainSimpleNames", baseChain)
                .Set("constructors", constructors)
                .Set("methods", methods)
                .Set("fields", fields)
                .Set("properties", properties)
                .Set("enumMembers", enumMembers);
        }

        public static TypeSurface FromJson(JsonObject json)
        {
            var surface = new TypeSurface
            {
                TypeKey = json.GetString("typeKey"),
                Assembly = json.GetString("assembly"),
                FullName = json.GetString("fullName"),
                SimpleName = json.GetString("simpleName"),
                Status = Enum.TryParse<SurfaceStatus>(json.GetString("status"), out var status) ? status : SurfaceStatus.Absent,
                IsEnum = json.GetBool("isEnum"),
                IsAbstract = json.GetBool("isAbstract"),
                IsInterface = json.GetBool("isInterface"),
            };

            foreach (var item in json.GetArray("baseChainSimpleNames").Items)
            {
                surface.BaseChainSimpleNames.Add(item.AsString());
            }

            foreach (var item in json.GetArray("constructors").Items)
            {
                surface.Constructors.Add(ConstructorSurface.FromJson(item.AsObject()));
            }

            foreach (var item in json.GetArray("methods").Items)
            {
                surface.Methods.Add(MethodSurface.FromJson(item.AsObject()));
            }

            foreach (var item in json.GetArray("fields").Items)
            {
                surface.Fields.Add(FieldSurface.FromJson(item.AsObject()));
            }

            foreach (var item in json.GetArray("properties").Items)
            {
                surface.Properties.Add(PropertySurface.FromJson(item.AsObject()));
            }

            foreach (var member in json.GetObject("enumMembers").Members)
            {
                surface.EnumMembers[member.Key] = member.Value.AsLong();
            }

            return surface;
        }
    }

    // A captured picture of the exact game surface the mods depend on. Two identity keys:
    //   surfaceContentHash — the PRIMARY "is this the same surface?" key (content-addressed; identical surfaces
    //                        compare equal regardless of a fresh Unity rebuild).
    //   gameCodeMvid       — advisory provenance only (changes every compile, so it is never gated on).
    public sealed class SurfaceSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string ExtractorVersion { get; set; } = string.Empty;
        public string CapturedUtc { get; set; } = string.Empty;
        public string GameCodeMvid { get; set; } = string.Empty;
        public string GameVersionLabel { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;

        // Sorted list of "<name> <version>" for every assembly in the resolver set. If two snapshots have
        // different reference sets, a differing method signature is more likely an environment artifact than a
        // real game change — the differ downgrades such findings.
        public IList<string> ReferenceSet { get; } = new List<string>();

        // typeKey -> full surface.
        public IDictionary<string, TypeSurface> Types { get; } = new SortedDictionary<string, TypeSurface>(StringComparer.Ordinal);

        // "<Assembly>|<simpleName>" -> number of types with that simple name (for SimpleNameWalk bindings).
        public IDictionary<string, long> SimpleNameCounts { get; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

        public TypeSurface? FindType(string typeKey) => Types.TryGetValue(typeKey, out var surface) ? surface : null;

        // Canonical JSON of ONLY the content-bearing sections (types + simpleNameCounts + referenceSet).
        // Deliberately excludes timestamps/mvid so the content hash is stable across captures of the same surface.
        private JsonObject ToContentJson()
        {
            var types = new JsonObject();
            foreach (var type in Types)
            {
                types.Set(type.Key, type.Value.ToJson());
            }

            var simpleNames = new JsonObject();
            foreach (var entry in SimpleNameCounts)
            {
                simpleNames.Set(entry.Key, entry.Value);
            }

            var referenceSet = new JsonArray();
            foreach (var reference in ReferenceSet)
            {
                referenceSet.Add(new JsonString(reference));
            }

            return new JsonObject()
                .Set("types", types)
                .Set("simpleNameCounts", simpleNames)
                .Set("referenceSet", referenceSet);
        }

        public string ComputeContentHash()
        {
            var canonical = ToContentJson().ToCanonical();
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public string ToCanonicalJson()
        {
            var content = ToContentJson();
            var root = new JsonObject()
                .Set("schemaVersion", SchemaVersion)
                .Set("extractorVersion", ExtractorVersion)
                .Set("capturedUtc", CapturedUtc)
                .Set("gameCodeMvid", GameCodeMvid)
                .Set("gameVersionLabel", GameVersionLabel)
                .Set("surfaceContentHash", ComputeContentHash());

            // Additive metadata: old baselines must remain byte-canonical when the version was not captured.
            if (!string.IsNullOrEmpty(GameVersion))
            {
                root.Set("gameVersion", GameVersion);
            }

            foreach (var member in content.Members)
            {
                root.Set(member.Key, member.Value);
            }

            return root.ToCanonical();
        }

        public static SurfaceSnapshot Parse(string json)
        {
            var root = JsonValue.Parse(json).AsObject();
            var snapshot = new SurfaceSnapshot
            {
                SchemaVersion = (int)root.GetLong("schemaVersion", 1),
                ExtractorVersion = root.GetString("extractorVersion"),
                CapturedUtc = root.GetString("capturedUtc"),
                GameCodeMvid = root.GetString("gameCodeMvid"),
                GameVersionLabel = root.GetString("gameVersionLabel"),
                GameVersion = root.GetString("gameVersion"),
            };

            foreach (var item in root.GetArray("referenceSet").Items)
            {
                snapshot.ReferenceSet.Add(item.AsString());
            }

            foreach (var member in root.GetObject("types").Members)
            {
                snapshot.Types[member.Key] = TypeSurface.FromJson(member.Value.AsObject());
            }

            foreach (var member in root.GetObject("simpleNameCounts").Members)
            {
                snapshot.SimpleNameCounts[member.Key] = member.Value.AsLong();
            }

            return snapshot;
        }

        // A snapshot is a valid known-good baseline only if nothing was left Unreadable (an environment-poisoned
        // capture must never be committed as "known good"). Returns the offending type keys.
        public IEnumerable<string> UnreadableTypes()
        {
            var unreadable = new List<string>();
            foreach (var type in Types.Values)
            {
                if (type.Status == SurfaceStatus.Unreadable)
                {
                    unreadable.Add(type.TypeKey);
                }
            }

            return unreadable;
        }
    }
}
