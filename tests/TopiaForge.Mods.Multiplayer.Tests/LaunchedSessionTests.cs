extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Sessions the launcher planned, started by the in-game screens over the real runtime and provider, as the game starts
/// them. A hosted launch opens on its own port under its own privacy; a launched join is pinned by the descriptor the
/// launcher checked, joining when it matches and refusing a server that changed; relay-only never goes direct; a live
/// join leaves the running game only when the player says so; a failed join goes back to the launcher as a handoff; and an
/// in-game preference change reaches the launcher's journal. No channel is touched on the frame's thread, and no record,
/// handoff, toast or log names an address.
/// </summary>
internal static class LaunchedSessionTests
{
    private const string RequestId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string Digest = "aa1fa8782182a9af";
    private const string Origin = "https://mp.topiaforge.dev";
    private const string Intent = "intent-7d3e1f20";
    private const string TargetHost = "192.168.1.20";
    private const int TargetPort = 27_261;
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void AHostedLaunchOpensOnItsOwnPortUnderItsOwnPrivacy()
    {
        using var rig = new ControlRig(7_801, Sandbox);
        using var screens = PeerScreens.Of(rig.Host, RequestId, Digest);
        var request = new LaunchSessionRequest("host", Intent, "Sam", new LaunchSessionTransport("relay-only", null, 27_262, false, true),
            host: new LaunchHostOptions("Sam's Sandbox", 4, "lan", false, false, false));
        var identity = new LaunchSessionIdentity(RequestId, "host", Intent);
        var offer = new SessionLaunch(identity, LaunchSessionOffer.ToSession(request), startsGame: false);
        That(offer.Take(rig.Host.Control) && offer.Plan != null, "the provider plans the hosted launch through its control (" + offer.Refusal + ")");
        var records = new StoredLaunch(screens.Store, identity);
        screens.Launched = new LaunchedSession(identity, offer.Plan!, records, () => LaunchedGame.Ready, hostPort: offer.HostPort);
        That(rig.Run(() => Frame(screens, () => records.HasResult)), "the launch settles (" + records + ")");
        var live = rig.Host.Provider.Live!.Host!;
        var network = rig.Host.Factory.Networks[^1];
        That(records.Outcome!.Status == "succeeded" && records.Outcome.Multiplayer.ServerId == live.Descriptor!.Descriptor.ServerId && rig.Host.Composition.Realm == NetRealm.ListenHost,
            "the provider's hosting, through the realm, is the launch's success, naming its server (" + records + ")");
        That(live.Port == 27_262 && network.LocalPort == 27_262 && network.Stack.Endpoint.Options.RelayOnly,
            "it hosts on exactly the launch's port, relay-only as the launch asked");

        var guest = rig.AddPeer("guest", Sandbox);
        rig.JoinAndWait(guest);
        That(rig.Run(() => Frame(screens, () => records.Blocks[^1].Players == 2)), "the launcher sees the player join (" + records.Blocks[^1].Players + ")");
        rig.Host.Composition.Leave();
        That(rig.Run(() => Frame(screens, () => records.End != null)) && records.End!.EndReason == "host-ended", "stopping ends the launched session as the host's");
        Clean(screens, rig.Host.Log.Lines);
    }

