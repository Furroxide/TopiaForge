using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotComposedCleanupTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            SyntaxTree Read(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file);
            var service = Read("RobotAgentService.cs");
            var sceneResources = Read("RobotAgentService.SceneResources.cs");
            var agent = Read("RobotAgent.cs");

            // Keep the spawn, prune and cleanup paths intact. Replace only their surrounding native dependencies.
            var source = Probe
                .Replace("/* service methods */", Methods(service, "Spawn", "SpawnNative", "Tick")
                    + Methods(sceneResources, "EnsureRoots", "EnsureKinematicRoot", "NextId", "LogSpawnModeOnce", "ClearAgents")
                    + Property(service, "ActiveAgents"))
                .Replace("/* agent lifetime */", Methods(agent, "Despawn", "SetBrainMode", "SetInteraction", "RestrictToBody", "Unavailable")
                    + Property(agent, "IsAlive"));
            var candidate = Read("RobotPrefabResolver.cs").GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(c => c.Identifier.Text == "RobotPrefabCandidate");
            var candidateSource = "using System; using UnityEngine; namespace TopiaForge.RobotKit { " + candidate + " }";
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(source, path: "ComposedCleanupProbe.cs"),
                CSharpSyntaxTree.ParseText(NativeBoundary, path: "ComposedCleanupNativeBoundary.cs"),
                CSharpSyntaxTree.ParseText(candidateSource, path: "RobotPrefabCandidate.cs"),
                Read("RobotPrefabSelection.cs"),
                Read("RobotComposedLifetime.cs"),
                Read("RobotComposedReferences.cs"),
            };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ComposedCleanupProbe", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.RobotAgentService", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Composed robot cleanup: activation, corpse sweep, both spawn rollback paths, the unverified context refusal and body-only spawns passed.");
        }

        private static string Methods(SyntaxTree tree, params string[] names) => string.Join("\n", names.Select(name =>
            tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name).ToFullString())) + "\n";

        private static string Property(SyntaxTree tree, string name) => tree.GetRoot().DescendantNodes()
            .OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.Text == name).ToFullString();

        private const string Probe = """
using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotAgentService
    {
        private readonly Logger logger = new();
        private readonly List<RobotAgent> agents = new();
        private readonly Dictionary<string, RobotAgent> agentsById = new(StringComparer.Ordinal);
        private readonly List<Search> searches = new();
        private GameObject? root, incubator;
        private IRobotAgent[]? activeSnapshot;
        private bool activeDirty = true;
        private bool disposed, loggedSpawnMode;
        private int spawnCounter;
        private Replication? Replication => null;
        private bool IsNavigationAvailable => true;

        private RobotPrefabCandidate[] catalog;
        private readonly object? world = null;
        private readonly NativeAssetOwner mapOwner = new GameObject("Map").AddComponent<NativeAssetOwner>();
        private readonly NativeAssetOwner siblingOwner = new GameObject("Unrelated scene object").AddComponent<NativeAssetOwner>();
        private Build? lastBuild;
        private bool unverifiedContext;
        private static readonly RobotAgentSpawnRequest Request = new();

        private RobotAgentService() => catalog = new[]
        {
            new RobotPrefabCandidate("composed", "Composed", Compose, () => true),
            new RobotPrefabCandidate("municipal", "MuniciPal", Compose, () => true, bodyOnly: true)
        };
        private IReadOnlyList<RobotPrefabCandidate> ResolveCachedCatalog() => catalog;
        private static void Quieten(GameObject actor, bool cosmetic) => throw new InvalidOperationException("Unexpected replica.");
        private static object? ResolveAnimator(GameObject actor) => null;
        private static string? ResolveWalkParameter() => null;

        /* service methods */

        private sealed record Build(GameObject Actor, RobotComposedLifetime Lease, NativeAssetOwner Owner);

        private GameObject Compose(Transform parent)
        {
            if (unverifiedContext) throw new RobotComposedContextUnverifiedException();
            siblingOwner.transform.SetParent(root!.transform, false);
            var builder = new GameObject("Builder");
            builder.transform.SetParent(parent, false);
            builder.SetActive(false);
            var owner = builder.AddComponent<NativeAssetOwner>();
            var actor = new GameObject("Actor");
            actor.transform.SetParent(builder.transform, false);
            var lease = builder.AddComponent<RobotComposedLifetime>();
            lease.Actor = actor;
            lease.Initialize(typeof(NativeAssetOwner), typeof(NativeAssetOwner).GetMethod(nameof(NativeAssetOwner.DestroyAssets))!);
            lastBuild = new Build(actor, lease, owner);
            Check(ReferenceEquals(builder.transform.parent, incubator!.transform) && !incubator.activeSelf && !actor.activeInHierarchy,
                "construction must start under the inactive incubator");
            return actor;
        }

        public static void Run()
        {
            Activation();
            CorpseSweep();
            Rollback(afterPublication: false);
            Rollback(afterPublication: true);
            UnverifiedContext();
            BodyOnly();
            MissingReason();
        }

        private static void BodyOnly()
        {
            var service = new RobotAgentService();
            var refused = service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "municipal", BrainMode = RobotBrainMode.Autonomous });
            Check(refused.ErrorCode == ModErrorCode.Unavailable && refused.Message == RobotComposedReferences.BodyOnly + " Request Dormant instead."
                && service.lastBuild == null, "an Autonomous body-only spawn is refused with its reason before construction");
            service.CheckEmpty();
            var full = service.Spawn(new RobotAgentSpawnRequest { BrainMode = RobotBrainMode.Autonomous });
            Check(full.ErrorCode == null && ((RobotAgent)full.Value!).Interaction.NativeTalkMode == RobotNativeTalkMode.Enabled,
                "a complete character keeps its Autonomous brain and native talk");

            var spawned = service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "municipal" });
            var agent = (RobotAgent)spawned.Value!;
            Check(spawned.ErrorCode == null && agent.Interaction.NativeTalkMode == RobotNativeTalkMode.Disabled,
                "a Dormant body-only spawn succeeds with native talk off");
            var wake = agent.SetBrainMode(RobotBrainMode.Autonomous);
            Check(wake.ErrorCode == ModErrorCode.Unavailable && wake.Message == RobotComposedReferences.BodyOnly
                && agent.BrainMode == RobotBrainMode.Dormant, "a body-only robot cannot wake a native brain");
            var talk = agent.SetInteraction(RobotInteractionOptions.NativeTalk());
            Check(talk.ErrorCode == ModErrorCode.Unavailable && talk.Message == RobotComposedReferences.BodyOnly
                && agent.Interaction.NativeTalkMode == RobotNativeTalkMode.Disabled, "a body-only robot cannot enable native talk");
            Check(agent.SetInteraction(RobotInteractionOptions.DisableNativeTalk()).ErrorCode == null, "a body-only robot accepts talk off");

            var custom = RobotInteractionOptions.Custom();
            var prompt = service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "municipal", Interaction = custom });
            Check(prompt.ErrorCode == null && ReferenceEquals(((RobotAgent)prompt.Value!).Interaction, custom),
                "a body-only robot keeps a custom interaction from its author");
        }

        private static void MissingReason()
        {
            var service = new RobotAgentService();
            service.catalog = service.catalog.Take(1).ToArray();
            const string plain = "The requested robot type is unavailable in this scene.";
            try
            {
                RobotKitDiagnostics.Last = RobotComposedRefusal.BodyPartUnavailable;
                var missing = service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "MuniciPal" });
                Check(missing.ErrorCode == ModErrorCode.Unavailable
                    && missing.Message == plain + " " + RobotComposedReferences.Explain(RobotComposedRefusal.BodyPartUnavailable),
                    "a missing MuniciPal reports its discovery reason to the author");
                Check(service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "copbot" }).Message == plain,
                    "other missing types do not borrow the composed reason");
                RobotKitDiagnostics.Last = null;
                Check(service.Spawn(new RobotAgentSpawnRequest { RobotTypeId = "municipal" }).Message == plain,
                    "no recorded discovery reason keeps the plain message");
            }
            finally { RobotKitDiagnostics.Last = null; }
        }

        private static void Activation()
        {
            var service = new RobotAgentService();
            var actor = service.SpawnComposed();
            service.CheckActivated(actor);
            Check(actor.Owner.Releases == 0 && !actor.Lease.gameObject.Destroyed && !actor.Actor.Destroyed,
                "activation must keep the actor and its builder assets alive");
            service.CheckSurroundings();
        }

        private static void CorpseSweep()
        {
            var service = new RobotAgentService();
            var corpse = service.SpawnComposed();
            var survivor = service.SpawnComposed();
            corpse.Actor.NativeDead = true;
            service.Tick(0f);
            Check(service.ActiveAgents.Count == 1 && service.agentsById.Count == 1
                && !service.agentsById.ContainsKey("robot-1"), "native death must prune the corpse handle before clearing the scene");
            Check(!corpse.Actor.Destroyed && !corpse.Lease.gameObject.Destroyed && corpse.Owner.Releases == 0,
                "native death must leave the corpse and its owned assets for the lease sweep");
            corpse.Lease.gameObject.SetActive(false);

            service.ClearAgents();

            CheckReleased(corpse, "lease sweep must find the inactive, untracked corpse");
            CheckReleased(survivor, "clear must despawn the remaining registered actor");
            service.CheckEmpty();
            service.CheckSurroundings();
        }

        private static void Rollback(bool afterPublication)
        {
            var service = new RobotAgentService();
            var fault = new InvalidOperationException("Injected spawn failure");
            if (afterPublication)
            {
                service.logger.OnInfo = () =>
                {
                    service.CheckActivated(service.lastBuild!);
                    var published = service.ActiveAgents.Single();
                    Check(ReferenceEquals(service.agentsById["robot-1"], published),
                        "late failure must occur after the actor appears in both registries and the cached snapshot");
                    throw fault;
                };
            }
            else
            {
                service.logger.OnConfigureBrain = () =>
                {
                    var build = service.lastBuild!;
                    Check(!build.Actor.activeInHierarchy && !build.Actor.Destroyed && build.Owner.Releases == 0
                        && ReferenceEquals(build.Lease.gameObject.transform.parent, service.incubator!.transform),
                        "early failure must occur while a fresh builder owns the inactive actor");
                    throw fault;
                };
            }

            var result = service.Spawn(Request);

            Check(result.ErrorCode == ModErrorCode.Unavailable && ReferenceEquals(RobotKitDiagnostics.LastError, fault),
                "spawn must report the injected construction failure");
            CheckReleased(service.lastBuild!, afterPublication ? "published actor rollback" : "unregistered clone rollback");
            service.CheckEmpty();
            service.CheckSurroundings();
        }

        private static void UnverifiedContext()
        {
            var service = new RobotAgentService { unverifiedContext = true };
            RobotKitDiagnostics.LastError = null;

            var result = service.Spawn(Request);

            Check(result.ErrorCode == ModErrorCode.Unavailable
                && result.Message == "The requested composed robot's native context could not be verified."
                && RobotKitDiagnostics.LastError == null,
                "an unverified composed context must return its own message without a construction diagnostic");
            Check(service.lastBuild == null, "an unverified composed context must not build an actor");
            service.CheckEmpty();
            service.CheckSurroundings();
        }

        private Build SpawnComposed()
        {
            var result = Spawn(Request);
            Check(result.ErrorCode == null, "fixture must successfully spawn a composed actor");
            return lastBuild!;
        }

        private void CheckActivated(Build build)
        {
            Check(ReferenceEquals(build.Lease.gameObject.transform.parent, root!.transform),
                "activation must move the builder from the inactive incubator to the live root");
            Check(ReferenceEquals(build.Actor.transform.parent, build.Lease.gameObject.transform)
                && build.Actor.activeInHierarchy && build.Lease.gameObject.activeInHierarchy,
                "activation must keep the actor under the builder and make both live in the hierarchy");
        }

        private void CheckEmpty() => Check(agents.Count == 0 && agentsById.Count == 0 && ActiveAgents.Count == 0,
            "cleanup must empty the list, id index and published snapshot");

        private void CheckSurroundings() => Check(!root!.Destroyed && root.activeInHierarchy
            && !incubator!.Destroyed && !incubator.activeSelf && !mapOwner.gameObject.Destroyed && mapOwner.Releases == 0
            && !siblingOwner.gameObject.Destroyed && siblingOwner.Releases == 0,
            "the service root, inactive incubator and unrelated owners must survive actor cleanup");

        private static void CheckReleased(Build build, string message) => Check(build.Owner.Releases == 1
            && !build.Lease.gameObject.activeSelf && build.Lease.gameObject.Destroyed && build.Actor.Destroyed,
            message + " must disable and destroy the builder and actor and release the native assets once");

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal sealed class RobotAgent(string id, GameObject actor, RobotAgentSpawnRequest request, Logger logger, object brainSnapshot) : IRobotAgent
    {
        internal string Id => id;
        private readonly GameObject go = actor;
        private bool despawned;
        private readonly object? replicaAnimation = null;
        private readonly object nativeBrainSnapshot = brainSnapshot;
        private RobotBrainMode brainMode = request.BrainMode;
        private RobotInteractionOptions interaction = request.Interaction;
        private string? bodyOnly;
        internal RobotBrainMode BrainMode => brainMode;
        internal RobotInteractionOptions Interaction => interaction;
        private static OperationResult<bool> ReplicaRefusal() => throw new InvalidOperationException("Unexpected replica.");
        private void Stop() { }
        private void ApplyInteraction() { }
        internal void RegisterSpeech() { }
        internal void ReleaseSpeech() { }
        internal void ReleaseWalk() { }
        internal void ResetInteractionOverrides() { }
        internal void OnActivated() { }
        internal void MakeReplica(object? animator, string? parameter) { }
        internal void SetScale(float scale) { }
        internal void SetTint(int tint) { }
        internal void Step() { }
        /* agent lifetime */
    }
}
""";

        private const string NativeBoundary = """
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed { get; private set; }

        public static void Destroy(GameObject value)
        {
            foreach (var child in value.transform.Children.ToArray()) Destroy(child.gameObject);
            value.transform.SetParent(null, false);
            value.Destroyed = true;
        }

        public static void DontDestroyOnLoad(GameObject value) { }
        public static GameObject Instantiate(GameObject prefab, Transform parent, bool worldPositionStays) =>
            throw new InvalidOperationException("Unexpected prefab instantiation.");
    }

    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public Component[] GetComponentsInChildren(Type type, bool includeInactive) =>
            gameObject.GetComponentsInChildren<Component>(includeInactive).Where(type.IsInstanceOfType).ToArray();
    }

    public class MonoBehaviour : Component { }
    public class Rigidbody : Component { public bool isKinematic; }

    public sealed class GameObject(string name) : Object
    {
        private readonly List<Component> components = new();
        private Transform? ownTransform;
        public Transform transform => ownTransform ??= new Transform { gameObject = this };
        public string name = name;
        public bool NativeDead;
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => !Destroyed && activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);

        public void SetActive(bool active) => activeSelf = active;
        public T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
        public bool TryGetComponent<T>(out T component) where T : Component => (component = GetComponent<T>()!) != null;
        public T AddComponent<T>() where T : Component
        {
            var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
            component.gameObject = this;
            components.Add(component);
            return component;
        }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            if (Destroyed || !includeInactive && !activeInHierarchy) return Array.Empty<T>();
            return components.OfType<T>().Concat(transform.Children.SelectMany(child =>
                child.gameObject.GetComponentsInChildren<T>(includeInactive))).ToArray();
        }
    }

    public sealed class Transform : Component
    {
        public Transform? parent { get; private set; }
        internal readonly List<Transform> Children = new();

        public void SetParent(Transform? next, bool worldPositionStays)
        {
            parent?.Children.Remove(this);
            parent = next;
            next?.Children.Add(this);
        }
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation) { }
    }

    public readonly record struct Vector3(float X, float Y, float Z)
    {
        public static Vector3 up => new(0f, 1f, 0f);
        public Vector3 normalized => this;
        public float sqrMagnitude => X * X + Y * Y + Z * Z;
    }
    public readonly struct Quaternion
    {
        public static Quaternion identity => default;
        public static Quaternion LookRotation(Vector3 facing, Vector3 up) => default;
    }
}

namespace TopiaForge.Mods
{
    public enum ModErrorCode { InvalidState, Unavailable }
    public enum RobotBrainMode { Dormant, Autonomous }
    public enum RobotNativeTalkMode { Enabled, Disabled }
    public sealed class RobotInteractionOptions
    {
        public RobotNativeTalkMode NativeTalkMode { get; private init; }
        public object? CustomInteraction { get; private init; }
        public static RobotInteractionOptions NativeTalk() => new();
        public static RobotInteractionOptions DisableNativeTalk() => new() { NativeTalkMode = RobotNativeTalkMode.Disabled };
        public static RobotInteractionOptions Custom() => new() { NativeTalkMode = RobotNativeTalkMode.Disabled, CustomInteraction = new object() };
        public RobotInteractionOptions Clone() => this;
    }
    public interface IRobotAgent { }
    public sealed class RobotAgentSpawnRequest
    {
        public string RobotTypeId { get; init; } = "composed";
        public string? Name => null;
        public Vector3 Position => default;
        public Vector3? Facing => null;
        public RobotBrainMode BrainMode { get; init; }
        public RobotInteractionOptions Interaction { get; init; } = RobotInteractionOptions.NativeTalk();
        public float Scale => 1f;
        public int? Tint => null;
    }
    public readonly record struct OperationResult<T>(ModErrorCode? ErrorCode, string? Message = null, T? Value = default)
    {
        public static OperationResult<T> Success(T value) => new(null, null, value);
        public static OperationResult<T> Failure(ModErrorCode code, string message) => new(code, message);
    }
}

namespace TopiaForge.RobotKit
{
    internal sealed class NativeAssetOwner : MonoBehaviour
    {
        internal int Releases;
        public void DestroyAssets() => Releases++;
    }
    internal sealed class RobotComposedContextUnverifiedException : Exception { }
    internal sealed class RobotAgentEntityIdentityAnchor : MonoBehaviour { internal void Initialize(string id) { } }
    internal static class GameReflection
    {
        internal static object CaptureBrainState(GameObject actor) => new();
        internal static void ConfigureBrain(GameObject actor, TopiaForge.Mods.RobotBrainMode mode, Logger logger) => logger.OnConfigureBrain?.Invoke();
        internal static void ApplyBrainMode(GameObject actor, TopiaForge.Mods.RobotBrainMode mode, object snapshot, Logger logger) { }
        internal static bool HasKilledComponent(GameObject actor) => actor.NativeDead;
    }
    internal sealed class Logger
    {
        internal Action? OnInfo, OnConfigureBrain;
        internal void Info(string message) => OnInfo?.Invoke();
        internal void Warn(string message) { }
        internal void Debug(string message) { }
    }
    internal static class RobotKitDiagnostics
    {
        internal static Exception? LastError;
        internal static RobotComposedRefusal? Last;
        internal static void ReportOnce(string category, Exception error) => LastError = error;
        internal static RobotComposedRefusal? LastComposedDiscovery(object? world) => Last;
    }
    internal static class RobotKitLog { internal static string ExceptionType(Exception error) => error.GetType().Name; }
    internal sealed class Replication { internal void Forget(string id) { } }
    internal sealed class Search
    {
        internal bool IsComplete => false;
        internal void Step() { }
        internal void Cancel() { }
    }
}
""";
    }
}
