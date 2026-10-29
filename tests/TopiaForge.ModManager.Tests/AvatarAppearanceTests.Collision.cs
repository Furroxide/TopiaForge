using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        // Compile production method bodies verbatim, as the UI contract probes do. The doubles
        // store engine properties only; none implements crouch, shape, or animation decisions.
        private static void PoweredDownCollision()
        {
            var folder = Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/Multiplayer/Presence");
            var unity = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(folder, "Unity/UnityPresenceAvatar.cs"))).GetRoot();
            var remote = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(folder, "RemotePlayerAvatar.cs"))).GetRoot();
            string Method(SyntaxNode source, string name) => source.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == name).ToString();
            var constants = string.Join("\n", unity.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText is "Height" or "Radius" or "CrouchedHeight")));
            var source = Contracts + "\nclass PhysicalAvatar { " + PhysicalFields + constants
                + Method(unity, "Animate") + Method(unity, "Present") + Method(unity, "Shape")
                + " }\nclass RemoteAvatar { " + RemoteFields + Method(remote, "Draw") + " }\n" + CollisionProbe;
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("AppearanceCollisionProbe", new[] { CSharpSyntaxTree.ParseText(source) },
                trusted.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Check(result.Success, "production collision methods compile: " + string.Join("; ", result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            try { Assembly.Load(bytes.ToArray()).GetType("CollisionProbe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            Console.WriteLine("PoweredDownCollision passed: provider body and capsule fallback; standing 1.8/0.9, crouching 1.2/0.6; pose restoration and crouch transitions while down.");
        }

        private const string PhysicalFields = """
            public readonly CapsuleCollider collider = new();
            public readonly Transform capsuleTransform = new();
            private readonly object root = new();
            private readonly GameObject capsule = new();
            private bool disposed, crouching, poweredDown;
            private const int BaseColor = 1, Color = 2;
            public void Place(NetVector3 feet, float yaw) { }
            """;

        private const string RemoteFields = """
            public readonly PhysicalAvatar avatar = new();
            public Body body;
            public readonly Presentation presentation = new();
            private bool disposed, hasDrawn;
            private NetVector3 drawn;
            private const float SpeedSmoothing = 0.3f;
            public float Speed { get; private set; }
            public void Pose(bool down) { presentation.PoweredDown = down; avatar.Present(new AvatarAppearance(), down); }
            """;

        private const string CollisionProbe = """
            public static class CollisionProbe
            {
                public static void Run()
                {
                    foreach (var provider in new[] { false, true })
                    {
                        var remote = new RemoteAvatar { body = provider ? new Body() : null };
                        var tick = 0;
                        void Frame(bool crouched, string stage)
                        {
                            remote.Draw(new NetVector3(++tick, 0, 0), 0, crouched, 0.1f);
                            Geometry(crouched, stage);
                            if (provider)
                            {
                                var body = remote.body;
                                Check(body.Crouched == (!remote.presentation.PoweredDown && crouched), stage + " cosmetic crouch");
                                Check(remote.presentation.PoweredDown ? body.Speed == 0 : body.Speed == remote.Speed, stage + " cosmetic speed");
                            }
                        }
                        void Geometry(bool crouched, string stage)
                        {
                            var collider = remote.avatar.collider;
                            Check(Near(collider.height, crouched ? 1.2f : 1.8f), stage + " collider height: " + collider.height);
                            Check(Near(collider.center.y, crouched ? 0.6f : 0.9f), stage + " collider center: " + collider.center.y);
                            Check(collider.center.x == 0 && collider.center.z == 0, stage + " centered capsule");
                        }
                        foreach (var initiallyCrouched in new[] { false, true })
                        {
                            remote.Pose(false);
                            Frame(initiallyCrouched, "before down");
                            remote.Pose(true);
                            Geometry(initiallyCrouched, "immediately down");
                            Frame(initiallyCrouched, "during down");
                            Frame(!initiallyCrouched, "crouch transition while down");
                            Frame(initiallyCrouched, "reverse crouch transition while down");
                            remote.Pose(false);
                            Geometry(initiallyCrouched, "immediately restored");
                            Frame(initiallyCrouched, "after down");
                            Frame(!initiallyCrouched, "crouch transition after down");
                        }
                    }
                }
                private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.00001f;
                private static void Check(bool condition, string message)
                { if (!condition) throw new InvalidOperationException("Powered-down collision: " + message); }
            }
            """;

        private const string Contracts = """
            using System;
            using UnityEngine;
            public struct NetVector3
            {
                public float X, Y, Z;
                public NetVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
            }
            public class Presentation { public bool PoweredDown; }
            public class Body
            {
                public float Speed;
                public bool Crouched;
                public void Animate(float speed, bool crouched) { Speed = speed; Crouched = crouched; }
            }
            public class AvatarAppearance { public Tint Plastic = new(); }
            public class Tint { public float Red, Green, Blue; }
            namespace UnityEngine
            {
                public struct Vector3
                {
                    public float x, y, z;
                    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
                }
                public struct Quaternion
                {
                    public static Quaternion identity => new();
                    public static Quaternion Euler(float x, float y, float z) => new();
                }
                public struct Color { public Color(float r, float g, float b, float a) { } }
                public class Transform { public Vector3 localPosition, localScale; public Quaternion localRotation; }
                public class CapsuleCollider { public float height; public Vector3 center; }
                public class MaterialPropertyBlock { public void SetColor(int id, Color color) { } }
                public class Renderer
                {
                    public void GetPropertyBlock(MaterialPropertyBlock block) { }
                    public void SetPropertyBlock(MaterialPropertyBlock block) { }
                }
                public class GameObject { public T GetComponent<T>() where T : new() => new(); }
            }
            """;
    }
}
