using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Structural reading for the V6 <c>contributions</c> object: closed key sets and required keys,
    /// enforced by hand at every nesting level.
    /// <para>
    /// By hand because this side has no JSON Schema validator -- nothing under src/ reads
    /// <c>topiaforge.mod.schema.json</c>, so the schema constrains the Dart launcher alone. Anything
    /// the schema says that is not also written out here is a rule the manager does not have.
    /// </para>
    /// </summary>
    public static partial class ModManifestJson
    {
        private static readonly HashSet<string> ContributionFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "worlds", "gamemodes", "launchTargets"
        };
        private static readonly HashSet<string> WorldFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "description", "content", "transitions", "spawn", "openTo", "openToAnyCompatible"
        };
        private static readonly HashSet<string> WorldContentFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind", "bundle", "prefab", "implementation", "sceneName"
        };
        private static readonly HashSet<string> SpawnFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind", "markerName"
        };
        private static readonly HashSet<string> ImplementationFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "assembly", "type"
        };
        private static readonly HashSet<string> GamemodeDeclarationFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "description", "implementation", "worldRequirements", "sceneChangePolicy"
        };
        private static readonly HashSet<string> WorldRequirementFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "transitions", "spawn"
        };
        private static readonly HashSet<string> LaunchTargetFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "title", "description", "sortKey", "gamemode", "world", "transition"
        };
        private static readonly HashSet<string> WorldPolicyFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "policy", "default", "allow", "allowPlayerOverride"
        };

        private static void ValidateContributionsObject(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties)
        {
            var raw = RequireRawProperty(properties, "contributions");
            ValidateClosedObject(
                "contributions",
                raw,
                ContributionFields,
                Array.Empty<string>(),
                requireAtLeastOne: true);

            var contributions = ReadObject("contributions", raw);
            foreach (var world in ArrayItems(contributions, "contributions.worlds", "worlds"))
            {
                ValidateClosedObject(
                    world.Path,
                    world.Raw,
                    WorldFields,
                    new[] { "id", "name", "content", "transitions", "spawn" },
                    requireAtLeastOne: false);
                var fields = ReadObject(world.Path, world.Raw);
                ValidateNestedObject(
                    fields,
                    world.Path + ".content",
                    "content",
                    WorldContentFields,
                    new[] { "kind" },
                    requireAtLeastOne: false);
                ValidateNestedObject(
                    fields,
                    world.Path + ".spawn",
                    "spawn",
                    SpawnFields,
                    new[] { "kind" },
                    requireAtLeastOne: false);

                var content = fields.FirstOrDefault(field => field.Name == "content");
                if (content != null)
                {
                    ValidateNestedObject(
                        ReadObject(world.Path + ".content", content.RawValue),
                        world.Path + ".content.implementation",
                        "implementation",
                        ImplementationFields,
                        new[] { "type" },
                        requireAtLeastOne: false);
                }
            }

            foreach (var gamemode in ArrayItems(contributions, "contributions.gamemodes", "gamemodes"))
            {
                ValidateClosedObject(
                    gamemode.Path,
                    gamemode.Raw,
                    GamemodeDeclarationFields,
                    new[] { "id", "name", "implementation" },
                    requireAtLeastOne: false);
                var fields = ReadObject(gamemode.Path, gamemode.Raw);
                ValidateNestedObject(
                    fields,
                    gamemode.Path + ".implementation",
                    "implementation",
                    ImplementationFields,
                    new[] { "type" },
                    requireAtLeastOne: false);

                // An empty worldRequirements is rejected rather than read as "no requirement",
                // because absent already means that and the two must stay distinguishable.
                ValidateNestedObject(
                    fields,
                    gamemode.Path + ".worldRequirements",
                    "worldRequirements",
                    WorldRequirementFields,
                    Array.Empty<string>(),
                    requireAtLeastOne: true);
            }

            foreach (var target in ArrayItems(contributions, "contributions.launchTargets", "launchTargets"))
            {
                ValidateClosedObject(
                    target.Path,
                    target.Raw,
                    LaunchTargetFields,
                    new[] { "id", "title", "gamemode", "world" },
                    requireAtLeastOne: false);
                ValidateNestedObject(
                    ReadObject(target.Path, target.Raw),
                    target.Path + ".world",
                    "world",
                    WorldPolicyFields,
                    new[] { "policy", "default" },
                    requireAtLeastOne: false);
            }
        }

        private static void ValidateNestedObject(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> parent,
            string path,
            string name,
            ISet<string> allowed,
            IReadOnlyList<string> required,
            bool requireAtLeastOne)
        {
            var property = parent.FirstOrDefault(item => item.Name == name);
            if (property == null)
            {
                return;
            }

            ValidateClosedObject(path, property.RawValue, allowed, required, requireAtLeastOne);
        }

        private static IReadOnlyList<JsonObjectMerge.RawJsonProperty> ReadObject(string path, string rawJson)
        {
            try
            {
                return JsonObjectMerge.ReadProperties(rawJson);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field '" + path + "' must be an object.", exception);
            }
        }

        private static IEnumerable<RawArrayItem> ArrayItems(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> parent,
            string path,
            string name)
        {
            var property = parent.FirstOrDefault(item => item.Name == name);
            if (property == null)
            {
                yield break;
            }

            IReadOnlyList<string> values;
            try
            {
                values = JsonObjectMerge.ReadArrayValues(property.RawValue);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field '" + path + "' must be an array.", exception);
            }

            // An empty declaration array is a mistake, not a contribution: the schema requires at
            // least one item, and a package that declares "worlds: []" meant to declare a world.
            if (values.Count == 0)
            {
                throw new InvalidDataException(
                    "Manifest field '" + path + "' must declare at least one entry or be omitted.");
            }

            for (var index = 0; index < values.Count; index++)
            {
                yield return new RawArrayItem(path + "[" + index + "]", values[index]);
            }
        }

        /// <summary>
        /// DataContractJsonSerializer builds instances with GetUninitializedObject, so every absent
        /// collection and string arrives null rather than at its initializer. Nullable value types are
        /// left alone on purpose: null is the answer there, and it is what distinguishes an absent
        /// flag from an explicit false.
        /// </summary>
        private static void NormalizeContributions(ModManifest manifest)
        {
            var contributions = manifest.Contributions;
            if (contributions == null)
            {
                return;
            }

            contributions.Worlds = contributions.Worlds ?? new List<ModWorldDeclaration>();
            contributions.Gamemodes = contributions.Gamemodes ?? new List<ModGamemodeDeclaration>();
            contributions.LaunchTargets =
                contributions.LaunchTargets ?? new List<ModLaunchTargetDeclaration>();

            foreach (var world in contributions.Worlds.Where(world => world != null))
            {
                world.Id = world.Id ?? string.Empty;
                world.Name = world.Name ?? string.Empty;
                world.Description = world.Description ?? string.Empty;
                world.Transitions = world.Transitions ?? new List<string>();
                world.OpenTo = world.OpenTo ?? new List<string>();
                if (world.Content != null)
                {
                    world.Content.Kind = world.Content.Kind ?? string.Empty;
                    world.Content.Bundle = world.Content.Bundle ?? string.Empty;
                    world.Content.Prefab = world.Content.Prefab ?? string.Empty;
                    world.Content.SceneName = world.Content.SceneName ?? string.Empty;
                    NormalizeBinding(world.Content.Implementation);
                }

                if (world.Spawn != null)
                {
                    world.Spawn.Kind = world.Spawn.Kind ?? string.Empty;
                    world.Spawn.MarkerName = world.Spawn.MarkerName ?? string.Empty;
                }
            }

            foreach (var gamemode in contributions.Gamemodes.Where(gamemode => gamemode != null))
            {
                gamemode.Id = gamemode.Id ?? string.Empty;
                gamemode.Name = gamemode.Name ?? string.Empty;
                gamemode.Description = gamemode.Description ?? string.Empty;
                gamemode.SceneChangePolicy = gamemode.SceneChangePolicy ?? string.Empty;
                NormalizeBinding(gamemode.Implementation);
                if (gamemode.WorldRequirements != null)
                {
                    gamemode.WorldRequirements.Transitions =
                        gamemode.WorldRequirements.Transitions ?? new List<string>();
                    gamemode.WorldRequirements.Spawn = gamemode.WorldRequirements.Spawn ?? string.Empty;
                }
            }

            foreach (var target in contributions.LaunchTargets.Where(target => target != null))
            {
                target.Id = target.Id ?? string.Empty;
                target.Title = target.Title ?? string.Empty;
                target.Description = target.Description ?? string.Empty;
                target.Gamemode = target.Gamemode ?? string.Empty;
                target.Transition = target.Transition ?? string.Empty;
                if (target.World != null)
                {
                    target.World.Policy = target.World.Policy ?? string.Empty;
                    target.World.Default = target.World.Default ?? string.Empty;
                    target.World.Allow = target.World.Allow ?? new List<string>();
                }
            }
        }

        private static void NormalizeBinding(ModImplementationBinding? binding)
        {
            if (binding == null)
            {
                return;
            }

            binding.Assembly = binding.Assembly ?? string.Empty;
            binding.Type = binding.Type ?? string.Empty;
        }

        private sealed class RawArrayItem
        {
            public RawArrayItem(string path, string raw)
            {
                Path = path;
                Raw = raw;
            }

            public string Path { get; }

            public string Raw { get; }
        }
    }
}
