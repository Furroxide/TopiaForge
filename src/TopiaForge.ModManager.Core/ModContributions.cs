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
        [DataMember(Name = "worlds", EmitDefaultValue = false)]
        public List<ModWorldDeclaration> Worlds { get; set; } = new List<ModWorldDeclaration>();

        [DataMember(Name = "gamemodes", EmitDefaultValue = false)]
        public List<ModGamemodeDeclaration> Gamemodes { get; set; } = new List<ModGamemodeDeclaration>();

        [DataMember(Name = "launchTargets", EmitDefaultValue = false)]
        public List<ModLaunchTargetDeclaration> LaunchTargets { get; set; } =
            new List<ModLaunchTargetDeclaration>();
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
        /// <summary>Empty means this manifest's <c>entryAssembly</c>.</summary>
        [DataMember(Name = "assembly")]
        public string Assembly { get; set; } = string.Empty;

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
        /// id is the family prefix; instances are <c>&lt;id&gt;.&lt;slug&gt;</c> and are never
        /// launchable on their own, so a stored selection cannot name content that has never existed.
        /// </summary>
        public const string DiscoveredKind = "discovered";

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Name = "bundle")]
        public string Bundle { get; set; } = string.Empty;

        [DataMember(Name = "prefab")]
        public string Prefab { get; set; } = string.Empty;

        [DataMember(Name = "implementation", EmitDefaultValue = false)]
        public ModImplementationBinding? Implementation { get; set; }

        [DataMember(Name = "sceneName")]
        public string SceneName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Where the player starts. Deliberately not a transform: V5 declares no numeric fields at all,
    /// and a spawn point drifting in the last bits is the bug nobody attributes to the manifest.
    /// </summary>
    [DataContract]
    public sealed class ModSpawnPolicy
    {
        public const string AuthoredMarkerKind = "authored-marker";
        public const string ProviderDefaultKind = "provider-default";

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Name = "markerName")]
        public string MarkerName { get; set; } = string.Empty;
    }

    /// <summary>One world this package owns.</summary>
    [DataContract]
    public sealed class ModWorldDeclaration
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;

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
        [DataMember(Name = "openTo")]
        public List<string> OpenTo { get; set; } = new List<string>();

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

        [DataMember(Name = "transitions")]
        public List<string> Transitions { get; set; } = new List<string>();

        [DataMember(Name = "spawn")]
        public string Spawn { get; set; } = string.Empty;
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

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;

        [DataMember(Name = "implementation", EmitDefaultValue = false)]
        public ModImplementationBinding? Implementation { get; set; }

        [DataMember(Name = "worldRequirements", EmitDefaultValue = false)]
        public ModWorldRequirements? WorldRequirements { get; set; }

        [DataMember(Name = "sceneChangePolicy")]
        public string SceneChangePolicy { get; set; } = string.Empty;
    }

    /// <summary>Which worlds a launch target admits.</summary>
    [DataContract]
    public sealed class ModWorldPolicy
    {
        /// <summary>Only <see cref="Default"/>.</summary>
        public const string FixedPolicy = "fixed";

        /// <summary><see cref="Default"/> plus <see cref="Allow"/>, with no world-side consent.</summary>
        public const string ListPolicy = "list";

        /// <summary>
        /// <see cref="Default"/> plus any profile world that meets the gamemode's requirements and
        /// consents to the pairing itself.
        /// </summary>
        public const string OpenPolicy = "open";

        [DataMember(Name = "policy")]
        public string Policy { get; set; } = string.Empty;

        [DataMember(Name = "default")]
        public string Default { get; set; } = string.Empty;

        [DataMember(Name = "allow")]
        public List<string> Allow { get; set; } = new List<string>();

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

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>Null means absent, which is not the same as an explicit 0.</summary>
        [DataMember(Name = "sortKey", EmitDefaultValue = false)]
        public int? SortKey { get; set; }

        [DataMember(Name = "gamemode")]
        public string Gamemode { get; set; } = string.Empty;

        [DataMember(Name = "world", EmitDefaultValue = false)]
        public ModWorldPolicy? World { get; set; }

        [DataMember(Name = "transition")]
        public string Transition { get; set; } = string.Empty;
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
