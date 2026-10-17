using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The realm at the runtime over real providers on the deterministic network, each composed as the game composes it:
/// opening to multiplayer promotes the running solo session in place and takes the census of what was spawned before,
/// joining needs an idle local session, and leaving is never refused.
/// </summary>
internal static class RuntimeRealmTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void OpeningToMultiplayerPromotesTheRunningSessionInPlace()
    {
        using var rig = new ControlRig(190, Sandbox);
        var host = rig.Host.Composition;
        host.SessionPhaseSource = () => SessionPhase.Running;
        var census = new CensusProbe();
        host.PromotionCensus = census;

        // What the solo session spawned before anyone thought of hosting.
        var crate = new RealmEntity();
        var gone = new RealmEntity();
        host.Spawns.Record(crate, Spawn("io.example.sandbox"));
        host.Spawns.Record(gone, Spawn("io.example.sandbox"));
        gone.IsAlive = false;
        var solo = rig.Host.Session.Snapshot.Id;

        var opened = host.OpenToMultiplayer(new InternalHostSettings("Open", 4));
        That(opened.Succeeded && opened.Verdict == NetRealmTransitionVerdict.Allowed && opened.Effect == NetRealmTransitionEffect.PromoteInPlace,
            "with a world running, opening to multiplayer promotes it in place (" + opened + ")");
        That(rig.Run(() => host.Realm == NetRealm.ListenHost) && rig.Host.Control.GetStatus().HostPhase == InternalHostPhase.Hosting, "the provider hosts at a later pump");
        That(rig.Host.Session.Snapshot.Id.Equals(solo), "the running solo session became the host's session in place");
        That(census.Promotions.Count == 1 && census.Promotions[0].Effect == NetRealmTransitionEffect.PromoteInPlace &&
             census.Promotions[0].Spawns.Single().Entity == crate, "the census baselines what was spawned before, and only what is still there");

        var guest = rig.AddPeer("guest", Sandbox);
        rig.JoinAndWait(guest);
        That(rig.Run(() => rig.Host.Session.Snapshot.Participants.Count(participant => participant.IsConnected) == 2) &&
             rig.Host.Session.Snapshot.Id.Equals(solo), "a player joins the promoted session");
        That(census.Promotions.Count == 1, "and takes no new census");
        That(host.OpenToMultiplayer(new InternalHostSettings("Again", 4)).Verdict == NetRealmTransitionVerdict.Unchanged, "a host is already open");

        var stopped = host.Leave();
        That(stopped.Succeeded && stopped.Effect == NetRealmTransitionEffect.EndOnlineSession && rig.Run(() => host.Realm == NetRealm.Offline),
            "stopping is leaving, and is never refused");
        That(rig.Host.Session.Snapshot.Id.Equals(solo) && rig.Run(() => guest.Composition.Realm == NetRealm.Offline),
            "the host plays on alone in the same session, and the player is back in single player");
    }

    internal static void JoiningNeedsAnIdleSessionAndLeavingIsNeverRefused()
    {
        using var rig = new ControlRig(191, Sandbox);
        rig.StartHosting(new InternalHostSettings("Rules", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        var phase = SessionPhase.Running;
        guest.Composition.SessionPhaseSource = () => phase;

        var refused = guest.Composition.Join(guest.Target(), new InternalJoinSettings());
        That(refused.Verdict == NetRealmTransitionVerdict.RequiresLeave && refused.Result.ErrorMessage == MultiplayerRuntime.LeaveGameFirst,
            "while the player's own game runs, a join asks it to leave first (" + refused + ")");
        That(guest.Control.GetStatus().JoinPhase == InternalJoinPhase.None && !rig.Run(() => guest.Control.Role != InternalSessionRole.Solo, 500_000),
            "and nothing starts");
        phase = SessionPhase.LoadingWorld;
        That(guest.Composition.Join(guest.Target(), new InternalJoinSettings()).Verdict == NetRealmTransitionVerdict.Busy, "a game that is loading must settle first");

        phase = SessionPhase.Idle;
        var joined = guest.Composition.Join(guest.Target(), new InternalJoinSettings());
        That(joined.Succeeded && joined.Effect == NetRealmTransitionEffect.ReplaceLocalSession, "from the menu the join replaces the idle session");
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && guest.Composition.Realm == NetRealm.Client), "and goes through");
        That(guest.Composition.OpenToMultiplayer(new InternalHostSettings("Mine", 4)).Result.ErrorMessage == MultiplayerRuntime.LeaveSessionFirst,
            "a joined player must leave before it hosts");
        That(rig.Host.Composition.Join(rig.Host.Target(), new InternalJoinSettings()).Result.ErrorMessage == MultiplayerRuntime.StopHostingFirst,
            "and a host must stop before it joins");

        // Leaving works whatever the local session is doing, even mid-change.
        phase = SessionPhase.Stopping;
        var left = guest.Composition.Leave();
        That(left.Succeeded && left.Verdict == NetRealmTransitionVerdict.Allowed && rig.Run(() => guest.Composition.Realm == NetRealm.Offline) &&
             guest.Control.GetStatus().JoinFailure == InternalLiveFailure.Left, "leaving is never refused (" + left + ")");
        var again = guest.Composition.Leave();
        That(again.Succeeded && again.Verdict == NetRealmTransitionVerdict.Unchanged, "leaving again changes nothing, and still succeeds");

        // A join on its way out of single player is stopped too.
        phase = SessionPhase.Idle;
        That(guest.Composition.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the player joins again");
        That(guest.Composition.Leave().Succeeded && rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
             guest.Control.GetStatus().JoinFailure == InternalLiveFailure.Cancelled && guest.Composition.Realm == NetRealm.Offline,
            "leaving before the link is up cancels the join");
    }

    private static MultiplayerSpawnDescription Spawn(string mod) =>
        new(mod, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), MultiplayerSpawnSource.SdkSpawn, MultiplayerSpawnKinds.AssetPrefab, null,
            TransformState.Identity, null);

    private sealed class CensusProbe : IMultiplayerPromotionCensus
    {
        internal List<MultiplayerPromotion> Promotions { get; } = new();

        public void TakeCensus(MultiplayerPromotion promotion) => Promotions.Add(promotion);
    }

    private sealed class RealmEntity : IEntity
    {
        private static int next;

        public string Id { get; } = "realm-entity-" + Interlocked.Increment(ref next);

        public string Name => "realm entity";

        public bool IsAlive { get; set; } = true;

        public Vec3 Position => Vec3.Zero;
    }
}