    internal static void AJoinLaunchIsPinnedToTheDescriptorTheLauncherChecked()
    {
        using var rig = new ControlRig(7_802, Sandbox);
        rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4));
        var descriptor = rig.Host.Provider.Live!.Host!.Descriptor!;
        var serverId = descriptor.Descriptor.ServerId;

        var guest = rig.AddPeer("guest", Sandbox);
        using var screens = PeerScreens.Of(guest, RequestId, Digest);
        var joined = LaunchJoin(screens, guest.Control, serverId, descriptor.Sha256);
        That(rig.Run(() => Frame(screens, () => joined.HasResult)) && joined.Outcome!.Status == "succeeded" && joined.Outcome.Multiplayer.ServerId == serverId &&
             guest.Composition.Realm == NetRealm.Client, "a join pinned to the server the launcher checked goes through the realm (" + joined + ")");

        var late = rig.AddPeer("late", Sandbox);
        using var lateScreens = PeerScreens.Of(late, RequestId, Digest);
        var admitted = rig.Host.Session.Snapshot.Participants.Count;
        var refused = LaunchJoin(lateScreens, late.Control, serverId, new string('0', 64));
        That(rig.Run(() => Frame(lateScreens, () => refused.HasResult)) && refused.Outcome!.Multiplayer.Failure == "admission-changed" &&
             refused.Outcome.Multiplayer.Detail == SessionLaunchRecords.AdmissionChangedDetail && late.Control.GetStatus().JoinFailure == InternalLiveFailure.AdmissionChanged,
            "a server whose descriptor is not the one the launcher checked is refused as changed (" + refused + ")");
        That(rig.Host.Session.Snapshot.Participants.Count == admitted && late.Composition.Realm == NetRealm.Offline, "and nothing joined it");

        // The failure goes back to the launcher that owns this game, which plans the join again; the handoff holds no address.
        var overlay = lateScreens.Presenter.Connecting;
        That(overlay.HasFailed && overlay.CanOpenLauncher && !overlay.CanRetry, "the failure view hands the join back to the launcher");
        overlay.OpenLauncher();
        var path = lateScreens.Store.SessionHandoffPath(RequestId);
        That(rig.Run(() => Frame(lateScreens, () => File.Exists(path))), "the handoff is written off the frame");
        var handoff = LaunchTransportJson.ReadSessionHandoff(File.ReadAllText(path));
        That(handoff.RequestId == RequestId && handoff.Target.Kind == "current" && lateScreens.Opened.Count == 0, "as the current session, never as a link");
        Clean(screens, guest.Log.Lines);
        Clean(lateScreens, late.Log.Lines);
    }

    internal static void RelayOnlyNeverGoesDirect()
    {
        using var rig = new ControlRig(7_803, Sandbox);
        rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4) { AllowDirect = true });
        var descriptor = rig.Host.Provider.Live!.Host!.Descriptor!;

        // The host answers only on a direct path: a relay-only launch refuses it, and nothing asks the player to allow it.
        rig.Resolver.Kind = TransportPathKind.Direct;
        var guest = rig.AddPeer("guest", Sandbox);
        using var screens = PeerScreens.Of(guest, RequestId, Digest);
        var refused = LaunchJoin(screens, guest.Control, descriptor.Descriptor.ServerId, descriptor.Sha256);
        That(rig.Run(() => Frame(screens, () => refused.HasResult)) && refused.Outcome!.Multiplayer.Failure == "direct-not-allowed" &&
             rig.Resolver.LastAllowDirect == false && screens.Presenter.PendingPrompt == null && guest.Composition.Realm == NetRealm.Offline,
            "a relay-only launch never goes direct and asks nothing (" + refused + ")");

        var consented = rig.AddPeer("consented", Sandbox);
        using var consentedScreens = PeerScreens.Of(consented, RequestId, Digest);
        var joined = LaunchJoin(consentedScreens, consented.Control, descriptor.Descriptor.ServerId, descriptor.Sha256, consent: true);
        That(rig.Run(() => Frame(consentedScreens, () => joined.HasResult)) && joined.Outcome!.Status == "succeeded" && rig.Resolver.LastAllowDirect == true,
            "with the player's consent for this session under the current disclosure it joins directly (" + joined + ")");
        Clean(screens, guest.Log.Lines);
        Clean(consentedScreens, consented.Log.Lines);
    }

    internal static void ALiveJoinLeavesTheRunningGameWhenTold()
    {
        using var rig = new InternetRig(7_804);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.10", "192.168.1.10", packages: Sandbox);
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        var descriptor = host.Provider.Live!.Host!.Descriptor!;
        var guest = rig.AddPeer("guest", NatBehaviour.FullCone, "203.0.113.20", "10.0.0.20", packages: Sandbox);
        using var screens = PeerScreens.Of(guest, RequestId, Digest, Origin);
        That(rig.Run(() => Frame(screens, () => screens.Presenter.Preferences.IsLoaded)), "the screens load");
        screens.Phase = SessionPhase.Running;

        var join = new SessionJoinRequest(RequestId, "join-live-1", Digest, new LaunchSessionRequest("join", Intent, "Sam",
            new LaunchSessionTransport("relay-only", null, 0, false, true), serviceOrigin: Origin,
            target: LaunchSessionTarget.Invite(invite.Code, descriptor.Descriptor.ServerId, descriptor.Sha256, new string('b', 64))));
        File.WriteAllText(screens.Store.SessionJoinPath(RequestId), LaunchTransportJson.WriteSessionJoin(join));
        screens.PollJoins();
        That(rig.Run(() => Frame(screens, () => screens.Presenter.PendingPrompt?.Title == ConnectingPresenter.LeaveTitle)) &&
             screens.Notices.Contains(LaunchSessionPresenter.LiveJoinArrived), "a live join while a game runs asks \"Leave current game?\" first");
        screens.Presenter.PendingPrompt!.Answer(true);
        That(rig.Run(() => Frame(screens, () => screens.MenuRequested)) && guest.Status.JoinPhase == InternalJoinPhase.None, "confirming returns to the menu before anything joins");
        screens.ReturnedToMenu();
        var outcome = screens.Store.OutcomePath("join-live-1");
        That(rig.Run(() => Frame(screens, () => File.Exists(outcome)), 60_000_000), "the live join settles (" + guest.Status.JoinPhase + ", " + guest.Status.JoinFailure + ")");
        var settled = LaunchTransportJson.ReadOutcome(File.ReadAllText(outcome));
        That(settled.Status == "succeeded" && settled.Multiplayer!.ServerId == descriptor.Descriptor.ServerId && settled.Command == "join-session" &&
             guest.Composition.Realm == NetRealm.Client, "it joins the invite through the realm, relay-only, and says so under its join id");
        That(!File.Exists(screens.Store.OutcomePath(RequestId)), "the launch's own records are untouched");
        Clean(screens, guest.Log.Lines, "203.0.113.20", "10.0.0.20", "198.51.100.10", "192.168.1.10");
    }

    internal static void AnInGamePreferenceChangeReachesTheJournal()
    {
        using var rig = new ControlRig(7_805, Sandbox);
        using var screens = PeerScreens.Of(rig.Host, RequestId, Digest);
        That(rig.Run(() => Frame(screens, () => screens.Presenter.Preferences.IsLoaded)), "the launcher's preferences load off the frame");
        screens.Presenter.SetDisplayName("Sammy");
        That(rig.Run(() => Frame(screens, () => !screens.Presenter.Preferences.IsSaving && screens.Store.ReadPreferencesJournal() != null)),
            "the change is recorded for the launcher");
        var change = screens.Store.ReadPreferencesJournal()!.Changes.Single();
        That(change.Kind == "displayName" && change.NewDisplayName == "Sammy" && rig.Host.Runtime.PreferredDisplayName == "Sammy",
            "in the launcher's journal, and in effect at once");
        That(screens.Channels.Calls.Any(call => call.Call == nameof(ILauncherChannels.RecordPreferenceChange)), "through the channel");
        Clean(screens, rig.Host.Log.Lines);
    }

    /// <summary>
    /// A join launched after an earlier session in this process ended, through "Leave current game?": the provider still
    /// reports the earlier join's end when the launched one starts, and only the launched join's own status settles it.
    /// </summary>
    internal static void AJoinAfterAnEarlierSessionIsSettledByItsOwnStatus()
    {
        using var rig = new ControlRig(7_806, Sandbox);
        rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4));
        var descriptor = rig.Host.Provider.Live!.Host!.Descriptor!;
        var guest = rig.AddPeer("guest", Sandbox);
        using var screens = PeerScreens.Of(guest, RequestId, Digest);
        rig.JoinAndWait(guest);
        guest.Control.Leave();
        That(rig.Run(() => Frame(screens, () => screens.Presenter.Context.Status.Status.JoinPhase == InternalJoinPhase.Ended)), "an earlier session ended");
        screens.Phase = SessionPhase.Running;

        var joined = LaunchJoin(screens, guest.Control, descriptor.Descriptor.ServerId, descriptor.Sha256);
        That(rig.Run(() => Frame(screens, () => screens.Presenter.PendingPrompt?.Title == ConnectingPresenter.LeaveTitle)), "the launched join asks to leave the running game");
        screens.Presenter.PendingPrompt!.Answer(true);
        That(rig.Run(() => Frame(screens, () => screens.MenuRequested)), "confirming returns to the menu first");
        screens.ReturnedToMenu();
        That(rig.Run(() => Frame(screens, () => joined.HasResult)), "the launch settles (" + joined + ")");
        That(joined.Outcome!.Status == "succeeded" && joined.Outcome.Multiplayer.ServerId == descriptor.Descriptor.ServerId && guest.Composition.Realm == NetRealm.Client,
            "it reports the join it started, never the earlier one's end (" + joined + ")");
        Clean(screens, guest.Log.Lines);
    }

    /// <summary>
    /// The pin holds against a server that moved on and against an impostor: a host that signed its descriptor again after
    /// the launcher fetched it, an answer with another server's descriptor, and the planned server's own descriptor replayed
    /// byte for byte from another endpoint, which cannot complete the handshake its transport key pins. Each is refused as
    /// admission changed, and nothing joins.
    /// </summary>
    internal static void AChangedOrReplayedDescriptorIsRefused()
    {
        using (var rig = new ControlRig(7_807, Sandbox))
        {
            rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4));
            var live = rig.Host.Provider.Live!.Host!;
            var fetched = live.Descriptor!;
            live.UpdateWorld("Freeplay", "Sandbox");
            That(live.Descriptor!.Sha256 != fetched.Sha256, "the host signs its descriptor again when its world changes");
            Refused(rig, fetched, "a host that signed its descriptor again since the launcher fetched it", SessionLaunchRecords.AdmissionChangedDetail);
        }

        using (var rig = new ControlRig(7_808, Sandbox))
        {
            rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4));
            var planned = rig.Host.Provider.Live!.Host!.Descriptor!;
            var other = Hosting(rig, "other");
            var host = rig.Host.Factory.Networks[^1].Node;
            var answer = other.Provider.Live!.Host!.Descriptor!;
            rig.Resolver.Host = () => (host, answer);
            Refused(rig, planned, "an answer with another server's descriptor", SessionLaunchRecords.AdmissionChangedDetail);
        }

        using (var rig = new ControlRig(7_809, Sandbox))
        {
            rig.StartHosting(new InternalHostSettings("Sam's Sandbox", 4));
            var planned = rig.Host.Provider.Live!.Host!.Descriptor!;
            var impostor = Hosting(rig, "impostor");
            var node = impostor.Factory.Networks[^1].Node;
            rig.Resolver.Host = () => (node, planned);
            Refused(rig, planned, "the planned server's descriptor replayed from another endpoint", "The host did not prove it is the server you planned to join.");
            That(impostor.Session.Snapshot.Participants.Count == 1, "and nothing joined the impostor");
        }
    }

    /// <summary>A second server on the rig's network, hosting, with its descriptor signed.</summary>
    private static ControlPeer Hosting(ControlRig rig, string name)
    {
        var peer = rig.AddPeer(name, Sandbox);
        That(peer.Control.StartHosting(new InternalHostSettings(name, 4)).Succeeded && rig.Run(() => peer.Provider.Live!.Host?.Descriptor != null),
            name + " hosts");
        return peer;
    }

    /// <summary>A launched join pinned to <paramref name="planned"/> fails as admission changed, with <paramref name="detail"/>, and joins nothing.</summary>
    private static void Refused(ControlRig rig, provider::TopiaForge.Multiplayer.Network.SignedServerDescriptor planned, string what, string detail)
    {
        var guest = rig.AddPeer("guest", Sandbox);
        using var screens = PeerScreens.Of(guest, RequestId, Digest);
        var hosted = rig.Host.Session.Snapshot.Participants.Count;
        var joined = LaunchJoin(screens, guest.Control, planned.Descriptor.ServerId, planned.Sha256);
        That(rig.Run(() => Frame(screens, () => joined.HasResult), 60_000_000), what + ": the launch settles (" + joined + ")");
        That(joined.Outcome!.Multiplayer.Failure == "admission-changed" && joined.Outcome.Multiplayer.Detail == detail &&
             guest.Composition.Realm == NetRealm.Offline && rig.Host.Session.Snapshot.Participants.Count == hosted, what + " is refused, and nothing joins (" + joined + ")");
        Clean(screens, guest.Log.Lines);
    }

    /// <summary>Plans a launched join of a host typed by address, through the provider's control as the runtime does, and hands it to the screens.</summary>
    private static StoredLaunch LaunchJoin(PeerScreens screens, IInternalMultiplayerControl control, string serverId, string descriptorSha256, bool consent = false)
    {
        var transport = consent
            ? new LaunchSessionTransport("direct", new LaunchDirectConsent(Intent, MultiplayerPrivacyText.DisclosureVersion), 0, false, true)
            : new LaunchSessionTransport("relay-only", null, 0, false, true);
        var request = new LaunchSessionRequest("join", Intent, "Sam", transport,
            target: LaunchSessionTarget.Direct(TargetHost, TargetPort, serverId, descriptorSha256, new string('b', 64)));
        var identity = new LaunchSessionIdentity(RequestId, "join", Intent);
        var offer = new SessionLaunch(identity, LaunchSessionOffer.ToSession(request), startsGame: false);
        That(offer.Take(control) && offer.Plan != null, "the provider plans the launched join (" + offer.Refusal + ")");
        var records = new StoredLaunch(screens.Store, identity);
        screens.Launched = new LaunchedSession(identity, offer.Plan!, records, () => LaunchedGame.Ready, handoff: LauncherHandoff.Current());
        return records;
    }

    /// <summary>Runs the screens' part of the frame the rig is on, then reads <paramref name="condition"/>.</summary>
    private static bool Frame(PeerScreens screens, Func<bool> condition)
    {
        screens.Frame();
        return condition();
    }

    /// <summary>
    /// Nothing the screens wrote for the launcher, toasted or logged names an address, and every channel was read and
    /// written off this thread, the frame's.
    /// </summary>
    private static void Clean(PeerScreens screens, IEnumerable<string> logs, params string[] addresses)
    {
        var texts = screens.Written().Concat(logs).ToArray();
        foreach (var secret in addresses.Concat(new[] { TargetHost, TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
            That(!texts.Any(text => text.Contains(secret, StringComparison.OrdinalIgnoreCase)), "no record, handoff, toast or log names " + secret);
        }

        var frame = Environment.CurrentManagedThreadId;
        var calls = screens.Channels.Calls.ToArray();
        That(calls.Length > 0 && calls.All(call => call.Thread != frame),
            "no channel is read or written on the frame's thread (" + string.Join(", ", calls.Where(call => call.Thread == frame).Select(call => call.Call)) + ")");
    }
}
