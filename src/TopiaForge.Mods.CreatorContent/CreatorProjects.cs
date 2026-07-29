using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Controls where an event project may run.</summary>
    public enum CreatorProjectScope
    {
        /// <summary>The project is intended for the managed sandbox world.</summary>
        Sandbox = 0,
        /// <summary>The project may run in an explicitly confirmed ordinary game scene.</summary>
        Global = 1
    }

    /// <summary>Chooses the world-space origin used to materialize project-relative transforms.</summary>
    public enum CreatorProjectOrigin
    {
        /// <summary>Transforms are relative to the managed world's authored origin.</summary>
        World = 0,
        /// <summary>Transforms are relative to the local player's position when a run begins.</summary>
        PlayerAtRun = 1
    }

    /// <summary>Identifies the bounded behavior of a visual graph node.</summary>
    public enum CreatorGraphNodeKind
    {
        /// <summary>Fires when a project run begins.</summary>
        ProjectStart = 0,
        /// <summary>Fires when manually invoked by the workbench.</summary>
        ManualTrigger = 1,
        /// <summary>Fires after an interaction trigger.</summary>
        InteractionTrigger = 2,
        /// <summary>Fires after the player enters a configured radius.</summary>
        PlayerEnteredRadius = 3,
        /// <summary>Fires when a declared entity is removed or a reversibly borrowed target is soft-hidden.</summary>
        EntityRemoved = 4,
        /// <summary>Fires after a robot objective reaches a configured state.</summary>
        RobotObjectiveState = 5,
        /// <summary>Fires after a validated closed-set conversation decision.</summary>
        ConversationDecision = 6,
        /// <summary>Waits for bounded world-clock time.</summary>
        Delay = 20,
        /// <summary>Branches through true or false after evaluating bounded declared state.</summary>
        StateCondition = 21,
        /// <summary>Emits each a bounded number of times, then emits done.</summary>
        Repeat = 22,
        /// <summary>Spawns a declared project entity.</summary>
        SpawnContent = 40,
        /// <summary>Despawns a project-owned entity or soft-hides a supported reversible borrowed target.</summary>
        DespawnContent = 41,
        /// <summary>Changes a project-owned or reversibly borrowed transform.</summary>
        SetTransform = 42,
        /// <summary>Configures a RobotKit-managed robot.</summary>
        ConfigureRobot = 43,
        /// <summary>Applies a bounded temporary robot personality.</summary>
        SetRobotPersonality = 44,
        /// <summary>Assigns a RobotKit objective.</summary>
        SetRobotObjective = 45,
        /// <summary>Displays a robot emote.</summary>
        SetRobotEmote = 46,
        /// <summary>Begins a bounded conversation.</summary>
        BeginConversation = 47,
        /// <summary>Shows a TopiaForge toast.</summary>
        ShowToast = 48,
        /// <summary>Plays a registered audio cue.</summary>
        PlayAudio = 49
    }

    /// <summary>Defines one content-backed logical entity in a project.</summary>
    public sealed class CreatorProjectEntity
    {
        /// <summary>Creates a project entity.</summary>
        public CreatorProjectEntity(
            string id,
            string displayName,
            string contentId,
            string expectedSourceVersion,
            TransformState transform,
            bool spawnOnStart)
        {
            Id = Required(id, nameof(id));
            DisplayName = Required(displayName, nameof(displayName));
            ContentId = Required(contentId, nameof(contentId));
            ExpectedSourceVersion = expectedSourceVersion ?? string.Empty;
            Transform = transform;
            SpawnOnStart = spawnOnStart;
        }

        /// <summary>Gets the project-local stable id.</summary>
        public string Id { get; }
        /// <summary>Gets the user-facing name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the stable source-qualified catalog id.</summary>
        public string ContentId { get; }
        /// <summary>Gets the optional source version expected when authored.</summary>
        public string ExpectedSourceVersion { get; }
        /// <summary>Gets the transform relative to the run origin.</summary>
        public TransformState Transform { get; }
        /// <summary>Gets whether the runner spawns it before firing project-start nodes.</summary>
        public bool SpawnOnStart { get; }

        private static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
            return value;
        }
    }

    /// <summary>Defines a logical, explicitly resolved native scene binding.</summary>
    public sealed class CreatorNativeBinding
    {
        /// <summary>Creates a native binding recipe.</summary>
        public CreatorNativeBinding(
            string id,
            string displayName,
            string sceneName,
            string nameContains,
            Vec3 expectedPosition,
            float searchRadius,
            string adapterId = "")
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("A scene name is required.", nameof(sceneName));
            if (string.IsNullOrWhiteSpace(nameContains)) throw new ArgumentException("A name fragment is required.", nameof(nameContains));
            if (!expectedPosition.IsFinite) throw new ArgumentException("The expected position must be finite.", nameof(expectedPosition));
            if (searchRadius <= 0f || float.IsNaN(searchRadius) || float.IsInfinity(searchRadius)) throw new ArgumentOutOfRangeException(nameof(searchRadius));
            Id = id;
            DisplayName = displayName;
            SceneName = sceneName;
            NameContains = nameContains;
            ExpectedPosition = expectedPosition;
            SearchRadius = searchRadius;
            AdapterId = adapterId ?? string.Empty;
        }

        /// <summary>Gets the project-local binding id.</summary>
        public string Id { get; }
        /// <summary>Gets the user-facing name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the exact scene name required for resolution.</summary>
        public string SceneName { get; }
        /// <summary>Gets the required case-insensitive object-name fragment.</summary>
        public string NameContains { get; }
        /// <summary>Gets the authored expected position.</summary>
        public Vec3 ExpectedPosition { get; }
        /// <summary>Gets the bounded search radius.</summary>
        public float SearchRadius { get; }
        /// <summary>Gets the stable curated native adapter required to resolve and restore this binding.</summary>
        public string AdapterId { get; }
    }

    /// <summary>Defines reusable bounded conversation guidance.</summary>
    public sealed class CreatorPersona
    {
        private readonly IReadOnlyDictionary<string, string> facts;

        /// <summary>Creates a project persona.</summary>
        public CreatorPersona(
            string id,
            string displayName,
            string systemFrame,
            string replyGuidance,
            IReadOnlyDictionary<string, string>? facts = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            Id = id;
            DisplayName = displayName;
            SystemFrame = systemFrame ?? string.Empty;
            ReplyGuidance = replyGuidance ?? string.Empty;
            this.facts = new ReadOnlyDictionary<string, string>(
                facts == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(facts, StringComparer.Ordinal));
        }

        /// <summary>Gets the project-local persona id.</summary>
        public string Id { get; }
        /// <summary>Gets the user-facing name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the bounded role and behavior frame.</summary>
        public string SystemFrame { get; }
        /// <summary>Gets optional response guidance.</summary>
        public string ReplyGuidance { get; }
        /// <summary>Gets immutable ground-truth facts.</summary>
        public IReadOnlyDictionary<string, string> Facts => facts;
    }

    /// <summary>Defines one visual event-graph node.</summary>
    public sealed class CreatorGraphNode
    {
        private readonly IReadOnlyDictionary<string, string> parameters;

        /// <summary>Creates a graph node.</summary>
        public CreatorGraphNode(
            string id,
            CreatorGraphNodeKind kind,
            Vec2 editorPosition,
            IReadOnlyDictionary<string, string>? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A node id is required.", nameof(id));
            if (!Enum.IsDefined(typeof(CreatorGraphNodeKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!editorPosition.IsFinite) throw new ArgumentException("The editor position must be finite.", nameof(editorPosition));
            Id = id;
            Kind = kind;
            EditorPosition = editorPosition;
            this.parameters = new ReadOnlyDictionary<string, string>(
                parameters == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(parameters, StringComparer.Ordinal));
        }

        /// <summary>Gets the project-local node id.</summary>
        public string Id { get; }
        /// <summary>Gets the bounded node behavior.</summary>
        public CreatorGraphNodeKind Kind { get; }
        /// <summary>Gets the visual-editor canvas position.</summary>
        public Vec2 EditorPosition { get; }
        /// <summary>Gets bounded node parameters interpreted by its node kind.</summary>
        public IReadOnlyDictionary<string, string> Parameters => parameters;
    }

    /// <summary>Defines a directed visual event-graph connection.</summary>
    public sealed class CreatorGraphEdge
    {
        /// <summary>Creates a graph edge.</summary>
        public CreatorGraphEdge(string fromNodeId, string fromPort, string toNodeId, string toPort)
        {
            if (string.IsNullOrWhiteSpace(fromNodeId)) throw new ArgumentException("A source node id is required.", nameof(fromNodeId));
            if (string.IsNullOrWhiteSpace(fromPort)) throw new ArgumentException("A source port is required.", nameof(fromPort));
            if (string.IsNullOrWhiteSpace(toNodeId)) throw new ArgumentException("A target node id is required.", nameof(toNodeId));
            if (string.IsNullOrWhiteSpace(toPort)) throw new ArgumentException("A target port is required.", nameof(toPort));
            FromNodeId = fromNodeId;
            FromPort = fromPort;
            ToNodeId = toNodeId;
            ToPort = toPort;
        }

        /// <summary>Gets the source node id.</summary>
        public string FromNodeId { get; }
        /// <summary>Gets the source output port.</summary>
        public string FromPort { get; }
        /// <summary>Gets the target node id.</summary>
        public string ToNodeId { get; }
        /// <summary>Gets the target input port.</summary>
        public string ToPort { get; }
    }

    /// <summary>Immutable local event-project document.</summary>
    public sealed class CreatorEventProject
    {
        private readonly IReadOnlyList<CreatorProjectEntity> entities;
        private readonly IReadOnlyList<CreatorNativeBinding> nativeBindings;
        private readonly IReadOnlyList<CreatorPersona> personas;
        private readonly IReadOnlyList<CreatorGraphNode> nodes;
        private readonly IReadOnlyList<CreatorGraphEdge> edges;

        /// <summary>Creates an event project.</summary>
        public CreatorEventProject(
            int schemaVersion,
            string id,
            string displayName,
            string description,
            CreatorProjectScope scope,
            string worldId,
            string sceneName,
            DateTimeOffset modifiedAtUtc,
            IEnumerable<CreatorProjectEntity>? entities = null,
            IEnumerable<CreatorNativeBinding>? nativeBindings = null,
            IEnumerable<CreatorPersona>? personas = null,
            IEnumerable<CreatorGraphNode>? nodes = null,
            IEnumerable<CreatorGraphEdge>? edges = null,
            CreatorProjectOrigin origin = CreatorProjectOrigin.World)
        {
            if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A project id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (!Enum.IsDefined(typeof(CreatorProjectScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (!Enum.IsDefined(typeof(CreatorProjectOrigin), origin)) throw new ArgumentOutOfRangeException(nameof(origin));
            SchemaVersion = schemaVersion;
            Id = id;
            DisplayName = displayName;
            Description = description ?? string.Empty;
            Scope = scope;
            Origin = origin;
            WorldId = worldId ?? string.Empty;
            SceneName = sceneName ?? string.Empty;
            ModifiedAtUtc = modifiedAtUtc.ToUniversalTime();
            this.entities = Copy(entities);
            this.nativeBindings = Copy(nativeBindings);
            this.personas = Copy(personas);
            this.nodes = Copy(nodes);
            this.edges = Copy(edges);
        }

        /// <summary>Gets the document schema version.</summary>
        public int SchemaVersion { get; }
        /// <summary>Gets the stable local project id.</summary>
        public string Id { get; }
        /// <summary>Gets the user-facing project name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the optional description.</summary>
        public string Description { get; }
        /// <summary>Gets the permitted run scope.</summary>
        public CreatorProjectScope Scope { get; }
        /// <summary>Gets the origin used to materialize relative entity transforms.</summary>
        public CreatorProjectOrigin Origin { get; }
        /// <summary>Gets an optional required managed world id.</summary>
        public string WorldId { get; }
        /// <summary>Gets an optional required native scene name.</summary>
        public string SceneName { get; }
        /// <summary>Gets the last authored UTC timestamp.</summary>
        public DateTimeOffset ModifiedAtUtc { get; }
        /// <summary>Gets declared content-backed entities.</summary>
        public IReadOnlyList<CreatorProjectEntity> Entities => entities;
        /// <summary>Gets logical native binding recipes; runtime entity ids are never persisted.</summary>
        public IReadOnlyList<CreatorNativeBinding> NativeBindings => nativeBindings;
        /// <summary>Gets reusable conversation personas.</summary>
        public IReadOnlyList<CreatorPersona> Personas => personas;
        /// <summary>Gets graph nodes.</summary>
        public IReadOnlyList<CreatorGraphNode> Nodes => nodes;
        /// <summary>Gets directed graph edges.</summary>
        public IReadOnlyList<CreatorGraphEdge> Edges => edges;

        private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) =>
            new ReadOnlyCollection<T>((values ?? Enumerable.Empty<T>()).ToList());
    }

    /// <summary>Severity of one project validation issue.</summary>
    public enum CreatorProjectValidationSeverity
    {
        /// <summary>The project may load and save, but a runner can require review or restored dependencies.</summary>
        Warning = 0,
        /// <summary>The project cannot safely run or be saved.</summary>
        Error = 1
    }

    /// <summary>One stable project validation diagnostic.</summary>
    public sealed class CreatorProjectValidationIssue
    {
        /// <summary>Creates a validation diagnostic.</summary>
        public CreatorProjectValidationIssue(
            string code,
            string message,
            CreatorProjectValidationSeverity severity,
            string subjectId = "")
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A code is required.", nameof(code));
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A message is required.", nameof(message));
            if (!Enum.IsDefined(typeof(CreatorProjectValidationSeverity), severity)) throw new ArgumentOutOfRangeException(nameof(severity));
            Code = code;
            Message = message;
            Severity = severity;
            SubjectId = subjectId ?? string.Empty;
        }

        /// <summary>Gets the stable diagnostic code.</summary>
        public string Code { get; }
        /// <summary>Gets the user-readable explanation.</summary>
        public string Message { get; }
        /// <summary>Gets diagnostic severity.</summary>
        public CreatorProjectValidationSeverity Severity { get; }
        /// <summary>Gets an optional project-local subject id.</summary>
        public string SubjectId { get; }
    }

    /// <summary>Immutable result of bounded project validation.</summary>
    public sealed class CreatorProjectValidationResult
    {
        private readonly IReadOnlyList<CreatorProjectValidationIssue> issues;

        /// <summary>Creates a validation result.</summary>
        public CreatorProjectValidationResult(IEnumerable<CreatorProjectValidationIssue> issues)
        {
            this.issues = new ReadOnlyCollection<CreatorProjectValidationIssue>(
                (issues ?? throw new ArgumentNullException(nameof(issues))).ToList());
        }

        /// <summary>Gets whether no error diagnostics were produced.</summary>
        public bool IsValid => issues.All(issue => issue.Severity != CreatorProjectValidationSeverity.Error);
        /// <summary>Gets deterministically ordered diagnostics.</summary>
        public IReadOnlyList<CreatorProjectValidationIssue> Issues => issues;
    }

    /// <summary>Compact immutable project index entry.</summary>
    public sealed class CreatorProjectSummary
    {
        /// <summary>Creates project summary metadata.</summary>
        public CreatorProjectSummary(string id, string displayName, CreatorProjectScope scope, DateTimeOffset modifiedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A project id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (!Enum.IsDefined(typeof(CreatorProjectScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            Id = id;
            DisplayName = displayName;
            Scope = scope;
            ModifiedAtUtc = modifiedAtUtc.ToUniversalTime();
        }

        /// <summary>Gets the stable local project id.</summary>
        public string Id { get; }
        /// <summary>Gets the user-facing project name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets its permitted run scope.</summary>
        public CreatorProjectScope Scope { get; }
        /// <summary>Gets its last authored UTC timestamp.</summary>
        public DateTimeOffset ModifiedAtUtc { get; }
    }

    /// <summary>Immutable authoritative local project-library index.</summary>
    public sealed class CreatorProjectLibrarySnapshot
    {
        private readonly IReadOnlyList<CreatorProjectSummary> projects;

        /// <summary>Creates a library snapshot.</summary>
        public CreatorProjectLibrarySnapshot(long revision, IEnumerable<CreatorProjectSummary> projects)
        {
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
            this.projects = new ReadOnlyCollection<CreatorProjectSummary>(
                (projects ?? throw new ArgumentNullException(nameof(projects))).ToList());
        }

        /// <summary>Gets the process-local library revision.</summary>
        public long Revision { get; }
        /// <summary>Gets summaries ordered by name and id.</summary>
        public IReadOnlyList<CreatorProjectSummary> Projects => projects;
    }

    /// <summary>Shared provider-owned local event-project library.</summary>
    public interface ICreatorProjectLibrary
    {
        /// <summary>Validates a project without writing it.</summary>
        CreatorProjectValidationResult Validate(CreatorEventProject project);
        /// <summary>Loads the authoritative library index.</summary>
        Task<OperationResult<CreatorProjectLibrarySnapshot>> ListAsync(CancellationToken cancellationToken = default);
        /// <summary>Loads one project by stable id.</summary>
        Task<OperationResult<CreatorEventProject>> LoadAsync(string projectId, CancellationToken cancellationToken = default);
        /// <summary>Validates and atomically saves a project, then updates the index.</summary>
        Task<OperationResult<CreatorProjectSummary>> SaveAsync(CreatorEventProject project, CancellationToken cancellationToken = default);
        /// <summary>Removes the index entry before deleting the project file.</summary>
        Task<OperationResult<bool>> DeleteAsync(string projectId, CancellationToken cancellationToken = default);
    }
}
