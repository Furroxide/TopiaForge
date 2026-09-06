using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TopiaForge.GameCompat;
using TopiaForge.GameCompat.Extractor;

namespace TopiaForge.ModManager.Tests
{
    internal static class GameCompatActivationTests
    {
        public static void Run()
        {
            var failures = new List<Exception>();
            foreach (var test in new (string Name, Action Run)[] {
                ("exact-zero-arity", ExactZeroArity), ("generic-arity", GenericArity), ("strict-type-identity", StrictTypeIdentity), ("public-static", PublicStatic),
                ("one-overload-contract", OneOverloadContract), ("public-constructor", PublicConstructor),
                ("field-access", FieldAccess), ("property-accessor", PropertyAccessor), ("property-indexer", PropertyIndexer),
                ("external-own-members", ExternalOwnMembers), ("nested-generic-shape", NestedGenericShape), ("constraint-roundtrip", ConstraintRoundTrip), ("malformed-constraints", MalformedConstraints), ("manager-linked-audit", ManagerLinkedAudit), ("auditor-directory-boundary", AuditorDirectoryBoundary) })
            {
                try { test.Run(); Console.WriteLine("GameCompat activation " + test.Name + ": PASS"); }
                catch (Exception exception)
                { failures.Add(exception); Console.WriteLine("GameCompat activation " + test.Name + ": FAIL " + exception.Message); }
            }
            if (failures.Count != 0) throw new AggregateException("GameCompat activation regressions failed.", failures);
        }

        private static JsonObject Contract(string kind = "Method") => new JsonObject().Set("id", "native.ready")
            .Set("kind", kind).Set("assembly", "GameCode").Set("declaringType", "NativeReady")
            .Set("member", "FindPlayer").Set("criticality", "Critical").Set("exactParameters", true)
            .Set("isPublic", true).Set("isStatic", true).Set("returnType", "PlayerController");

        private static TypeSurface NativeType() => new TypeSurface
        {
            TypeKey = "GameCode|NativeReady",
            Assembly = "GameCode",
            FullName = "NativeReady",
            SimpleName = "NativeReady",
            Status = SurfaceStatus.Resolved
        };
        private static MethodSurface Method(bool isPublic = true, bool isStatic = true, string result = "PlayerController") =>
            new MethodSurface { Name = "FindPlayer", ReturnType = result, IsPublic = isPublic, IsStatic = isStatic };
        private static CompatReport Resolve(TypeSurface type, JsonObject contract)
        {
            var manifest = new BindingManifest { ModId = "test.native" };
            manifest.Bindings.Add(GameBinding.FromJson(contract));
            var snapshot = new SurfaceSnapshot(); snapshot.Types.Add(type.TypeKey, type);
            return SurfaceDiffer.ResolveManifests(new[] { manifest }, snapshot);
        }
        private static void Assert(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }

