using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredNativeBindingTests
    {
        internal static void Run(string source)
        {
            // Compile the complete production adapter. Only engine/helper definitions and type
            // resolution are substituted; constructor binding and per-record conversion are real.
            var tree = CSharpSyntaxTree.ParseText(source).GetRoot();
            var calls = tree.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            var lookups = calls.Where(c => c.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == "GetField").ToArray();
            Assert(lookups.Length == 1 && lookups[0].Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.Text == "Field",
                "field metadata lookup is confined to the binding helper");
            var bindings = calls.Where(c => c.Expression is IdentifierNameSyntax name && name.Identifier.Text == "Field").ToArray();
            Assert(bindings.Length == 7 && bindings.All(c => c.Ancestors().OfType<ConstructorDeclarationSyntax>().Any()),
                "all field bindings occur in construction, never during record conversion");
            var resolutions = calls.Count(c => c.Expression.ToString() == "Type.GetType");
            Assert(resolutions == 3, "fixture replaces exactly the three game type resolutions");
            var production = source.Replace("Type.GetType(", "CoordinateTypes.Resolve(", StringComparison.Ordinal);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Worlds", new[]
                { CSharpSyntaxTree.ParseText(production), CSharpSyntaxTree.ParseText(Fixture + InvalidTypes()) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Assert(result.Success, "complete native adapter compiles: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("CoordinateBindingProbe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Authored native bindings passed (compiled adapter, cached field conversion and constructor refusals).");
        }

        private static string InvalidTypes()
        {
            var types = new StringBuilder();
            var overloads = new StringBuilder("public static partial class UgcRotationHelper {\n");
            foreach (var family in new[] { "Vector", "Rotation" })
            foreach (var field in family == "Vector" ? new[] { "x", "y", "z" } : new[] { "x", "y", "z", "w" })
            foreach (var missing in new[] { true, false })
            {
                var name = "Bad" + family + field + (missing ? "Missing" : "Wrong");
                types.Append("public sealed class ").Append(name).Append(" {\n");
                foreach (var component in family == "Vector" ? new[] { "x", "y", "z" } : new[] { "x", "y", "z", "w" })
                {
                    if (component == field && missing) continue;
                    var type = component == field ? "double" : component == "w" ? "float?" : "float";
                    types.Append("public ").Append(type).Append(' ').Append(component).Append(";\n");
                }
                if (family == "Vector") types.Append("public UnityEngine.Vector3 ToUnityPosition() => new UnityEngine.Vector3(0,0,0);\n");
                else overloads.Append("public static UnityEngine.Quaternion ToUnityQuaternion(").Append(name)
                    .Append(" value) => new UnityEngine.Quaternion(0,0,0,1);\n");
                types.Append("}\n");
            }
            // Methods are valid for every malformed type, so a constructor that skips field
            // validation cannot accidentally pass these refusals by failing method resolution.
            return types.Append(overloads).Append("}\n").ToString();
        }

        private const string Fixture = """
using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds.Maps;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 right => new Vector3(1,0,0);
        public static Vector3 up => new Vector3(0,1,0);
        public static Vector3 forward => new Vector3(0,0,1);
    }
    public struct Quaternion
    {
        public float x,y,z,w;
        public Quaternion(float x, float y, float z, float w) { this.x=x; this.y=y; this.z=z; this.w=w; }
        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            var q = new Quat(a.x,a.y,a.z,a.w) * new Quat(b.x,b.y,b.z,b.w);
            return new Quaternion(q.X,q.Y,q.Z,q.W);
        }
    }
    public sealed class Transform
    {
        public Vector3 position => new Vector3(0,0,0);
        public Quaternion rotation => new Quaternion(0,0,0,1);
        public Vector3 lossyScale => new Vector3(1,1,1);
        public Vector3 TransformVector(Vector3 value) => value;
        public Vector3 TransformPoint(Vector3 value) => value;
    }
}
namespace TopiaForge.Worlds.Maps
{
    internal sealed class MapProblem : Exception
    {
        internal MapProblem(ModErrorCode code, string message) : base(message) { Code=code; }
        internal ModErrorCode Code { get; }
    }
}
public sealed class UgcVector3Value
{
    public float x,y,z;
    public Vector3 ToUnityPosition() => new Vector3(-x,y,z);
}
public sealed class UgcRotationValue { public float x,y,z; public float? w; }
public static partial class UgcRotationHelper
{
    public static int EulerCalls, QuaternionCalls;
    public static bool Fail;
    public static Quaternion ToUnityQuaternion(UgcRotationValue value)
    {
        if (Fail) throw new InvalidOperationException("Unexpected helper failure");
        Quat rotation;
        if (value.w.HasValue)
        {
            QuaternionCalls++;
            var length = (double)value.x*value.x + (double)value.y*value.y + (double)value.z*value.z + (double)value.w.Value*value.w.Value;
            if (Math.Abs(length-1) > 0.00001) throw new InvalidOperationException("Quaternion must be normalized before conversion");
            rotation = new Quat(value.x,value.y,value.z,value.w.Value);
        }
        else
        {
            EulerCalls++;
            double x=value.x*Math.PI/360, y=value.y*Math.PI/360, z=value.z*Math.PI/360;
            rotation = new Quat(0,(float)Math.Sin(y),0,(float)Math.Cos(y)) *
                new Quat((float)Math.Sin(x),0,0,(float)Math.Cos(x)) * new Quat(0,0,(float)Math.Sin(z),(float)Math.Cos(z));
        }
        return new Quaternion(rotation.X,-rotation.Y,-rotation.Z,rotation.W);
    }
}
internal static class CoordinateTypes
{
    internal static Type Vector=typeof(UgcVector3Value), Rotation=typeof(UgcRotationValue);
    internal static Type Resolve(string name, bool throwOnError) => name switch
    {
        "UgcVector3Value, GameCode" => Vector,
        "UgcRotationValue, GameCode" => Rotation,
        "UgcRotationHelper, GameCode" => typeof(UgcRotationHelper),
        _ => throw new InvalidOperationException("Unexpected type resolution")
    };
}
public static class CoordinateBindingProbe
{
    public static void Run()
    {
        var parent = new MapTransform(new Vec3(2,3,5),new Vec3(10,20,30),new Vec3(2,3,4));
        var child = new MapTransform(new Vec3(7,11,13),new Quat(1e20f,2e20f,3e20f,4e20f),new Vec3(5,6,7));
        using var fake = new FakeAuthoredMap()
            .AddEntity("parent","Parent",editorTransform:parent)
            .AddEntity("child","Child",editorTransform:child,parentId:"parent",hasMarker:false);
        var records = fake.Entities.Select(e => new AuthoredRecord(e.Id,e.SceneId,e.Name,e.ParentId,e.ModelId,e.PrefabUri,e.EditorTransform,e.Components)).ToArray();
        var scene = new AuthoredScene("scene","Scene","",records);
        var adapter = new MapNativeCoordinates(new Transform());
        for (var iteration=0; iteration<2; iteration++)
        {
            var actual = adapter.AuthoredTransforms(scene);
            foreach (var record in records)
            {
                var expected = fake.Entity(record.Id).WorldTransform;
                Check(actual[record.Id].TryGetPose(out var pose),"converted record is representable");
                Near(pose.Position,expected.Position,"cached position fields and parent composition");
                Near(pose.Scale,expected.Scale,"authored scale preserved");
                Near(pose.Rotation*new Vec3(1,0,0),expected.Rotation*new Vec3(1,0,0),"cached quaternion fields");
                Near(pose.Rotation*new Vec3(0,1,0),expected.Rotation*new Vec3(0,1,0),"cached quaternion orientation");
            }
        }
        Check(UgcRotationHelper.EulerCalls==2 && UgcRotationHelper.QuaternionCalls==2,"Euler null w and quaternion w survive repeated conversion");
        Near(adapter.Position(new Vec3(17,19,23)),new Vec3(-17,19,23),"standalone position uses cached vector fields");
        Near(adapter.Transform(child).Position,fake.ToWorld(child).Position,"legacy transform shares cached conversion");
        UgcRotationHelper.Fail=true;
        try { adapter.AuthoredTransforms(scene); throw new Exception("Helper failure was hidden"); }
        catch (TargetInvocationException error) { Check(error.InnerException is InvalidOperationException,"unexpected helper failure propagates"); }
        finally { UgcRotationHelper.Fail=false; }
        var refused=0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t=>t.Name.StartsWith("BadVector") || t.Name.StartsWith("BadRotation")))
        {
            CoordinateTypes.Vector=type.Name.StartsWith("BadVector") ? type : typeof(UgcVector3Value);
            CoordinateTypes.Rotation=type.Name.StartsWith("BadRotation") ? type : typeof(UgcRotationValue);
            try { _=new MapNativeCoordinates(new Transform()); throw new Exception("Malformed metadata accepted: "+type.Name); }
            catch (MapProblem error)
            {
                Check(error.Code==ModErrorCode.Unavailable && error.Message=="This game build cannot convert authored map coordinates yet. Update TopiaForge.","constructor fails closed with the existing refusal");
                refused++;
            }
        }
        Check(refused==14,"every vector and rotation field rejects missing and wrong types during construction");
    }
    private static void Near(Vec3 actual, Vec3 expected, string reason) => Check((actual-expected).Length<0.0001f,reason);
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
""";
    }
}
