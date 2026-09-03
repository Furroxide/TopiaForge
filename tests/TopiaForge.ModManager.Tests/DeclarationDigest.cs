using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A flat, line-per-declaration rendering of what a reader actually parsed out of a V6 manifest.
    /// <para>
    /// The existing shared-manifest corpus compares only the accept/reject verdict, so two readers can
    /// agree a manifest is valid while disagreeing about what it said. That is not a hypothetical: an
    /// absent <c>openToAnyCompatible</c> and an explicit <c>false</c> mean different things, and
    /// DataContractJsonSerializer builds instances with GetUninitializedObject, so a non-nullable
    /// property would collapse the two with nothing to show for it.
    /// </para>
    /// <para>
    /// So absence is spelled out as the literal <c>absent</c> and an empty value as <c>-</c>, and the
    /// Dart runner renders the identical strings in
    /// <c>packages/launcher_domain/test/gamemode_contract_conformance_cases.dart</c>. A disagreement
    /// shows up as a diff of two readable lines rather than as a boolean that stayed true.
    /// </para>
    /// </summary>
    internal static class DeclarationDigest
    {
        public const string Absent = "absent";
        public const string Empty = "-";

        public static readonly string[] Kinds = { "worlds", "gamemodes", "launchTargets" };

        public static Dictionary<string, List<string>> Of(ModManifest manifest)
        {
            var contributions = manifest.Contributions;
            return new Dictionary<string, List<string>>
            {
                ["worlds"] = (contributions?.Worlds ?? new List<ModWorldDeclaration>())
                    .Select(World).ToList(),
                ["gamemodes"] = (contributions?.Gamemodes ?? new List<ModGamemodeDeclaration>())
                    .Select(Gamemode).ToList(),
                ["launchTargets"] = (contributions?.LaunchTargets ?? new List<ModLaunchTargetDeclaration>())
                    .Select(LaunchTarget).ToList()
            };
        }

        private static string World(ModWorldDeclaration world)
        {
            var content = world.Content;
            return Join(
                world.Id,
                Text(content?.Kind),
                Text(content?.Bundle),
                Text(content?.Prefab),
                Binding(content?.Implementation),
                Text(content?.SceneName),
                List(world.Transitions),
                world.Spawn == null
                    ? Empty
                    : Text(world.Spawn.Kind) + ">" + Text(world.Spawn.MarkerName),
                List(world.OpenTo),
                Flag(world.OpenToAnyCompatible));
        }

        private static string Gamemode(ModGamemodeDeclaration gamemode)
        {
            var requirements = gamemode.WorldRequirements;
            return Join(
                gamemode.Id,
                Binding(gamemode.Implementation),
                requirements == null
                    ? Empty
                    : List(requirements.Transitions) + ">" + Text(requirements.Spawn),
                Text(gamemode.SceneChangePolicy));
        }

        private static string LaunchTarget(ModLaunchTargetDeclaration target)
        {
            var policy = target.World;
            return Join(
                target.Id,
                Text(target.Gamemode),
                Text(policy?.Policy),
                Text(policy?.Default),
                List(policy?.Allow),
                Flag(policy?.AllowPlayerOverride),
                Text(target.Transition),
                target.SortKey == null
                    ? Absent
                    : target.SortKey.Value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// An absent binding is <c>-</c>; a present one is always <c>assembly&gt;type</c>, with an empty
        /// assembly meaning the manifest's own entryAssembly. Rendering the separator either way keeps
        /// "no binding" and "a binding that defaults its assembly" distinguishable.
        /// </summary>
        private static string Binding(ModImplementationBinding? binding) =>
            binding == null ? Empty : binding.Assembly + ">" + binding.Type;

        private static string Flag(bool? value) =>
            value == null ? Absent : value.Value ? "true" : "false";

        private static string Text(string? value) =>
            string.IsNullOrEmpty(value) ? Empty : value;

        private static string List(IReadOnlyCollection<string>? values) =>
            values == null || values.Count == 0 ? Empty : string.Join(",", values);

        private static string Join(params string[] parts) => string.Join("|", parts);
    }
}
