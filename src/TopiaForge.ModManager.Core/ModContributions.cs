using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// What a V6 package contributes to the launch surface: the worlds it owns, the gamemodes it
    /// implements, and the targets a player can pick.
    /// <para>
    /// Wrapped in one object rather than declared as top-level <c>worlds</c>/<c>gamemodes</c> keys
    /// because <c>gamemodes</c> is already taken: it is a live retired-field sentinel that both
    /// readers reject on sight (see <see cref="ModManifest"/>'s unsupported-field list). One extra
    /// object level sidesteps the collision and costs nothing else.
    /// </para>
    /// <para>
    /// V5 could express none of this. A <c>worldGamemodes</c> entry was an id, a name and a
    /// description -- no implementation owner, no world, no launch identity -- so the manifest and the
    /// code that actually ran could disagree and nothing noticed.
    /// </para>
    /// </summary>
    [DataContract]
    public sealed class ModContributions
    {
        public List<ModWorldDeclaration> Worlds { get; set; } = new List<ModWorldDeclaration>();

        [DataMember(Name = "worlds", EmitDefaultValue = false)]
        private List<ModWorldDeclaration>? SerializedWorlds
        {
            get => Worlds?.Count > 0 ? Worlds : null;
            set => Worlds = value ?? new List<ModWorldDeclaration>();
        }

        public List<ModGamemodeDeclaration> Gamemodes { get; set; } = new List<ModGamemodeDeclaration>();

        [DataMember(Name = "gamemodes", EmitDefaultValue = false)]
        private List<ModGamemodeDeclaration>? SerializedGamemodes
        {
            get => Gamemodes?.Count > 0 ? Gamemodes : null;
            set => Gamemodes = value ?? new List<ModGamemodeDeclaration>();
        }

        public List<ModLaunchTargetDeclaration> LaunchTargets { get; set; } = new List<ModLaunchTargetDeclaration>();

        [DataMember(Name = "launchTargets", EmitDefaultValue = false)]
        private List<ModLaunchTargetDeclaration>? SerializedLaunchTargets
        {
            get => LaunchTargets?.Count > 0 ? LaunchTargets : null;
            set => LaunchTargets = value ?? new List<ModLaunchTargetDeclaration>();
        }
    }

    /// <summary>
    /// Names the type that implements a declaration, and the assembly it lives in.
    /// <para>
    /// An object rather than a bare type string so <see cref="Assembly"/> can exist at all: a bare
    /// string would weld every gamemode to the manifest's <c>entryAssembly</c> inside a contract that
    /// is closed to new fields. When present the assembly must also be a key of <c>hashes</c>, so a
    /// binding can only point at bytes the installer verified.
    /// </para>
    /// </summary>
    [DataContract]
    public sealed class ModImplementationBinding
    {
        /// <summary>Null means this manifest's <c>entryAssembly</c>.</summary>
        [DataMember(Name = "assembly", EmitDefaultValue = false)]
        public string? Assembly { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; } = string.Empty;
    }

    /// <summary>How a world's content is obtained.</summary>
    [DataContract]
    public sealed class ModWorldContent
    {
        public const string BundleKind = "bundle";
        public const string ProviderKind = "provider";
        public const string GameSceneKind = "game-scene";

        /// <summary>
        /// A family of worlds enumerated at runtime rather than declared one by one. The declaration's
        /// id is the family prefix; observed instances are <c>&lt;id&gt;.&lt;slug&gt;</c>.
        /// Neither a family nor an instance may appear in a static default or allow list.
        /// A player override may select a concrete observed instance when the target policy permits it.
        /// </summary>
        public const string DiscoveredKind = "discovered";

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Name = "bundle", EmitDefaultValue = false)]
        public string? Bundle { get; set; }

        [DataMember(Name = "prefab", EmitDefaultValue = false)]
        public string? Prefab { get; set; }

        [DataMember(Name = "implementation", EmitDefaultValue = false)]
        public ModImplementationBinding? Implementation { get; set; }

        [DataMember(Name = "sceneName", EmitDefaultValue = false)]
        public string? SceneName { get; set; }
    }

    /// <summary>
    /// Selects an authored marker or the provider's default spawn. World loading must resolve
    /// and apply this spawn after scene, content, and player readiness before gameplay starts.
    /// </summary>
    [DataContract]
    public sealed class ModSpawnPolicy
    {
        public const string AuthoredMarkerKind = "authored-marker";
        public const string ProviderDefaultKind = "provider-default";

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Name = "markerName", EmitDefaultValue = false)]
        public string? MarkerName { get; set; }
    }

    /// <summary>One world this package owns.</summary>
    [DataContract]
    public sealed class ModWorldDeclaration
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string? Description { get; set; }

        [DataMember(Name = "content", EmitDefaultValue = false)]
        public ModWorldContent? Content { get; set; }

        [DataMember(Name = "transitions")]
        public List<string> Transitions { get; set; } = new List<string>();

        [DataMember(Name = "spawn", EmitDefaultValue = false)]
        public ModSpawnPolicy? Spawn { get; set; }

        /// <summary>
        /// Gamemodes this world agrees to be paired with by an <c>open</c> policy. Consent is scoped
        /// to that one policy on purpose: requiring it for every pairing would make a world's package
        /// depend on the gamemodes that use it, and the first-party graph already runs the other way.
        /// </summary>
        [DataMember(Name = "openTo", EmitDefaultValue = false)]
        public List<string>? OpenTo { get; set; }

        /// <summary>Null means absent, which is not the same as an explicit false.</summary>
        [DataMember(Name = "openToAnyCompatible", EmitDefaultValue = false)]
        public bool? OpenToAnyCompatible { get; set; }
    }

    /// <summary>
    /// What a gamemode needs of a world. Absent entirely means no requirement; an empty object would
    /// be indistinguishable from a requirement the author forgot to fill in, so it is rejected.
    /// </summary>
    [DataContract]
    public sealed class ModWorldRequirements
    {
        public const string AnySpawn = "any";

        public List<string> Transitions { get; set; } = new List<string>();

        [DataMember(Name = "transitions", EmitDefaultValue = false)]
        private List<string>? SerializedTransitions
        {
            get => Transitions?.Count > 0 ? Transitions : null;
            set => Transitions = value ?? new List<string>();
        }

        [DataMember(Name = "spawn", EmitDefaultValue = false)]
        public string? Spawn { get; set; }
    }

    /// <summary>One gamemode this package implements.</summary>
    [DataContract]
    public sealed class ModGamemodeDeclaration
    {
        /// <summary>A scene change ends the session.</summary>
        public const string EndSessionPolicy = "end-session";

        /// <summary>A scene change leaves the running controller alone. The default.</summary>
        public const string KeepControllerPolicy = "keep-controller";

        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string? Description { get; set; }

        [DataMember(Name = "implementation", EmitDefaultValue = false)]
        public ModImplementationBinding? Implementation { get; set; }

        [DataMember(Name = "worldRequirements", EmitDefaultValue = false)]
        public ModWorldRequirements? WorldRequirements { get; set; }

        [DataMember(Name = "sceneChangePolicy", EmitDefaultValue = false)]
        public string? SceneChangePolicy { get; set; }
    }

    /// <summary>Which worlds a launch target admits.</summary>
    [DataContract]
    public sealed class ModWorldPolicy
    {
        /// <summary>Only <see cref="Default"/>.</summary>
        public const string FixedPolicy = "fixed";

        /// <summary>Members of <see cref="Allow"/>, including <see cref="Default"/>, with no world-side consent.</summary>
        public const string ListPolicy = "list";

        /// <summary>
        /// The static <see cref="Default"/> and any permitted player-selected profile world must
        /// meet the gamemode's requirements and consent to the pairing.
        /// </summary>
        public const string OpenPolicy = "open";

        [DataMember(Name = "policy")]
        public string Policy { get; set; } = string.Empty;

        [DataMember(Name = "default")]
        public string Default { get; set; } = string.Empty;

        public List<string> Allow { get; set; } = new List<string>();

        [DataMember(Name = "allow", EmitDefaultValue = false)]
        private List<string>? SerializedAllow
        {
            get => Allow?.Count > 0 ? Allow : null;
            set => Allow = value ?? new List<string>();
        }

        /// <summary>Null means absent, which is not the same as an explicit false.</summary>
        [DataMember(Name = "allowPlayerOverride", EmitDefaultValue = false)]
        public bool? AllowPlayerOverride { get; set; }
    }

    /// <summary>
    /// What the player picks. Menus, Home, Setup and the CLI all select one of these, so its identity
    /// is user-facing and outlives any one menu.
    /// </summary>
    [DataContract]
    public sealed class ModLaunchTargetDeclaration
    {
        /// <summary>
        /// Take the highest-precedence transition both sides allow. Scene replacement outranks the
        /// additive arena, and the precedence is fixed so a world offering both is never ambiguous.
        /// </summary>
        public const string AutoTransition = "auto";

        /// <summary>Offer the whole intersection to the player instead of choosing.</summary>
        public const string PlayerChoiceTransition = "player-choice";

        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string? Description { get; set; }

        /// <summary>Null means absent, which is not the same as an explicit 0.</summary>
        public int? SortKey { get; set; }

        // JSON Schema accepts integral JSON numbers such as 1.0 and 1e0. The raw reader
        // checks integrality and range before this serializer bridge converts the value.
        [DataMember(Name = "sortKey", EmitDefaultValue = false)]
        private double? SerializedSortKey
        {
            get => SortKey;
            set => SortKey = value.HasValue ? checked((int)value.Value) : (int?)null;
        }

        [DataMember(Name = "gamemode")]
        public string Gamemode { get; set; } = string.Empty;

        [DataMember(Name = "world", EmitDefaultValue = false)]
        public ModWorldPolicy? World { get; set; }

        [DataMember(Name = "transition", EmitDefaultValue = false)]
        public string? Transition { get; set; }
    }

    /// <summary>The two ways a world can be entered.</summary>
    public static class ModTransitions
    {
        public const string SceneReplacement = "scene-replacement";
        public const string AdditiveArena = "additive-arena";

        /// <summary>
        /// Most-preferred first. <c>auto</c> takes the first member of this order that both the world
        /// and the gamemode allow, which is what makes the choice deterministic for a world that
        /// supports both -- and one ships today.
        /// </summary>
        public static readonly string[] ByPrecedence = { SceneReplacement, AdditiveArena };
    }
}