        private static void ExactZeroArity()
        {
            var type = NativeType(); var method = Method(); method.Parameters.Add("System.Int32"); type.Methods.Add(method);
            Assert(Resolve(type, Contract()).HasBreakingChanges, "FindPlayer(int) cannot satisfy an exact FindPlayer() binding.");
            method.Parameters.Clear();
            Assert(!Resolve(type, Contract()).HasBreakingChanges, "The exact public static no-argument method must resolve.");
        }
        private static void GenericArity()
        {
            var type = NativeType();
            type.Methods.Add(MethodSurface.FromJson(Method().ToJson().Set("genericArity", 1)));
            Assert(Resolve(type, Contract().Set("genericArity", 0)).HasBreakingChanges,
                "A generic-only method cannot satisfy the non-generic method invoked by native readiness.");
        }
        private static void StrictTypeIdentity()
        {
            var type = NativeType(); type.Methods.Add(Method(result: "Unrelated.PlayerController"));
            Assert(Resolve(type, Contract()).HasBreakingChanges,
                "Exact native return types must retain namespace identity and case.");
        }
        private static void PublicStatic()
        {
            foreach (var method in new[] { Method(isPublic: false), Method(isStatic: false), Method(result: "System.String") })
            {
                var type = NativeType(); type.Methods.Add(method);
                Assert(Resolve(type, Contract()).HasBreakingChanges, "Private, instance, or wrong-return methods cannot satisfy public static readiness.");
            }
        }
        private static void OneOverloadContract()
        {
            var type = NativeType(); type.Methods.Add(Method(result: "System.String")); type.Methods.Add(Method(isPublic: false));
            Assert(Resolve(type, Contract()).HasBreakingChanges, "Visibility and return type must hold on the same overload.");
        }
        private static void PublicConstructor()
        {
            var type = NativeType(); type.Constructors.Add(new ConstructorSurface { IsPublic = false });
            Assert(Resolve(type, Contract("Constructor").Set("isStatic", false)).HasBreakingChanges,
                "A private parameterless constructor cannot satisfy Activator.CreateInstance(Type).");
        }
        private static void FieldAccess()
        {
            var type = NativeType(); type.Fields.Add(new FieldSurface { Name = "FindPlayer", Type = "PlayerController", IsPublic = false, IsStatic = false });
            Assert(Resolve(type, Contract("Field")).HasBreakingChanges, "Field visibility and static ownership are part of the contract.");
        }
        private static void PropertyAccessor()
        {
            var type = NativeType();
            type.Properties.Add(PropertySurface.FromJson(new JsonObject().Set("name", "FindPlayer").Set("type", "PlayerController")
                .Set("canRead", true).Set("canWrite", true).Set("getterIsPublic", false).Set("setterIsPublic", true)
                .Set("isStatic", true)));
            Assert(Resolve(type, Contract("Property").Set("requireReadable", true)).HasBreakingChanges,
                "A public setter does not satisfy a required public getter.");
        }
        private static void PropertyIndexer()
        {
            var type = NativeType();
            type.Properties.Add(PropertySurface.FromJson(new JsonObject().Set("name", "FindPlayer").Set("type", "PlayerController")
                .Set("canRead", true).Set("getterIsPublic", true).Set("isStatic", true).Set("indexParameterCount", 1)));
            Assert(Resolve(type, Contract("Property").Set("requireReadable", true).Set("indexParameterCount", 0)).HasBreakingChanges,
                "An indexed property cannot satisfy the zero-index IsCompleted property native readiness reads.");
        }
        private static void ConstraintRoundTrip()
        {
            var roundTrip = GameBinding.FromJson(Contract()).ToJson();
            Assert(roundTrip.GetBool("exactParameters") && roundTrip.GetBool("isPublic") && roundTrip.GetBool("isStatic"),
                "Canonical serialization must retain callable constraints.");
        }
        private static void ExternalOwnMembers()
        {
            // ReadMembers uses Type metadata only; an uninitialized reader avoids acquiring any game files.
            var reader = RuntimeHelpers.GetUninitializedObject(typeof(GameCodeSurfaceReader));
            var readMembers = typeof(GameCodeSurfaceReader).GetMethod("ReadMembers", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var surface = new TypeSurface();
            readMembers.Invoke(reader, new object[] { typeof(System.Threading.Tasks.Task), surface });
            Assert(surface.Methods.Any(method => method.Name == "GetAwaiter"),
                "A requested external type must retain its own methods while inherited framework noise is omitted.");
        }
        private static void NestedGenericShape()
        {
            var normalize = typeof(GameCodeSurfaceReader).GetMethod("NormalizeTypeName", BindingFlags.Static | BindingFlags.NonPublic)!;
            var nested = typeof(Dictionary<,>).GetNestedType("Enumerator")!;
            var shape = (string)normalize.Invoke(null, new object[] { nested })!;
            Assert(shape.Contains("Dictionary+Enumerator<", StringComparison.Ordinal),
                "Generic arity normalization must preserve the nested type instead of replacing it with its outer type.");
        }

        private static void MalformedConstraints()
        {
            foreach (var name in new[] { "exactParameters", "isPublic", "isStatic", "requireReadable", "requireWritable" })
            {
                var rejected = false;
                try { GameBinding.FromJson(Contract().Set(name, "false")); }
                catch (FormatException) { rejected = true; }
                Assert(rejected, "A string must not silently disable boolean constraint " + name + ".");
            }
        }

        private static void ManagerLinkedAudit()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeManagerAudit-");
            var root = owned.FullName;
            var manager = Path.Combine(root, "src", "TopiaForge.ModManager");
            var shared = Path.Combine(root, "mods", "Shared");
            Directory.CreateDirectory(manager); Directory.CreateDirectory(shared); Directory.CreateDirectory(Path.Combine(root, "bindings"));
            try
            {
                var manifest = new BindingManifest { ModId = "io.github.furroxide.topiaforge.modmanager" };
                File.WriteAllText(Path.Combine(root, "bindings", manifest.ModId + ".gamebindings.json"), manifest.ToCanonicalJson());
                File.WriteAllText(Path.Combine(manager, "TopiaForge.ModManager.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../../mods/Shared/UgcNoOpLaunchRequest.cs\" /></ItemGroup></Project>");
                File.WriteAllText(Path.Combine(manager, "Native.cs"), "class Native { object Bind() => System.Type.GetType(\"ManagerOnly, GameCode\"); }");
                File.WriteAllText(Path.Combine(shared, "UgcNoOpLaunchRequest.cs"), "class Shared { object Bind() => System.Type.GetType(\"SharedOnly, GameCode\"); }");
                var findings = GameReflectionAuditor.Audit(root);
                Assert(findings.Any(f => f.Kind == "undeclared" && f.Detail.Contains("ManagerOnly", StringComparison.Ordinal))
                    && findings.Any(f => f.Kind == "undeclared" && f.Detail.Contains("SharedOnly", StringComparison.Ordinal)),
                    "Manager native sources and their actual linked helper must both be audited.");
            }
            finally { owned.Delete(recursive: true); }
        }
        private static void AuditorDirectoryBoundary()
        {
            var failures = new List<Exception>();
            foreach (var location in new[] { "manager", "src", "nested", "linked", "glob" })
            {
                try { AssertDirectoryLinkRejected(location); }
                catch (Exception error) { failures.Add(new InvalidOperationException(location + ": " + error.Message, error)); }
            }
            if (failures.Count != 0) throw new AggregateException("The source audit crossed its repository boundary.", failures);
        }

        private static void AssertDirectoryLinkRejected(string location)
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeAuditBoundary-");
            FileSystemInfo? link = null;
            try
            {
                var root = owned.CreateSubdirectory("repository").FullName;
                var outside = owned.CreateSubdirectory("outside").FullName;
                var bindings = Directory.CreateDirectory(Path.Combine(root, "bindings"));
                const string id = "io.github.furroxide.topiaforge.modmanager";
                File.WriteAllText(Path.Combine(bindings.FullName, id + ".gamebindings.json"),
                    new BindingManifest { ModId = id }.ToCanonicalJson());
                var manager = Path.Combine(root, "src", "TopiaForge.ModManager");
                var escaped = location == "src" ? Directory.CreateDirectory(Path.Combine(outside, "TopiaForge.ModManager")).FullName : outside;
                File.WriteAllText(Path.Combine(escaped, "Escaped.cs"), "class Escaped { object Bind() => System.Type.GetType(\"OutsideRepository, GameCode\"); }");
                string linkPath;
                if (location == "manager")
                {
                    Directory.CreateDirectory(Path.Combine(root, "src"));
                    linkPath = manager;
                }
                else if (location == "src") linkPath = Path.Combine(root, "src");
                else
                {
                    Directory.CreateDirectory(manager);
                    linkPath = location == "nested" ? Path.Combine(manager, "Nested") : Path.Combine(root, "Shared");
                    if (location != "nested")
                    {
                        var include = location == "glob" ? "../../Shared/**/*.cs" : "../../Shared/Escaped.cs";
                        File.WriteAllText(Path.Combine(manager, "TopiaForge.ModManager.csproj"),
                            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"" + include + "\" /></ItemGroup></Project>");
                    }
                }
                link = Directory.CreateSymbolicLink(linkPath, outside);
                var rejected = false;
                try { GameReflectionAuditor.Audit(root); }
                catch (InvalidDataException error)
                { rejected = error.Message.Contains("link", StringComparison.OrdinalIgnoreCase); }
                Assert(rejected, "The audit must reject a " + location + " directory link before reading outside source.");
            }
            finally
            {
                // Remove only the owned link before recursive fixture cleanup; never recurse through its target.
                link?.Delete();
                owned.Delete(recursive: true);
            }
        }
    }
}
