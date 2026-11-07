using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotEditorAuthorityTests
    {
        internal static void Run()
        {
            var robotKit = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            var production = new[]
            {
                "RobotSceneEditorService.cs", "RobotEditTarget.cs", "RobotEditLease.cs",
                "NativeEntityAdapter.cs", "RobotKitLog.cs"
            };
            var reflection = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(robotKit, "GameReflection.cs")))
                .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(type => type.Identifier.ValueText == "GameReflection");
            var snapshot = reflection.Members.OfType<ClassDeclarationSyntax>()
                .Single(type => type.Identifier.ValueText == "BrainStateSnapshot");
            string Method(string name) => reflection.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == name).ToFullString();
            var restoration = string.Join("\n", snapshot.ToFullString(), Method("RestoreBrainStateConflictSafe"),
                Method("RestoreFieldIfExpected"));
            var sources = production.Select(file => File.ReadAllText(Path.Combine(robotKit, file)))
                .Concat(new[] { Engine, NativeBoundary.Replace("/* production brain restoration */", restoration), Scenarios });
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            // Run the shipped editor, target, lease, identity marker and conflict-safe brain restoration.
            // Unity and the remaining native boundaries use managed stand-ins.
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success)
            {
                throw new InvalidOperationException("Robot editor authority probe compilation failed: " + string.Join("\n",
                    emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            }

            var probe = Assembly.Load(bytes.ToArray()).GetType("RobotEditorProbe", throwOnError: true)!;
            foreach (var scenario in new[] { "DiscoveryAndResolution", "RetainedTarget", "ManagedTransition",
                "ManagedSceneTeardown", "PreviewRollback", "MovedTreeRestoration", "OrdinaryRestoration" })
            {
                ((Action)probe.GetMethod(scenario)!.CreateDelegate(typeof(Action)))();
                Console.WriteLine("Robot editor authority: " + scenario + " passed.");
            }
        }

        private const string Engine = """
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public class Object
    {
        internal static readonly List<GameObject> SceneObjects = new();

        public static void ResetScene() => SceneObjects.Clear();
        public static Object[] FindObjectsByType(Type type, FindObjectsSortMode sortMode) =>
            SceneObjects.Where(item => item.ActiveInHierarchy).SelectMany(item => item.Components)
                .Where(type.IsInstanceOfType).ToArray();
    }

    public enum FindObjectsSortMode { InstanceID }

    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public Component? GetComponentInParent(Type type) => gameObject.Parents()
            .Where(item => item.ActiveInHierarchy)
            .SelectMany(item => item.Components).FirstOrDefault(type.IsInstanceOfType);
        public T? GetComponentInParent<T>(bool includeInactive) where T : Component =>
            gameObject.GetComponentInParent<T>(includeInactive);
    }

    public class Behaviour : Component
    {
        private bool isEnabled = true;
        public int EnabledWrites;
        public bool enabled
        {
            get => isEnabled;
            set { isEnabled = value; EnabledWrites++; }
        }
    }

    public class MonoBehaviour : Behaviour { }

    public class GameObject : Object
    {
        private static int nextId;
        private readonly int instanceId = ++nextId;
        internal readonly List<Component> Components = new();
        private readonly Transform pose;
        public Transform transform { get { TransformReads++; return pose; } }
        public int TransformReads;
        public readonly Scene scene = new();
        public readonly string name;
        public GameObject? Parent;
        public bool Active = true;
        public bool ActiveInHierarchy => Active && (Parent?.ActiveInHierarchy ?? true);

        public GameObject(string name, GameObject? parent = null)
        {
            this.name = name;
            Parent = parent;
            pose = new Transform(this);
            SceneObjects.Add(this);
        }

        public int GetInstanceID() => instanceId;
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this };
            Components.Add(component);
            return component;
        }

        public T? GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
        public T? GetComponentInParent<T>(bool includeInactive) where T : Component => Parents()
            .Where(item => includeInactive || item.ActiveInHierarchy)
            .SelectMany(item => item.Components).OfType<T>().FirstOrDefault();
        public T? GetComponentInChildren<T>(bool includeInactive) where T : Component =>
            GetComponentsInChildren<T>(includeInactive).FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => Children()
            .Where(item => includeInactive || item.ActiveInHierarchy)
            .SelectMany(item => item.Components).OfType<T>().ToArray();

        internal IEnumerable<GameObject> Parents()
        {
            for (GameObject? item = this; item != null; item = item.Parent)
                yield return item;
        }

        internal IEnumerable<GameObject> Children() => SceneObjects.Where(item => item.Parents().Contains(this));
    }

    public sealed class Scene
    {
        public int handle => 1;
        public string name => "Fixture";
        public bool IsValid() => true;
    }

    public sealed class Transform
    {
        private readonly GameObject owner;
        internal Transform(GameObject owner) => this.owner = owner;
        public bool IsChildOf(Transform parent) => owner.Parents().Contains(parent.owner);

        public Vector3 position;
        public Quaternion rotation = new(0, 0, 0, 1);
        public Vector3 localScale = new(1, 1, 1);
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }
    }

    public readonly record struct Vector3(float x, float y, float z);
    public readonly record struct Quaternion(float x, float y, float z, float w);
}
""";

        private const string NativeBoundary = """
using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotBody : Component { }
    internal sealed class QuestProgress : Component { }
    internal sealed class BehaviorTree : Behaviour { }
    internal sealed class LLMAgent : Component
    {
        internal bool llmDisabled;
        internal RobotBrainMode Mode
        {
            get => llmDisabled ? RobotBrainMode.Dormant : RobotBrainMode.Autonomous;
            set => llmDisabled = value == RobotBrainMode.Dormant;
        }
        internal object? Personality = new object();
        internal int BrainReads;
        internal int BrainApplies;
        internal int BrainRollbacks;
        internal int PersonalityReads;
        internal int PersonalityApplies;
        internal int PersonalityRestores;
        internal Action? DuringBrainApply;
    }

    internal sealed class RobotAgentService
    {
        internal IRobotAgent? Agent;
        internal IRobotAgent? FindAgentByEntity(IEntity entity) => entity.Id == Agent?.Id ? Agent : null;
    }

    public class AgentHandle : DispatchProxy, INativeEntityAdapter
    {
        public GameObject NativeGameObject { get; set; } = null!;

        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Id" => "robotkit:fixture",
            "get_IsAlive" => true,
            _ => throw new InvalidOperationException("Unexpected agent call.")
        };
    }

    internal static class GameReflection
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /* production brain restoration */

        internal static Component? FindComponent(GameObject root, params string[] names) =>
            root.GetComponentsInChildren<Component>(true).FirstOrDefault(component => names.Contains(component.GetType().Name));

        internal static BrainStateSnapshot CaptureBrainState(GameObject root)
        {
            var agent = GetAgent(root);
            agent.BrainReads++;
            var snapshot = new BrainStateSnapshot { LlmDisabled = agent.llmDisabled };
            foreach (var tree in root.GetComponentsInChildren<BehaviorTree>(true))
                snapshot.BehaviorTrees.Add((tree, tree.enabled));
            return snapshot;
        }

        internal static void ApplyBrainMode(GameObject root, RobotBrainMode mode, BrainStateSnapshot? original, IModLogger logger)
        {
            var agent = GetAgent(root);
            agent.BrainApplies++;
            agent.Mode = mode;
            foreach (var tree in root.GetComponentsInChildren<BehaviorTree>(true))
                tree.enabled = mode == RobotBrainMode.Autonomous;
            agent.DuringBrainApply?.Invoke();
        }

        internal static void RestoreBrainState(GameObject root, BrainStateSnapshot? original, IModLogger logger)
        {
            var agent = GetAgent(root);
            agent.BrainRollbacks++;
            agent.llmDisabled = original?.LlmDisabled is true;
        }

        private static LLMAgent GetAgent(GameObject root) => root.GetComponentInChildren<LLMAgent>(true)!;
    }

    internal static class RobotPersonalityBridge
    {
        internal static Type? AgentType => typeof(LLMAgent);
        internal static Type? RobotBodyType => typeof(RobotBody);
        internal static bool IsAvailable => true;

        internal sealed class Temporary
        {
            internal int DestroyCount;
        }

        internal static object? GetHackedPersonality(Component agent)
        {
            var native = (LLMAgent)agent;
            native.PersonalityReads++;
            return native.Personality;
        }

        internal static OperationResult<object> Apply(Component agent, RobotPersonalityDraft draft)
        {
            var native = (LLMAgent)agent;
            native.PersonalityApplies++;
            return OperationResult<object>.Success(native.Personality = new Temporary());
        }

        internal static bool IsCurrent(Component agent, object? temporary) =>
            ReferenceEquals(((LLMAgent)agent).Personality, temporary);

        internal static void Restore(Component agent, object? original)
        {
            var native = (LLMAgent)agent;
            native.PersonalityRestores++;
            native.Personality = original;
        }

        internal static void DestroyTemporary(object? temporary)
        {
            if (temporary is Temporary owned) owned.DestroyCount++;
        }
    }

    internal sealed class ProbeLogger : IModLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
    }
}
""";

        private const string Scenarios = """
using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.RobotKit;
using UnityEngine;

public static class RobotEditorProbe
{
    private static readonly RobotPersonalityDraft Draft = new("Preview", "Test temporary personality.");
    private static readonly TransformState Moved = new(new Vec3(2, 3, 4), Quat.Identity, new Vec3(1, 1, 1));

    public static void DiscoveryAndResolution()
    {
        using var editor = CreateEditor(out var agents);
        var scenery = new GameObject("Scene robots");
        var ordinary = CreateRobot("Ordinary", scenery);
        var rootMarked = CreateRobot("Managed clone", scenery);
        MarkManaged(rootMarked.Root);
        var wrapper = new GameObject("Composed actor", scenery);
        MarkManaged(wrapper);
        var ancestorMarked = CreateRobot("Composed body", wrapper);
        var descendantMarked = CreateRobot("Managed subtree", scenery);
        var markerChild = new GameObject("Inactive managed child", descendantMarked.Root);
        markerChild.Active = false;
        MarkManaged(markerChild);
        var quest = CreateRobot("Quest robot", scenery);
        quest.Root.AddComponent<QuestProgress>();

        Check(editor.Targets.Select(target => target.DisplayName).SequenceEqual(new[] { ordinary.Root.name }),
            "Discovery must hide marked roots, ancestors, inactive descendants and progression robots while retaining siblings.");
        Check(editor.Targets.Single().IsNativeSceneObject, "The ordinary scene target must be marked as native.");
        var handle = DispatchProxy.Create<IRobotAgent, AgentHandle>();
        agents.Agent = handle;
        foreach (var root in new[] { rootMarked.Root, wrapper, ancestorMarked.Root, descendantMarked.Root })
        {
            ((AgentHandle)handle).NativeGameObject = root;
            Check(!editor.TryResolve(handle, out var target) && target == null, "Resolution must hide managed actors: " + root.name);
        }

        wrapper.Active = false;
        ((AgentHandle)handle).NativeGameObject = wrapper;
        Check(!editor.TryResolve(handle, out var inactive) && inactive == null, "Resolution must honor inactive ancestors.");
    }

    public static void RetainedTarget()
    {
        using var editor = CreateEditor(out _);
        var (root, brain) = CreateRobot("Retained");
        var target = editor.Targets.Single();
        MarkManaged(root);

        Check(target.IsAlive, "Adding ownership must not make the target disappear.");
        Refused(editor.BeginTemporaryEdit(target), "Retained target admission");
        Check(root.TransformReads == 0 && brain.BrainReads == 0 && brain.PersonalityReads == 0
            && brain.BrainApplies == 0 && brain.PersonalityApplies == 0,
            "A retained managed target must be refused before snapshot or native mutation.");
    }

    public static void ManagedTransition()
    {
        using var editor = CreateEditor(out _);
        var (root, brain) = CreateRobot("Active edit");
        var target = editor.Targets.Single();
        using var lease = Value(editor.BeginTemporaryEdit(target));
        var temporary = PreviewDormant(lease, brain);
        var transformReads = root.TransformReads;
        var brainReads = brain.BrainReads;
        var managed = new GameObject("Managed ancestor");
        MarkManaged(managed);
        root.Parent = managed;

        Refused(lease.PreviewBrainMode(RobotBrainMode.Autonomous), "Managed autonomous preview");
        Refused(lease.PreviewBrainMode(RobotBrainMode.Dormant), "Managed dormant preview");
        Refused(lease.PreviewTransform(TransformState.Identity), "Managed transform preview");
        Refused(lease.PreviewPersonality(Draft), "Managed personality preview");
        Refused(lease.Restore(), "Managed restore");
        Check(!lease.IsActive && !Value(lease.Restore()), "A restored lease must end and stay ended.");
        Check(root.TransformReads == transformReads && brain.BrainReads == brainReads
            && brain.BrainApplies == 1 && brain.BrainRollbacks == 0
            && brain.PersonalityApplies == 1 && brain.PersonalityRestores == 0
            && brain.Mode == RobotBrainMode.Dormant && temporary.DestroyCount == 1,
            "A managed transition must prevent all native edit writes and release the temporary personality once.");

        root.Parent = null;
        using var replacement = Value(editor.BeginTemporaryEdit(target));
        lease.Dispose();
        Refused(editor.BeginTemporaryEdit(target), "Repeated disposal must not release the replacement lease");
        Check(replacement.IsActive && temporary.DestroyCount == 1, "The replacement must retain its lease slot.");
    }

    public static void ManagedSceneTeardown()
    {
        using var editor = CreateEditor(out _);
        var (root, brain) = CreateRobot("Scene teardown");
        var lease = Value(editor.BeginTemporaryEdit(editor.Targets.Single()));
        var temporary = PreviewDormant(lease, brain);
        MarkManaged(root);

        var (ordinary, ordinaryBrain) = CreateRobot("Ordinary scene teardown");
        var ordinaryLease = Value(editor.BeginTemporaryEdit(editor.Targets.Single(target => target.DisplayName == ordinary.name)));
        var ordinaryTemporary = PreviewDormant(ordinaryLease, ordinaryBrain);

        editor.OnSceneChanged();
        editor.Dispose();
        lease.Dispose();
        ordinaryLease.Dispose();
        Check(!lease.IsActive && !Value(lease.Restore()) && temporary.DestroyCount == 1
            && brain.Mode == RobotBrainMode.Dormant && brain.BrainRollbacks == 0 && brain.PersonalityRestores == 0,
            "Scene cleanup and repeated disposal must release the managed lease without writing native snapshots.");
        Check(!ordinaryLease.IsActive && ordinaryTemporary.DestroyCount == 1
            && ordinaryBrain.Mode == RobotBrainMode.Autonomous && ordinaryBrain.BrainRollbacks == 0,
            "The same scene cleanup must restore and release eligible leases.");
    }

    public static void PreviewRollback()
    {
        using var editor = CreateEditor(out _);
        var (root, brain) = CreateRobot("Ownership during native call");
        using var lease = Value(editor.BeginTemporaryEdit(editor.Targets.Single()));
        brain.DuringBrainApply = () =>
        {
            MarkManaged(root);
            throw new InvalidOperationException("Simulated native apply failure.");
        };

        Check(!lease.PreviewBrainMode(RobotBrainMode.Dormant).Succeeded, "The failed preview must report failure.");
        Check(brain.BrainApplies == 1 && brain.BrainRollbacks == 0 && brain.Mode == RobotBrainMode.Dormant,
            "Rollback after a native callback changes ownership must not reawaken the brain.");

        var (other, otherBrain) = CreateRobot("Ownership during successful native call");
        using var otherLease = Value(editor.BeginTemporaryEdit(editor.Targets.Single()));
        otherBrain.DuringBrainApply = () => MarkManaged(other);
        Refused(otherLease.PreviewBrainMode(RobotBrainMode.Dormant), "Ownership changed during native preview");
        Check(otherBrain.BrainApplies == 1 && otherBrain.BrainReads == 2 && otherBrain.BrainRollbacks == 0,
            "A preview which crosses the authority boundary must stop before readback or rollback.");
    }

    public static void MovedTreeRestoration()
    {
        using var editor = CreateEditor(out _);
        var scene = new GameObject("Scene robots");
        var (root, brain) = CreateRobot("Ordinary", scene);
        var rootTree = root.AddComponent<BehaviorTree>();
        var retainedTree = new GameObject("Retained tree", root).AddComponent<BehaviorTree>();
        var movedChild = new GameObject("Moved tree", root);
        var movedTree = movedChild.AddComponent<BehaviorTree>();
        var (managed, _) = CreateRobot("Managed sibling", scene);
        MarkManaged(managed);

        using var lease = Value(editor.BeginTemporaryEdit(editor.Targets.Single()));
        Check(Value(lease.PreviewBrainMode(RobotBrainMode.Dormant)) && !movedTree.enabled
            && !rootTree.enabled && !retainedTree.enabled, "Dormant preview must disable the original trees.");
        movedChild.Parent = managed;
        var writes = movedTree.EnabledWrites;

        Refused(lease.Restore(), "Restoring a tree that left the original robot");
        Check(!movedTree.enabled && movedTree.EnabledWrites == writes,
            "Restoration must not touch a captured tree after it moves to a managed sibling.");
        Check(rootTree.enabled && retainedTree.enabled && brain.Mode == RobotBrainMode.Autonomous,
            "The remaining scene robot brain and its root and child trees must still restore.");
    }

    public static void OrdinaryRestoration()
    {
        using var editor = CreateEditor(out _);
        var (root, brain) = CreateRobot("Ordinary restoration");
        var original = brain.Personality;
        var target = editor.Targets.Single();
        using var lease = Value(editor.BeginTemporaryEdit(target));
        Refused(editor.BeginTemporaryEdit(target), "Ordinary edit exclusivity");
        var first = PreviewDormant(lease, brain);
        Check(Value(lease.PreviewBrainMode(RobotBrainMode.Autonomous)) && brain.Mode == RobotBrainMode.Autonomous,
            "An eligible scene robot must still support native autonomous preview.");
        Check(Value(lease.PreviewBrainMode(RobotBrainMode.Dormant)), "A second ordinary dormant preview must succeed.");
        Check(Value(lease.PreviewPersonality(Draft)), "An ordinary personality preview can be replaced.");
        var second = (RobotPersonalityBridge.Temporary)brain.Personality!;
        Check(first.DestroyCount == 1 && second.DestroyCount == 0, "Replacing a preview must release only the old personality.");

        lease.Dispose();
        lease.Dispose();
        Check(brain.Mode == RobotBrainMode.Autonomous && brain.BrainRollbacks == 0
            && ReferenceEquals(brain.Personality, original) && brain.PersonalityRestores == 1
            && first.DestroyCount == 1 && second.DestroyCount == 1 && !Value(lease.Restore()),
            "Disposing an ordinary lease must restore brain and personality and release each asset once.");
        Check(target.TryGetTransform(out var restored) && restored.Equals(TransformState.Identity),
            "Disposing an ordinary lease must restore its original transform.");
        using var replacement = Value(editor.BeginTemporaryEdit(target));
        Check(replacement.IsActive, "Ordinary disposal must free the lease slot.");
    }

    private static RobotSceneEditorService CreateEditor(out RobotAgentService agents)
    {
        UnityEngine.Object.ResetScene();
        agents = new RobotAgentService();
        return new RobotSceneEditorService(agents, new ProbeLogger());
    }

    private static (GameObject Root, LLMAgent Brain) CreateRobot(string name, GameObject? parent = null)
    {
        var root = new GameObject(name, parent);
        root.AddComponent<RobotBody>();
        var brain = new GameObject(name + " brain", root).AddComponent<LLMAgent>();
        return (root, brain);
    }

    private static void MarkManaged(GameObject root) =>
        root.AddComponent<RobotAgentEntityIdentityAnchor>().Initialize("robotkit:fixture");

    private static RobotPersonalityBridge.Temporary PreviewDormant(IRobotEditLease lease, LLMAgent brain)
    {
        Check(Value(lease.PreviewTransform(Moved)).Equals(Moved), "Ordinary transform preview must take effect.");
        Check(Value(lease.PreviewBrainMode(RobotBrainMode.Dormant)) && brain.Mode == RobotBrainMode.Dormant,
            "Ordinary dormant preview must take effect.");
        Check(Value(lease.PreviewPersonality(Draft)), "Ordinary personality preview must succeed.");
        return (RobotPersonalityBridge.Temporary)brain.Personality!;
    }

    private static T Value<T>(OperationResult<T> result) where T : notnull =>
        result.TryGetValue(out var value) ? value : throw new InvalidOperationException(result.ErrorMessage);

    private static void Refused<T>(OperationResult<T> result, string description) where T : notnull =>
        Check(!result.Succeeded && result.ErrorCode == ModErrorCode.Conflict, description + " must report Conflict.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
""";
    }
}
