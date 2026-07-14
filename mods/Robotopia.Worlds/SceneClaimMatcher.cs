using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.Worlds
{
    /// <summary>Unity-free matching for a coordinated scene arrival; separated so wildcard semantics stay tested.</summary>
    internal static class SceneClaimMatcher
    {
        public static SceneClaimInfo? FindForeign(
            IReadOnlyList<SceneClaimInfo> claims,
            string arrivingSceneName,
            string ownerModId)
        {
            if (claims == null)
            {
                return null;
            }

            // User transitions stack and the most recently acquired claim is the last dispatch/winner.
            // Prefer it for both attribution and wildcard-vs-specific collisions.
            for (var index = claims.Count - 1; index >= 0; index--)
            {
                var claim = claims[index];
                if (string.Equals(claim.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The public request contract permits an unknown scene name. Such a claim applies to the next
                // single-mode arrival; otherwise a valid user takeover could never end the displaced session.
                if (string.IsNullOrWhiteSpace(claim.SceneName)
                    || string.Equals(claim.SceneName, arrivingSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return claim;
                }
            }

            return null;
        }
    }
}
