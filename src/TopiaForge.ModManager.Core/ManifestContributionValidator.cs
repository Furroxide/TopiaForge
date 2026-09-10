using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The V6 contribution rules that JSON Schema cannot express, and the ones it can but this side
    /// never sees.
    /// <para>
    /// Nothing under src/ reads <c>topiaforge.mod.schema.json</c>; the schema constrains the Dart
    /// launcher alone. So every rule the schema states is written out here too, and the rules it
    /// cannot state -- ownership, cross-package references, policy coherence -- are only here and in
    /// the Dart mirror. The shared fixtures under tests/fixtures/gamemode-v6 are what keep the two
    /// honest, because nothing else compares them.
    /// </para>
    /// <para>
    /// Every message opens with the path it is about, so a failure names the declaration rather than
    /// the manifest.
    /// </para>
    /// </summary>
    internal static class ManifestContributionValidator
    {
        private const int MaxDeclarationIdLength = 96;
        private const int MinDeclarationIdLength = 4;

        private static readonly HashSet<string> ContentKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            ModWorldContent.BundleKind, ModWorldContent.ProviderKind,
            ModWorldContent.GameSceneKind, ModWorldContent.DiscoveredKind
        };
        private static readonly HashSet<string> SpawnKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            ModSpawnPolicy.AuthoredMarkerKind, ModSpawnPolicy.ProviderDefaultKind
        };
        private static readonly HashSet<string> Policies = new HashSet<string>(StringComparer.Ordinal)
        {
            ModWorldPolicy.FixedPolicy, ModWorldPolicy.ListPolicy, ModWorldPolicy.OpenPolicy
        };

        public static void Validate(ModManifest manifest, List<string> errors)
        {
            var contributions = manifest.Contributions;
            if (contributions == null)
            {
                return;
            }

            try
            {
                ModManifestJson.ValidateContributionModel(contributions);
            }
            catch (InvalidDataException exception)
            {
                errors.Add(exception.Message);
                return;
            }

            // Declaring a launch surface means owning worlds at runtime, and world-service is the
            // capability that discloses it. The schema says this with `contains`; the manager would
            // not know it otherwise.
            if (!manifest.Capabilities.Contains("world-service", StringComparer.Ordinal))
            {
                errors.Add(
                    "contributions requires the world-service capability, because declaring worlds, " +
                    "gamemodes or launch targets means owning world content at runtime.");
            }

            ValidateCount(contributions.Worlds.Count, "contributions.worlds", 64, errors);
            ValidateCount(contributions.Gamemodes.Count, "contributions.gamemodes", 16, errors);
            ValidateCount(contributions.LaunchTargets.Count, "contributions.launchTargets", 64, errors);

            OwnedIds(manifest, errors);
            var ownedWorlds = new HashSet<string>(
                contributions.Worlds.Select(world => world.Id), StringComparer.OrdinalIgnoreCase);
            var discovered = new HashSet<string>(
                contributions.Worlds
                    .Where(world => world.Content != null
                        && string.Equals(world.Content.Kind, ModWorldContent.DiscoveredKind, StringComparison.Ordinal))
                    .Select(world => world.Id),
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < contributions.Worlds.Count; index++)
            {
                ValidateWorld(manifest, contributions.Worlds[index], "contributions.worlds[" + index + "]", errors);
            }

            for (var index = 0; index < contributions.Gamemodes.Count; index++)
            {
                ValidateGamemode(
                    manifest, contributions.Gamemodes[index], "contributions.gamemodes[" + index + "]", errors);
            }

            for (var index = 0; index < contributions.LaunchTargets.Count; index++)
            {
                ValidateLaunchTarget(
                    manifest,
                    contributions.LaunchTargets[index],
                    "contributions.launchTargets[" + index + "]",
                    ownedWorlds,
                    discovered,
                    errors);
            }
        }

        /// <summary>
        /// Declaration ownership and uniqueness, together because both are about the id alone. An id must be
        /// namespaced under the declaring package and strictly longer than that prefix, so a package
        /// can never declare something in a namespace it does not own -- and <c>id == name</c> is not
        /// a declaration, it is the package.
        /// </summary>
        private static HashSet<string> OwnedIds(ModManifest manifest, List<string> errors)
        {
            var prefix = manifest.Id + ".";
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in AllDeclarations(manifest))
            {
                var path = declaration.Path + ".id";
                if (!IsValidDeclarationId(declaration.Id))
                {
                    errors.Add(
                        path + " must be " + MinDeclarationIdLength + "-" + MaxDeclarationIdLength +
                        " characters and use letters, numbers, underscore, dot, or dash.");
                    continue;
                }

                if (!IsLocal(manifest, declaration.Id) || declaration.Id.Length <= prefix.Length)
                {
                    errors.Add(
                        path + " must belong to this package: it has to start with '" +
                        prefix + "', name something beyond it, and not be owned by a longer dependency namespace.");
                    continue;
                }

                if (!owned.Add(declaration.Id))
                {
                    errors.Add(path + " repeats a declaration id already used in this manifest.");
                }
            }

            return owned;
        }

        private static void ValidateWorld(
            ModManifest manifest,
            ModWorldDeclaration world,
            string path,
            List<string> errors)
        {
            ValidateText(world.Name, path + ".name", 1, 128, errors);
            ValidateText(world.Description, path + ".description", 0, 1024, errors);
            ValidateTransitions(world.Transitions, path + ".transitions", required: true, errors);

            var content = world.Content;
            if (content != null)
            {
                ValidateContent(manifest, content, path + ".content", errors);
            }

            var spawn = world.Spawn;
            if (spawn != null)
            {
                if (!SpawnKinds.Contains(spawn.Kind))
                {
                    errors.Add(path + ".spawn.kind must be authored-marker or provider-default.");
                }
                else if (string.Equals(spawn.Kind, ModSpawnPolicy.AuthoredMarkerKind, StringComparison.Ordinal))
                {
                    ValidateText(spawn.MarkerName, path + ".spawn.markerName", 1, 128, errors);
                }
                else if (spawn.MarkerName != null)
                {
                    errors.Add(
                        path + ".spawn.markerName only means something for an authored-marker spawn.");
                }
            }

            // A world either names the gamemodes it consents to or consents to all compatible ones.
            // Saying both leaves the narrower list looking authoritative when it is not.
            if (world.OpenToAnyCompatible == true && world.OpenTo != null)
            {
                errors.Add(
                    path + ".openTo cannot be listed alongside openToAnyCompatible: the list would " +
                    "read as a limit it is not.");
            }

            if (world.OpenTo?.Count > 32)
            {
                errors.Add(path + ".openTo cannot contain more than 32 entries.");
            }

            foreach (var consent in world.OpenTo ?? Enumerable.Empty<string>())
            {
                ValidateReference(manifest, consent, path + ".openTo", errors);
                if (IsValidDeclarationId(consent) && IsLocal(manifest, consent)
                    && !manifest.Contributions!.Gamemodes.Any(mode =>
                        string.Equals(mode.Id, consent, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add(path + ".openTo names a gamemode inside this package that this manifest does not declare.");
                }
            }
        }

        private static void ValidateContent(
            ModManifest manifest,
            ModWorldContent content,
            string path,
            List<string> errors)
        {
            if (!ContentKinds.Contains(content.Kind))
            {
                errors.Add(path + ".kind must be bundle, provider, game-scene, or discovered.");
                return;
            }

            var required = new List<string>();
            switch (content.Kind)
            {
                case ModWorldContent.BundleKind:
                    required.Add("bundle");
                    required.Add("prefab");
                    break;
                case ModWorldContent.GameSceneKind:
                    required.Add("sceneName");
                    break;
                default:
                    required.Add("implementation");
                    break;
            }

            var present = new List<string>();
            if (content.Bundle != null) present.Add("bundle");
            if (content.Prefab != null) present.Add("prefab");
            if (content.SceneName != null) present.Add("sceneName");
            if (content.Implementation != null) present.Add("implementation");

            foreach (var field in required.Where(field => !present.Contains(field)))
            {
                errors.Add(path + " of kind " + content.Kind + " requires " + field + ".");
            }

            foreach (var field in present.Where(field => !required.Contains(field)))
            {
                errors.Add(path + " of kind " + content.Kind + " cannot also carry " + field + ".");
            }

            if (content.Prefab != null) ValidateText(content.Prefab, path + ".prefab", 1, 512, errors);
            if (content.SceneName != null) ValidateText(content.SceneName, path + ".sceneName", 1, 128, errors);
            if (content.Bundle != null && !IsPortablePath(content.Bundle))
            {
                errors.Add(path + ".bundle must be a safe relative path inside the package.");
            }

            if (content.Implementation != null)
            {
                ValidateBinding(manifest, content.Implementation, path + ".implementation", errors);
            }
        }

        private static void ValidateGamemode(
            ModManifest manifest,
            ModGamemodeDeclaration gamemode,
            string path,
            List<string> errors)
        {
            ValidateText(gamemode.Name, path + ".name", 1, 128, errors);
            ValidateText(gamemode.Description, path + ".description", 0, 1024, errors);
            if (gamemode.Implementation != null)
            {
                ValidateBinding(manifest, gamemode.Implementation, path + ".implementation", errors);
            }

            if (gamemode.SceneChangePolicy != null
                && gamemode.SceneChangePolicy != ModGamemodeDeclaration.EndSessionPolicy
                && gamemode.SceneChangePolicy != ModGamemodeDeclaration.KeepControllerPolicy)
            {
                errors.Add(path + ".sceneChangePolicy must be end-session or keep-controller.");
            }

            var requirements = gamemode.WorldRequirements;
            if (requirements == null)
            {
                return;
            }

            if (requirements.Transitions.Count > 0)
            {
                ValidateTransitions(
                    requirements.Transitions, path + ".worldRequirements.transitions", required: false, errors);
            }

            if (requirements.Spawn != null
                && requirements.Spawn != ModSpawnPolicy.AuthoredMarkerKind
                && requirements.Spawn != ModWorldRequirements.AnySpawn)
            {
                errors.Add(path + ".worldRequirements.spawn must be authored-marker or any.");
            }
        }

        private static void ValidateLaunchTarget(
            ModManifest manifest,
            ModLaunchTargetDeclaration target,
            string path,
            ICollection<string> owned,
            ICollection<string> discovered,
            List<string> errors)
        {
            ValidateText(target.Title, path + ".title", 1, 128, errors);
            ValidateText(target.Description, path + ".description", 0, 1024, errors);
            if (target.SortKey != null && (target.SortKey < 0 || target.SortKey > 999))
            {
                errors.Add(path + ".sortKey must be between 0 and 999.");
            }

            if (target.Transition != null
                && target.Transition != ModLaunchTargetDeclaration.AutoTransition
                && target.Transition != ModLaunchTargetDeclaration.PlayerChoiceTransition
                && target.Transition != ModTransitions.SceneReplacement
                && target.Transition != ModTransitions.AdditiveArena)
            {
                errors.Add(
                    path + ".transition must be auto, player-choice, scene-replacement, or additive-arena.");
            }

            ValidateReference(manifest, target.Gamemode, path + ".gamemode", errors);
            if (IsLocal(manifest, target.Gamemode)
                && !manifest.Contributions!.Gamemodes.Any(item =>
                    string.Equals(item.Id, target.Gamemode, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    path + ".gamemode names an id inside this package that this manifest does not declare.");
            }

            var policy = target.World;
            if (policy == null)
            {
                return;
            }

            if (!Policies.Contains(policy.Policy))
            {
                errors.Add(path + ".world.policy must be fixed, list, or open.");
            }

            ValidateWorldReference(manifest, policy.Default, path + ".world.default", owned, discovered, errors);
            if (policy.Allow.Count > 64)
            {
                errors.Add(path + ".world.allow cannot contain more than 64 entries.");
            }

            foreach (var allowed in policy.Allow)
            {
                ValidateWorldReference(manifest, allowed, path + ".world.allow", owned, discovered, errors);
            }

            if (string.Equals(policy.Policy, ModWorldPolicy.ListPolicy, StringComparison.Ordinal))
            {
                if (policy.Allow.Count == 0)
                {
                    errors.Add(path + ".world.allow is required by the list policy.");
                }
                else if (!policy.Allow.Contains(policy.Default, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(
                        path + ".world.default must be a member of allow, or the default is a world " +
                        "the policy does not admit.");
                }
            }
            else if (policy.Allow.Count > 0)
            {
                errors.Add(
                    path + ".world.allow only means something for the list policy; " + policy.Policy +
                    " admits its default" +
                    (string.Equals(policy.Policy, ModWorldPolicy.OpenPolicy, StringComparison.Ordinal)
                        ? " plus any consenting world."
                        : " and nothing else."));
            }

            if (string.Equals(policy.Policy, ModWorldPolicy.FixedPolicy, StringComparison.Ordinal)
                && policy.AllowPlayerOverride == true)
            {
                errors.Add(
                    path + ".world.allowPlayerOverride contradicts the fixed policy, which admits one world.");
            }

            ValidateLocalPairing(manifest, target, path, errors);
        }

        /// <summary>
        /// Pairing compatibility, only where it can actually be checked. When the world and the gamemode are declared
        /// in different packages this manifest cannot see both sides, so compatibility is the
        /// resolver's job; checking it here would pass every first-party pairing without looking.
        /// </summary>
        private static void ValidateLocalPairing(
            ModManifest manifest,
            ModLaunchTargetDeclaration target,
            string path,
            List<string> errors)
        {
            if (target.World == null || !IsLocal(manifest, target.World.Default)
                || !IsLocal(manifest, target.Gamemode))
            {
                return;
            }

            var contributions = manifest.Contributions!;
            var world = contributions.Worlds.FirstOrDefault(item =>
                string.Equals(item.Id, target.World?.Default, StringComparison.OrdinalIgnoreCase));
            var gamemode = contributions.Gamemodes.FirstOrDefault(item =>
                string.Equals(item.Id, target.Gamemode, StringComparison.OrdinalIgnoreCase));
            if (world == null || gamemode == null)
            {
                return;
            }

            var requirements = gamemode.WorldRequirements;
            if (requirements != null
                && string.Equals(requirements.Spawn, ModSpawnPolicy.AuthoredMarkerKind, StringComparison.Ordinal)
                && world.Spawn != null
                && !string.Equals(world.Spawn.Kind, ModSpawnPolicy.AuthoredMarkerKind, StringComparison.Ordinal))
            {
                errors.Add(
                    path + ".world.default names a world with a provider-default spawn, but " +
                    gamemode.Id + " requires an authored marker.");
            }

            var offered = requirements == null || requirements.Transitions.Count == 0
                ? world.Transitions
                : world.Transitions.Where(requirements.Transitions.Contains).ToList();
            if (offered.Count == 0)
            {
                errors.Add(
                    path + ".world.default names a world that shares no transition with " + gamemode.Id + ".");
            }
            else if (target.Transition != null
                && target.Transition != ModLaunchTargetDeclaration.AutoTransition
                && target.Transition != ModLaunchTargetDeclaration.PlayerChoiceTransition
                && !offered.Contains(target.Transition, StringComparer.Ordinal))
            {
                errors.Add(
                    path + ".transition is not one the default world and " + gamemode.Id + " both offer.");
            }
        }

        /// <summary>
        /// Static-world references. A discovered family is a prefix, not a world: nothing under it exists
        /// until the game has run and reported it, so a policy that names one is naming content that
        /// may never appear.
        /// </summary>
        private static void ValidateWorldReference(
            ModManifest manifest,
            string reference,
            string path,
            ICollection<string> owned,
            ICollection<string> discovered,
            List<string> errors)
        {
            ValidateReference(manifest, reference, path, errors);
            if (reference.Length == 0 || !IsLocal(manifest, reference))
            {
                return;
            }

            foreach (var family in discovered)
            {
                if (string.Equals(reference, family, StringComparison.OrdinalIgnoreCase)
                    || reference.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        path + " names " + family + ", a discovered world family. Its instances only " +
                        "exist once the game has reported them, so a policy cannot name one.");
                    return;
                }
            }

            if (IsLocal(manifest, reference)
                && !owned.Contains(reference)
                && IsValidDeclarationId(reference))
            {
                errors.Add(path + " names an id inside this package that this manifest does not declare.");
            }
        }

        /// <summary>
        /// Required references and ownership. A reference this package does not own must be owned by a package it
        /// requires -- optionalDependencies never qualifies, because a reference that resolves only
        /// sometimes is a launch that fails only sometimes. Ownership goes to the longest matching
        /// name, so a package cannot squat inside a longer-named one's namespace.
        /// </summary>
        private static void ValidateReference(
            ModManifest manifest,
            string reference,
            string path,
            List<string> errors)
        {
            if (reference.Length == 0)
            {
                errors.Add(path + " is required.");
                return;
            }

            if (!IsValidDeclarationId(reference))
            {
                errors.Add(
                    path + " must be " + MinDeclarationIdLength + "-" + MaxDeclarationIdLength +
                    " characters and use letters, numbers, underscore, dot, or dash.");
                return;
            }

            var owners = ReferenceOwners(manifest, reference);
            if (owners.Count > 1 && owners[0].Id.Length == owners[1].Id.Length)
            {
                errors.Add(path + " names " + reference + ", which two packages both claim to own.");
                return;
            }

            if (owners.Count > 0 && (owners[0].Local || owners[0].Required))
            {
                return;
            }

            errors.Add(
                path + " names " + reference + ", which no required dependency owns" +
                (owners.Count > 0
                    ? ". An optional dependency cannot own a reference: a launch that resolves only when it happens to be installed is a launch that fails without warning."
                    : "."));
        }

        private static bool IsLocal(ModManifest manifest, string reference)
        {
            var owners = ReferenceOwners(manifest, reference);
            return owners.Count > 0 && owners[0].Local
                && (owners.Count == 1 || owners[1].Id.Length < owners[0].Id.Length);
        }

        private static List<ReferenceOwner> ReferenceOwners(ModManifest manifest, string reference)
        {
            var owners = new List<ReferenceOwner>();
            if (reference.StartsWith(manifest.Id + ".", StringComparison.OrdinalIgnoreCase))
            {
                owners.Add(new ReferenceOwner(manifest.Id, local: true, required: false));
            }

            foreach (var id in manifest.Dependencies.Keys)
            {
                if (reference.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
                    owners.Add(new ReferenceOwner(id, local: false, required: true));
            }

            foreach (var id in manifest.OptionalDependencies.Keys)
            {
                if (reference.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
                    owners.Add(new ReferenceOwner(id, local: false, required: false));
            }

            return owners.OrderByDescending(owner => owner.Id.Length)
                .ThenBy(owner => owner.Id, StringComparer.Ordinal).ToList();
        }

        private sealed class ReferenceOwner
        {
            public ReferenceOwner(string id, bool local, bool required)
            {
                Id = id;
                Local = local;
                Required = required;
            }

            public string Id { get; }
            public bool Local { get; }
            public bool Required { get; }
        }

        private static void ValidateBinding(
            ModManifest manifest,
            ModImplementationBinding binding,
            string path,
            List<string> errors)
        {
            if (!IsValidTypeName(binding.Type))
            {
                errors.Add(
                    path + ".type must be a namespace-qualified CLR type name, with no assembly " +
                    "qualifier and no nested-type syntax.");
            }

            if (binding.Assembly == null)
            {
                return;
            }

            if (!IsPortablePath(binding.Assembly)
                || !binding.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(path + ".assembly must be a safe portable .dll path inside the package.");
                return;
            }

            // Binding integrity. A binding may only point at bytes the installer verified, so naming an assembly the
            // manifest does not hash would let a declaration bind to something never checked.
            if (!manifest.Hashes.Keys.Any(key => string.Equals(key, binding.Assembly, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    path + ".assembly must also appear in hashes, so a declaration can only bind to " +
                    "bytes the installer verified.");
            }
        }

        private static void ValidateTransitions(
            IReadOnlyList<string> transitions,
            string path,
            bool required,
            List<string> errors)
        {
            if (transitions.Count == 0)
            {
                if (required)
                {
                    errors.Add(path + " must name at least one transition.");
                }

                return;
            }

            if (transitions.Count > 2)
            {
                errors.Add(path + " cannot contain more than 2 entries.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transition in transitions)
            {
                if (transition != ModTransitions.SceneReplacement && transition != ModTransitions.AdditiveArena)
                {
                    errors.Add(path + " must contain only scene-replacement or additive-arena.");
                }
                else if (!seen.Add(transition))
                {
                    errors.Add(path + " repeats " + transition + ".");
                }
            }
        }

        private static void ValidateCount(int count, string path, int maximum, List<string> errors)
        {
            if (count > maximum)
            {
                errors.Add(path + " cannot contain more than " + maximum + " entries.");
            }
        }

        private static void ValidateText(string? value, string path, int minimum, int maximum, List<string> errors)
        {
            var length = UnicodeScalarLength(value);
            if (length < minimum || length > maximum)
            {
                errors.Add(path + " must contain between " + minimum + " and " + maximum + " characters.");
            }
        }

        private static bool IsPortablePath(string path) =>
            PortablePackagePath.TryValidate(path, out _, out _, out _);

        internal static bool IsValidDeclarationId(string id) =>
            HasDeclarationIdGrammar(id) && !ManifestValidator.IsRetiredEcosystemId(id);

        internal static bool HasDeclarationIdGrammar(string id)
        {
            if (string.IsNullOrEmpty(id)
                || id.Length < MinDeclarationIdLength
                || id.Length > MaxDeclarationIdLength)
            {
                return false;
            }

            if (!IsAsciiLetterOrDigit(id[0]))
            {
                return false;
            }

            foreach (var character in id)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '_' && character != '.' && character != '-')
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsValidTypeName(string type)
        {
            if (string.IsNullOrEmpty(type) || type.Length < 3 || type.Length > 512)
            {
                return false;
            }

            var segments = type.Split('.');
            if (segments.Length < 2)
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || (!IsAsciiLetter(segment[0]) && segment[0] != '_'))
                {
                    return false;
                }

                if (segment.Any(character => !IsAsciiLetterOrDigit(character) && character != '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private static bool IsAsciiLetterOrDigit(char value) =>
            IsAsciiLetter(value) || (value >= '0' && value <= '9');

        internal static int UnicodeScalarLength(string? value)
        {
            if (value == null) return 0;
            var count = 0;
            for (var index = 0; index < value.Length; index++, count++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1])) index++;
            }

            return count;
        }

        private static IEnumerable<Declaration> AllDeclarations(ModManifest manifest)
        {
            var contributions = manifest.Contributions!;
            for (var index = 0; index < contributions.Worlds.Count; index++)
            {
                yield return new Declaration(
                    contributions.Worlds[index].Id, "contributions.worlds[" + index + "]");
            }

            for (var index = 0; index < contributions.Gamemodes.Count; index++)
            {
                yield return new Declaration(
                    contributions.Gamemodes[index].Id, "contributions.gamemodes[" + index + "]");
            }

            for (var index = 0; index < contributions.LaunchTargets.Count; index++)
            {
                yield return new Declaration(
                    contributions.LaunchTargets[index].Id, "contributions.launchTargets[" + index + "]");
            }
        }

        private sealed class Declaration
        {
            public Declaration(string id, string path)
            {
                Id = id;
                Path = path;
            }

            public string Id { get; }

            public string Path { get; }
        }
    }
}
