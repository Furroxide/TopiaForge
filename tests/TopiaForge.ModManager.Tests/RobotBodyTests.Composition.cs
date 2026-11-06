using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        private static void PersistentBodyComposition(string robotKit)
        {
            var files = new[] { "RobotPersistentBody.cs", "RobotAvatarAppearance.cs", "RobotAvatarAccessorySlots.cs",
                "RobotBodyCompositionDiagnostics.cs" };
            var source = files.Select(file => File.ReadAllText(Path.Combine(robotKit, file)))
                .Concat(new[] { "using TopiaForge.RobotKit;\n" + VisualFixture, CompositionFixture }).ToArray();
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(path).StartsWith("TopiaForge", StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(path), "GameCode.dll", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { typeof(AvatarAppearance).Assembly.Location, typeof(RobotBodyRequest).Assembly.Location })
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            foreach (var hasBuild in new[] { true, false })
            {
                // The real binding asks for GameCode by name. Isolate the fake named surface from the game
                // and give the second run a genuinely missing reflected method.
                var options = new CSharpParseOptions(preprocessorSymbols: hasBuild ? new[] { "HAS_BUILD" } : Array.Empty<string>());
                var compilation = CSharpCompilation.Create("GameCode", source.Select(text => CSharpSyntaxTree.ParseText(text, options)),
                    references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                        nullableContextOptions: NullableContextOptions.Enable));
                using var bytes = new MemoryStream();
                var result = compilation.Emit(bytes);
                if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
                bytes.Position = 0;
                var context = new AssemblyLoadContext("PersistentBodyComposition", isCollectible: true);
                try
                {
                    using var reflection = context.EnterContextualReflection();
                    var probe = context.LoadFromStream(bytes).GetType("PersistentCompositionScenarios", true)!;
                    var run = (Func<bool, int>)probe.GetMethod("Run")!.CreateDelegate(typeof(Func<bool, int>));
                    assertions += run(hasBuild);
                }
                finally { context.Unload(); }
            }
        }

        private const string CompositionFixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.RobotKit;
using UnityEngine;
using UnityObject = UnityEngine.Object;

internal static class PersistentCompositionScenarios
{
    internal static readonly List<string> Steps = new();
    internal static readonly Color Plastic = new(0.8f, 0.1f, 0.2f, 1f);
    internal static readonly Color Metal = new(0.2f, 0.3f, 0.4f, 1f);
    private static int assertions;

    public static int Run(bool hasBuild)
    {
        if (!hasBuild)
        {
            RefusesAndReleasesSource(includeComposer: true, failBuild: false, enteredBuild: false);
            LiveDefaultUsesItsExistingShape();
            return assertions;
        }

        PersistentUsesComposedShape(null, null);
        PersistentUsesComposedShape("Cap", "Cap");
        PersistentUsesComposedShape("", "None");
        RefusesAndReleasesSource(includeComposer: false, failBuild: false, enteredBuild: false);
        RefusesAndReleasesSource(includeComposer: true, failBuild: true, enteredBuild: true);
        LiveDefaultUsesItsExistingShape();
        return assertions;
    }

    private static RobotBodyRequest Request(string? accessory = null) => new(new AvatarAppearance(
        new RgbaColor(Plastic.R, Plastic.G, Plastic.B), new RgbaColor(Metal.R, Metal.G, Metal.B), accessory), new Vec3(1, 2, 3));

