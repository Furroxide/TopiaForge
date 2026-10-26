namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>Host-only world geometry: shared scripts shadow, but player actions never predict it.</summary>
    internal static class BlockerAuthorityRules
    {
        internal static ExecutionRoute Route(NetRealm realm, ModNetProfile profile, IntentSource intent)
        {
            NetRealms.Require(realm);
            if (profile.Context == ModExecutionContext.Participant || !profile.RunsOn(realm))
                return ExecutionRoute.NotRun;
            if (realm != NetRealm.Client) return ExecutionRoute.Canonical;
            return profile.NetworkClass == ModNetworkClass.Shared && !IntentRules.IsPlayerIntent(intent)
                ? ExecutionRoute.Shadow : ExecutionRoute.NotRun;
        }
    }
}
