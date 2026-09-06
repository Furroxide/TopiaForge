using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    // Temporary V3 wire adapter. Slice 7 removes it with the old profile wire.
    // It translates only a unique valid legacy selection; it never supplies a fallback world.
    internal static class LegacyWorldLaunchAdapter
    {
        internal static OperationResult<LaunchRequest> Resolve(EffectiveProfile profile, RuntimeObservation observation, WorldLaunchIntent intent)
        {
            if (intent.IsMainMenu || intent.Validate().Count != 0)
                return Failure("The legacy launch selection is invalid. Select a manifest launch target in the manager.");
            var desiredTransition = intent.LoadMode == WorldLaunchSettings.SceneReplacement
                ? ModTransitions.SceneReplacement : ModTransitions.AdditiveArena;
            var ownership = new LaunchProfileIndex(profile);
            var candidates = new List<LaunchRequest>();
            var blocks = new List<string>();
            foreach (var package in profile.Packages)
                foreach (var target in package.Manifest.Contributions?.LaunchTargets ?? new List<ModLaunchTargetDeclaration>())
                {
                    if (!ownership.Owns(package, target.Id) || !string.Equals(target.Gamemode, intent.GamemodeId, StringComparison.OrdinalIgnoreCase)) continue;
                    var requestedWorld = string.IsNullOrEmpty(intent.WorldId)
                        || string.Equals(target.World?.Default, intent.WorldId, StringComparison.OrdinalIgnoreCase) ? null : intent.WorldId;
                    var request = new LaunchRequest(target.Id, requestedWorld,
                        target.Transition == ModLaunchTargetDeclaration.PlayerChoiceTransition ? desiredTransition : null);
                    var result = LaunchResolver.Resolve(profile, request, observation);
                    if (result.Resolved && result.Plan!.Transition == desiredTransition) candidates.Add(request);
                    else if (!result.Resolved) blocks.AddRange(result.Blocks.Select(block => block.Code + ": " + block.Subject));
                    else blocks.Add("The target does not offer the remembered transition: " + target.Id);
                }
            if (candidates.Count == 1) return OperationResult<LaunchRequest>.Success(candidates[0]);
            var reason = candidates.Count > 1 ? "Several launch targets match" : "No valid launch target matches";
            return Failure(reason + " the legacy mode '" + intent.GamemodeId + "' and world '" + intent.WorldId
                + "'. The saved selection was preserved; select a target explicitly. "
                + string.Join("; ", blocks.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)));
        }
        private static OperationResult<LaunchRequest> Failure(string message) => OperationResult<LaunchRequest>.Failure(ModErrorCode.InvalidState, message);
    }
}