    private static void PersistentUsesComposedShape(string? accessory, string? selected)
    {
        var service = Start(includeComposer: true, failBuild: false, out var source);
        var result = service.CreatePersistentBody(Request(accessory), authoritative: true);
        Check(result.Succeeded, "The stock snapshot must succeed with the complete composer binding.");
        var body = (RobotPersistentBody)result.Value!;
        var anchor = (GameObject)body.NativeObject!;
        var sequence = selected == null ? new[] { "dormant", "quieten", "build", "paint:0", "paint:1", "bake" }
            : new[] { "dormant", "quieten", "select:" + selected, "build", "paint:0", "paint:1", "bake" };
        Check(Steps.SequenceEqual(sequence), "Expected one build before painting the new slots and baking them.");
        Check(GameObject.Activated.SequenceEqual(new[] { anchor }) && !source().activeInHierarchy,
            "Only the renderer anchor may become active.");
        Check(UnityObject.Destroyed.Contains(source()), "The inactive native source must be released after the snapshot.");
        var mesh = anchor.GetComponentsInChildren<MeshFilter>(true).Single().sharedMesh!;
        Check(mesh.vertexCount == 11 && service.RetainedVertices == 11, "Only the newly composed geometry contributes to the visual and budget.");
        var renderer = anchor.GetComponentsInChildren<Renderer>(true).Single();
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block, 0);
        Check(block.ColorAt(Shader.PropertyToID("_BaseColor")) == Plastic, "The baked renderer retains the requested plastic.");
        renderer.GetPropertyBlock(block, 1);
        Check(block.ColorAt(Shader.PropertyToID("_BaseColor")) == Metal, "The baked renderer retains the requested metal.");
        Check(anchor.GetComponentsInChildren<RobotBodyComposer>(true).Length == 0, "The snapshot cannot retain a composer component.");
        body.Dispose();
        Check(!body.IsAlive && service.RetainedVertices == 0 && UnityObject.Destroyed.Contains(mesh)
            && UnityObject.Destroyed.Contains(anchor), "Disposal releases the composed visual, owned mesh, and budget.");
    }

    private static void RefusesAndReleasesSource(bool includeComposer, bool failBuild, bool enteredBuild)
    {
        var service = Start(includeComposer, failBuild, out var source);
        var result = service.CreatePersistentBody(Request(), authoritative: true);
        Check(!result.Succeeded && result.ErrorCode == ModErrorCode.Unavailable, "Missing or failed composition must refuse the persistent body.");
        Check(Steps.SequenceEqual(enteredBuild ? new[] { "dormant", "quieten", "build" } : new[] { "dormant", "quieten" }),
            "The refusal occurs before material projection or baking.");
        Check(UnityObject.Destroyed.Contains(source()) && service.RetainedVertices == 0 && GameObject.Activated.Count == 0
            && GameObject.Created.All(value => value.name != "Powered-down robot"), "The refused source is destroyed without a body, activation, or vertex charge.");
    }

    private static void LiveDefaultUsesItsExistingShape()
    {
        Steps.Clear();
        var clone = MakeSource(includeComposer: true, failBuild: false);
        clone.SetActive(false);
        RobotAvatarAppearance.Apply(clone, Request().Appearance);
        Check(Steps.Count == 0 && clone.GetComponentsInChildren<MeshRenderer>(true).Single().enabled,
            "Live avatar default application must leave native composition to its own lifecycle.");
    }

    private static RobotAgentService Start(bool includeComposer, bool failBuild, out Func<GameObject> source)
    {
        Steps.Clear(); GameObject.Activated.Clear(); GameObject.Created.Clear(); UnityObject.Destroyed.Clear();
        GameObject? created = null;
        UnityObject.Clone = _ => created = MakeSource(includeComposer, failBuild);
        source = () => created!;
        return new RobotAgentService();
    }

    private static GameObject MakeSource(bool includeComposer, bool failBuild)
    {
        var root = new GameObject("Inactive snapshot source");
        var template = new GameObject("Uncomposed visual");
        template.transform.SetParent(root.transform, false);
        template.AddComponent<MeshFilter>().sharedMesh = new Mesh { Vertices = 7 };
        template.AddComponent<MeshRenderer>();
        if (includeComposer)
        {
            var composer = root.AddComponent<RobotBodyComposer>();
            composer.Template = template;
            composer.FailBuild = failBuild;
        }
        return root;
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
}

public enum ComposerPartType { Hat }

public sealed class RobotBodyComposer : Component
{
    internal GameObject Template = null!;
    internal bool FailBuild;
    public static GameObject NoneOption { get; } = new("None");
    public static bool IsExteriorMetal(Material material) => material.Metal;
    public IEnumerable<GameObject> IterPartsFor(ComposerPartType slot) => new[] { new GameObject("Cap") };
    public void SelectPartPrefab(ComposerPartType slot, GameObject part) => PersistentCompositionScenarios.Steps.Add("select:" + part.name);

#if HAS_BUILD
    private void BuildBodyFromParts()
    {
        PersistentCompositionScenarios.Check(!gameObject.activeInHierarchy, "Native composition must remain inactive.");
        PersistentCompositionScenarios.Steps.Add("build");
        if (FailBuild) throw new InvalidOperationException("Controlled native composition failure.");
        Template.SetActive(false);
        var part = new GameObject("Composed stock geometry");
        part.transform.SetParent(transform, false);
        var skin = part.AddComponent<SkinnedMeshRenderer>();
        skin.sharedMesh = new Mesh { Vertices = 11 };
        skin.sharedMaterials = new[] { new Material(), new Material { Metal = true } };
        skin.OnSetBlock = (_, slot) => PersistentCompositionScenarios.Steps.Add("paint:" + slot);
        skin.BeforeBake = () => PersistentCompositionScenarios.Steps.Add("bake");
    }
#endif
}

namespace TopiaForge.Mods.Internal
{
    internal interface IInternalNativeObjectEntity { object? NativeObject { get; } }
}

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        private readonly GameObject prefab = new("Stock template");
        private readonly IModLogger? logger = null;
        private readonly bool disposed = false;
        private GameObject? root, incubator;
        internal int RetainedVertices => bodyVertices;
        private GameObject ResolveCachedPrefab() => prefab;
        private void EnsureRoots()
        {
            root ??= new GameObject("Root");
            if (incubator != null) return;
            incubator = new GameObject("Incubator");
            incubator.transform.SetParent(root.transform, false);
            incubator.SetActive(false);
        }
        private static void Quieten(GameObject clone) => PersistentCompositionScenarios.Steps.Add("quieten");
    }

    internal static class GameReflection
    {
        internal static void ConfigureBrain(GameObject clone, RobotBrainMode mode, IModLogger? logger)
        {
            PersistentCompositionScenarios.Check(mode == RobotBrainMode.Dormant && !clone.activeInHierarchy,
                "The source must be dormant before any composition.");
            PersistentCompositionScenarios.Steps.Add("dormant");
        }
    }

    internal sealed class RobotAgentEntityIdentityAnchor : Component
    {
        internal void Initialize(string id) { }
    }
}
""";
    }
}
