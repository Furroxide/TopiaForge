using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredNativeCoordinatesTests
    {
        internal static void Run(string repo)
        {
            // Compile the production root/chain boundary, substituting only engine values and
            // the game's local coordinate helper. Native helper semantics remain a game-run gate.
            var source = File.ReadAllText(Path.Combine(repo, "mods", "TopiaForge.Worlds", "Maps", "MapNativeCoordinates.cs"));
            AuthoredNativeBindingTests.Run(source);
            var tree = CSharpSyntaxTree.ParseText(source).GetRoot();
            var method = tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "AuthoredTransforms");
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Worlds", new[] { CSharpSyntaxTree.ParseText(Engine.Replace("/* production */", method.ToFullString())) },
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream(); var result = compilation.Emit(bytes);
            Assert(result.Success, "native authored adapter compiles: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("CoordinateProbe", true)!;
            var read = (Func<AuthoredScene, Func<MapTransform, AuthoredAffine>, AuthoredAffine, IReadOnlyDictionary<string, AuthoredAffine>>)
                probe.GetMethod("Run")!.CreateDelegate(typeof(Func<AuthoredScene, Func<MapTransform, AuthoredAffine>, AuthoredAffine, IReadOnlyDictionary<string, AuthoredAffine>>));
            using var fake = new FakeAuthoredMap()
                .AddEntity("parent", "Parent", editorTransform: new MapTransform(new Vec3(10, 0, 0), new Vec3(0, 0, 90), new Vec3(2, 3, 4)))
                .AddEntity("child", "Child", editorTransform: new MapTransform(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "parent", hasMarker: false);
            var records = fake.Entities.Select(e => new AuthoredRecord(e.Id, e.SceneId, e.Name, e.ParentId, e.ModelId, e.PrefabUri, e.EditorTransform, e.Components)).ToArray();
            var scene = new AuthoredScene("scene", "Scene", "", records);
            Func<MapTransform, AuthoredAffine> local = value => AuthoredAffine.From(fake.ToWorld(value));
            var poses = read(scene, local, AuthoredAffine.Identity);
            Near(poses["child"].Position, new Vec3(-4, 2, 12), "production adapter composes parents without live markers");
            Near(poses["child"].Position, fake.Entity("child").WorldTransform.Position, "production/fake parity");
            var root = new AuthoredAffine(new Vec3(20, 30, 40), new Vec3(0, 2, 0), new Vec3(3, 1, 0), new Vec3(0, 0, 4), Quat.Identity);
            poses = read(scene, local, root);
            Near(poses["child"].Position, new Vec3(26, 24, 88), "production adapter captures complete import-root basis");
            var half = (float)Math.Sqrt(0.5);
            root = AuthoredAffine.From(new TransformState(new Vec3(5, 6, 7), new Quat(0, half, 0, half), new Vec3(2, 2, 2)));
            poses = read(scene, local, root);
            Near(poses["child"].Position, new Vec3(29, 10, 15), "production rotated/scaled import root");
            Near(poses["child"].Rotation * new Vec3(0, 0, 1), new Vec3(1, 0, 0), "production import-root quaternion");
            Console.WriteLine("Authored native coordinate boundary passed (compiled production method, fake parity and affine import root).");
        }
        private static void Near(Vec3 actual, Vec3 expected, string reason) => Assert((actual - expected).Length < 0.0001f, reason);
        private const string Engine = """
using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
internal sealed class Vector3 {
    internal float x, y, z;
    internal Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
    internal static Vector3 right => new Vector3(1,0,0);
    internal static Vector3 up => new Vector3(0,1,0);
    internal static Vector3 forward => new Vector3(0,0,1);
}
internal sealed class Rotation {
    internal float x,y,z,w;
    internal Rotation(Quat q) { x=q.X; y=q.Y; z=q.Z; w=q.W; }
}
internal sealed class Root {
    internal AuthoredAffine basis;
    internal Vector3 position => Make(basis.Position);
    internal Rotation rotation => new Rotation(basis.Rotation);
    internal Vector3 TransformVector(Vector3 value) => Make(basis.Vector(new Vec3(value.x,value.y,value.z)));
    internal static Vector3 Make(Vec3 v) => new Vector3(v.X,v.Y,v.Z);
}
internal sealed class CoordinateProbe {
    private Root root;
    private Func<MapTransform,AuthoredAffine> local;
    private AuthoredAffine LocalTransform(MapTransform value) => local(value);
    private static Vec3 Value(Vector3 v) => new Vec3(v.x,v.y,v.z);
    private static Exception Unavailable() => new InvalidOperationException();
    public static IReadOnlyDictionary<string,AuthoredAffine> Run(AuthoredScene scene, Func<MapTransform,AuthoredAffine> local, AuthoredAffine root) =>
        new CoordinateProbe { root = new Root { basis = root }, local = local }.AuthoredTransforms(scene);
    /* production */
}
""";
    }
}
