using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        private static void VisualSnapshotMeshHandling()
        {
            var robotKit = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            var visual = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(robotKit, "RobotPersistentBody.cs"))).GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(node => node.Identifier.Text == "RobotBodyVisual");
            var stages = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(robotKit, "RobotBodyCompositionDiagnostics.cs"))).GetRoot()
                .DescendantNodes().OfType<EnumDeclarationSyntax>().Single(node => node.Identifier.Text == "RobotBodyCompositionStage");
            var source = "using System; using System.Collections.Generic; using UnityEngine;\n" + visual.ToFullString() + stages.ToFullString();
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(path).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("RobotBodyVisualProbe",
                new[] { source, VisualFixture }.Select(text => CSharpSyntaxTree.ParseText(text)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("VisualSnapshotScenarios", true)!;
            var run = (Func<int>)probe.GetMethod("Run")!.CreateDelegate(typeof(Func<int>));
            assertions += run();
            PersistentBodyComposition(robotKit);
        }

        private const string VisualFixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

internal static class VisualSnapshotScenarios
{
    private static int assertions;

    public static int Run()
    {
        var empty = InactiveRoot();
        AddPlaceholders(empty);
        Refuses(empty, RobotBodyCompositionStage.NoVisibleMeshes);

        var clone = InactiveRoot();
        AddPlaceholders(clone);
        var staticMesh = new Mesh { Vertices = 7 };
        var staticRenderer = StaticRenderer(clone, staticMesh);
        var skinnedRenderer = Skin(clone, new Mesh { Vertices = 11 });
        Child(clone).AddComponent<Renderer>().enabled = false;
        var hidden = Child(clone);
        hidden.SetActive(false);
        hidden.AddComponent<Renderer>();

        var anchor = InactiveRoot();
        var owned = new List<Mesh>();
        var stage = default(RobotBodyCompositionStage);
        var vertices = RobotBodyVisual.Copy(clone, anchor, owned, 18, ref stage);
        var renderers = anchor.GetComponentsInChildren<Renderer>(true);
        var filters = anchor.GetComponentsInChildren<MeshFilter>(true);
        Check(vertices == 18 && renderers.Length == 2 && filters.Length == 2 && owned.Count == 1,
            "Only the valid static and skinned geometry contributes to the snapshot and budget.");
        Check(ReferenceEquals(filters[0].sharedMesh, staticMesh) && ReferenceEquals(filters[1].sharedMesh, owned[0])
            && owned[0].vertexCount == 11 && skinnedRenderer.Bakes == 1,
            "Static geometry is shared and the skinned geometry is baked into exactly one owned mesh.");
        Check(ReferenceEquals(renderers[0].sharedMaterials, staticRenderer.sharedMaterials) && !clone.activeSelf && !anchor.activeSelf,
            "The visual copy preserves the valid renderer materials without activating either root.");

        var faultyAccess = new InvalidOperationException("Mesh accessor failed.");
        var unreadable = InactiveRoot();
        StaticRenderer(unreadable, new Mesh { Vertices = 1, VertexFailure = faultyAccess });
        Refuses(unreadable, RobotBodyCompositionStage.Mesh, fault: faultyAccess);

        var faultyBake = new InvalidOperationException("Bake failed.");
        var unbakeable = InactiveRoot();
        var skin = Skin(unbakeable, new Mesh { Vertices = 11 });
        skin.BakeFailure = faultyBake;
        Refuses(unbakeable, RobotBodyCompositionStage.Bake, fault: faultyBake);
        skin.BakeFailure = null;
        skin.BakedVertices = 0;
        Refuses(unbakeable, RobotBodyCompositionStage.Bake);
        var previousBakes = skin.Bakes;
        Refuses(unbakeable, RobotBodyCompositionStage.VertexBudget, budget: 10);
        Check(skin.Bakes == previousBakes, "An oversized mesh is refused before baking.");
        Refuses(clone, RobotBodyCompositionStage.VertexBudget, budget: 17);

        var badMaterials = InactiveRoot();
        var materialRenderer = StaticRenderer(badMaterials, new Mesh { Vertices = 1 });
        materialRenderer.sharedMaterials = new Material[] { null! };
        Refuses(badMaterials, RobotBodyCompositionStage.Materials);
        materialRenderer.sharedMaterials = new Material[33];
        Refuses(badMaterials, RobotBodyCompositionStage.Materials);

        var unsupported = InactiveRoot();
        Child(unsupported).AddComponent<Renderer>();
        Refuses(unsupported, RobotBodyCompositionStage.Renderers);
        Refuses(InactiveRoot(), RobotBodyCompositionStage.Renderers);
        var oversized = InactiveRoot();
        for (var index = 0; index < 257; index++) Child(oversized).AddComponent<MeshRenderer>();
        Refuses(oversized, RobotBodyCompositionStage.Renderers);
        var deep = InactiveRoot();
        var node = deep;
        for (var index = 0; index < 65; index++) node = Child(node);
        Refuses(deep, RobotBodyCompositionStage.Hierarchy);
        return assertions;
    }

    private static void AddPlaceholders(GameObject clone)
    {
        var unexpected = new InvalidOperationException("A meshless renderer was processed.");
        Child(clone).AddComponent<MeshRenderer>().MaterialFailure = unexpected;
        StaticRenderer(clone, null).MaterialFailure = unexpected;
        var skin = Skin(clone, null);
        skin.BakeFailure = unexpected;
        skin.MaterialFailure = unexpected;
    }

    private static void Refuses(GameObject clone, RobotBodyCompositionStage expected, int budget = 100, Exception? fault = null)
    {
        var anchor = InactiveRoot();
        var stage = default(RobotBodyCompositionStage);
        try { RobotBodyVisual.Copy(clone, anchor, new List<Mesh>(), budget, ref stage); }
        catch (InvalidOperationException exception)
        {
            Check(stage == expected && (fault == null || ReferenceEquals(exception, fault)) && !anchor.activeSelf,
                "Expected refusal at " + expected + ", received " + stage + ".");
            return;
        }
        throw new InvalidOperationException("Expected visual snapshot refusal at " + expected + ".");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static GameObject InactiveRoot()
    {
        var root = new GameObject("Inactive fixture root");
        root.SetActive(false);
        return root;
    }

    private static GameObject Child(GameObject parent)
    {
        var child = new GameObject("Fixture child");
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    private static MeshRenderer StaticRenderer(GameObject root, Mesh? mesh)
    {
        var child = Child(root);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        return child.AddComponent<MeshRenderer>();
    }

    private static SkinnedMeshRenderer Skin(GameObject root, Mesh? mesh)
    {
        var renderer = Child(root).AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }
}

namespace UnityEngine
{
    public readonly struct Vector3 { public Vector3(float x, float y, float z) { } }
    public readonly struct Quaternion
    {
        public Quaternion(float x, float y, float z, float w) { }
        public static Quaternion Euler(float x, float y, float z) => default;
    }
    public readonly record struct Color(float R, float G, float B, float A)
    {
        public static Color operator *(Color value, float scale) => new(value.R * scale, value.G * scale, value.B * scale, value.A * scale);
    }
    public sealed class Material { internal bool Metal; }
    public static class Shader { public static int PropertyToID(string name) => StringComparer.Ordinal.GetHashCode(name); }
    public sealed class MaterialPropertyBlock
    {
        private readonly Dictionary<int, Color> colors = new();
        public void Clear() => colors.Clear();
        public void SetColor(int id, Color color) => colors[id] = color;
        internal Color ColorAt(int id) => colors[id];
        internal void CopyFrom(MaterialPropertyBlock source)
        { Clear(); foreach (var pair in source.colors) colors.Add(pair.Key, pair.Value); }
    }

    public class Object
    {
        internal static Func<GameObject, GameObject>? Clone;
        internal static readonly List<Object> Destroyed = new();
        public static GameObject Instantiate(GameObject prefab, Transform parent, bool worldPositionStays)
        {
            var copy = Clone!(prefab);
            copy.transform.SetParent(parent, worldPositionStays);
            return copy;
        }
        public static void Destroy(Object value) => Destroyed.Add(value);
    }

    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new List<Component>();
        internal static readonly List<GameObject> Activated = new();
        internal static readonly List<GameObject> Created = new();
        public string name { get; }
        public Transform transform { get; }
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
        public GameObject(string name) { this.name = name; transform = new Transform(this); Created.Add(this); }
        public void SetActive(bool active)
        { activeSelf = active; if (active) Activated.Add(this); }
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this };
            components.Add(component);
            return component;
        }
        public T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
        public Component? GetComponentInChildren(Type type, bool includeInactive) =>
            GetComponentsInChildren<Component>(includeInactive).FirstOrDefault(type.IsInstanceOfType);
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => components.OfType<T>()
            .Concat(transform.Children.SelectMany(child => child.gameObject.GetComponentsInChildren<T>(includeInactive))).ToArray();
    }

    public sealed class Transform
    {
        internal readonly List<Transform> Children = new List<Transform>();
        public GameObject gameObject { get; }
        public Transform parent { get; private set; } = null!;
        public Vector3 localPosition { get; set; }
        public Quaternion localRotation { get; set; }
        public Vector3 localScale { get; set; }
        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; }
        public int childCount => Children.Count;
        public Transform(GameObject owner) { gameObject = owner; }
        public Transform GetChild(int index) => Children[index];
        public void SetParent(Transform value, bool worldPositionStays)
        {
            parent = value;
            value.Children.Add(this);
        }
    }

    public abstract class Component
    {
        public GameObject gameObject { get; set; } = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }

    public sealed class Mesh : Object
    {
        internal Exception? VertexFailure;
        internal int Vertices;
        public int vertexCount => VertexFailure == null ? Vertices : throw VertexFailure;
    }

    public sealed class MeshFilter : Component { public Mesh? sharedMesh { get; set; } }

    public class Renderer : Component
    {
        private Material[] materials = new[] { new Material() };
        private readonly Dictionary<int, MaterialPropertyBlock> blocks = new();
        internal Action<MaterialPropertyBlock, int>? OnSetBlock;
        internal Exception? MaterialFailure;
        public Material[] sharedMaterials
        {
            get => MaterialFailure == null ? materials : throw MaterialFailure;
            set => materials = value;
        }
        public bool enabled { get; set; } = true;
        public int shadowCastingMode { get; set; }
        public bool receiveShadows { get; set; }
        public void GetPropertyBlock(MaterialPropertyBlock block, int index = -1)
        { if (blocks.TryGetValue(index, out var current)) block.CopyFrom(current); else block.Clear(); }
        public void SetPropertyBlock(MaterialPropertyBlock block, int index = -1)
        {
            OnSetBlock?.Invoke(block, index);
            if (!blocks.TryGetValue(index, out var current)) blocks.Add(index, current = new MaterialPropertyBlock());
            current.CopyFrom(block);
        }
    }

    public sealed class MeshRenderer : Renderer { }

    public sealed class SkinnedMeshRenderer : Renderer
    {
        internal Exception? BakeFailure;
        internal int BakedVertices = 11;
        internal int Bakes;
        internal Action? BeforeBake;
        public Mesh? sharedMesh { get; set; }
        public void BakeMesh(Mesh mesh)
        {
            Bakes++;
            BeforeBake?.Invoke();
            if (BakeFailure != null) throw BakeFailure;
            mesh.Vertices = BakedVertices;
        }
    }
}
""";
    }
}
