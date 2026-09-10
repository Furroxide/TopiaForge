using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class LaunchSelectionResolution
    {
        internal LaunchSelectionResolution(bool mainMenu, LaunchRequest? request, IReadOnlyList<LaunchBlock> blocks, string repairMessage = "")
        { IsMainMenu = mainMenu; Request = request; Blocks = LaunchBlockCollection.Copy(blocks); RepairMessage = repairMessage; }
        public bool IsMainMenu { get; }
        public LaunchRequest? Request { get; }
        public IReadOnlyList<LaunchBlock> Blocks { get; }
        public string RepairMessage { get; }
        public bool Available => RepairMessage.Length == 0 && Blocks.Count == 0 && (IsMainMenu || Request != null);
    }
    public static class LaunchSelectionResolver
    {
        public static LaunchSelectionResolution Resolve(LaunchSelection selection, EffectiveProfile profile, RuntimeObservation? observation = null, RuntimeBindingSnapshot? bindings = null)
        {
            if (selection.Kind == "main-menu") return new LaunchSelectionResolution(true, null, Array.Empty<LaunchBlock>());
            if (selection.Kind == "target")
            {
                var result = LaunchResolver.Resolve(profile, selection.Request!, observation, bindings);
                return new LaunchSelectionResolution(false, selection.Request, result.Blocks);
            }
            return ResolveLegacy(selection, profile, observation, bindings);
        }
        private static LaunchSelectionResolution ResolveLegacy(LaunchSelection selection, EffectiveProfile profile, RuntimeObservation? observation, RuntimeBindingSnapshot? bindings)
        {
            string gamemode;
            string world;
            string transition;
            try
            {
                var wrapper = JsonObjectMerge.ReadProperties(selection.LegacyJson ?? "{}").ToDictionary(item => item.Name, item => item.RawValue, StringComparer.Ordinal);
                if (!wrapper.TryGetValue("worldLaunch", out var raw) || raw.Trim() == "null") return Repair("The original manager selection is missing.");
                var fields = JsonObjectMerge.ReadProperties(raw).ToDictionary(item => item.Name, item => item.RawValue, StringComparer.Ordinal);
                gamemode = LegacyString(fields, "selectedGamemodeId");
                world = LegacyString(fields, "selectedWorldId");
                var loadMode = LegacyString(fields, "loadMode");
                if (!ManifestContributionValidator.IsValidDeclarationId(gamemode) || !ManifestContributionValidator.IsValidDeclarationId(world))
                    return Repair("The saved gamemode or world identity is missing or invalid.");
                if (loadMode != WorldLaunchSettings.SceneReplacement && loadMode != WorldLaunchSettings.AdditiveArena)
                    return Repair("The saved transition is unknown and must be selected explicitly.");
                transition = loadMode == WorldLaunchSettings.SceneReplacement ? ModTransitions.SceneReplacement : ModTransitions.AdditiveArena;
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is System.IO.InvalidDataException
                || error is System.Runtime.Serialization.SerializationException)
            { return Repair("The original manager selection is incomplete or malformed."); }
            var index = new LaunchProfileIndex(profile);
            var candidates = new List<LaunchRequest>();
            var blocks = new List<LaunchBlock>();
            foreach (var package in profile.Packages)
                foreach (var target in package.Manifest.Contributions?.LaunchTargets ?? new List<ModLaunchTargetDeclaration>())
                {
                    if (!index.Owns(package, target.Id) || !string.Equals(target.Gamemode, gamemode, StringComparison.OrdinalIgnoreCase)) continue;
                    var worldOverride = string.Equals(world, target.World?.Default, StringComparison.OrdinalIgnoreCase) ? null : world;
                    var transitionOverride = target.Transition == ModLaunchTargetDeclaration.PlayerChoiceTransition ? transition : null;
                    var request = new LaunchRequest(target.Id, worldOverride, transitionOverride);
                    var result = LaunchResolver.Resolve(profile, request, observation, bindings);
                    if (result.Resolved && result.Plan!.Transition == transition
                        && string.Equals(result.Plan.WorldId, world, StringComparison.OrdinalIgnoreCase)) candidates.Add(request);
                    else blocks.AddRange(result.Blocks);
                }
            if (candidates.Count == 1) return new LaunchSelectionResolution(false, candidates[0], Array.Empty<LaunchBlock>());
            return new LaunchSelectionResolution(false, null, blocks, candidates.Count > 1
                ? "The saved selection matches several targets. Choose one explicitly."
                : "The saved selection has no available target. Choose a target or the main menu.");
        }
        private static string LegacyString(IReadOnlyDictionary<string, string> fields, string name)
        {
            if (!fields.TryGetValue(name, out var raw) || !raw.TrimStart().StartsWith("\"", StringComparison.Ordinal))
                throw new FormatException("Missing legacy string " + name + ".");
            return JsonUtil.Deserialize<string>(raw);
        }
        private static LaunchSelectionResolution Repair(string reason)
        {
            return new LaunchSelectionResolution(false, null, Array.Empty<LaunchBlock>(), reason);
        }
    }
}
