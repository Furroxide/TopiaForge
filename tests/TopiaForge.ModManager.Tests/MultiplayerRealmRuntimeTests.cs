using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The realm at the runtime (WS3.1): it follows the provider's role, opening to multiplayer promotes the running solo
    /// session in place and takes the census, joining needs an idle session, leaving is never refused, and every loaded
    /// mod has the network class its effective manifest gives it.
    /// </summary>
    internal static class MultiplayerRealmRuntimeTests
    {
        internal static void Run()
        {
            TheRealmFollowsTheProvidersRole();
            OpeningToMultiplayerPromotesTheRunningSessionInPlace();
            HostingFollowsTheRealmRules();
            JoiningNeedsAnIdleSession();
            LeavingIsNeverRefused();
            RealmRequestsNeedALiveProvider();
            AnUnreadablePhaseCountsAsBusy();
            CensusFailuresAreIsolated();
            NetworkClassesComeFromEffectiveManifests();
            StandaloneRefusalsNameTheMods();
            MapWorldCannotPromote();
        }

        private static void MapWorldCannotPromote()
        {
            var (runtime, control, log) = Runtime(() => SessionPhase.Running);
            runtime.CanHostWorld = () => false;
            runtime.Connect();
            var verdict = runtime.EvaluateRealmChange(NetRealm.ListenHost);
            That(verdict.Verdict == NetRealmTransitionVerdict.WorldRefused && !verdict.IsAllowed,
                "A single-player map cannot be promoted into a multiplayer session.");
            That(TopiaForge.ModManager.Sessions.SessionRealmGate.ForHost(verdict) == TopiaForge.ModManager.Sessions.RealmDecision.WorldRefused,
                "The host screen must show the same map refusal.");
            That(runtime.EvaluateRealmChange(NetRealm.Offline).Verdict == NetRealmTransitionVerdict.Unchanged,
                "Map admission never prevents staying offline.");
            var opened = runtime.OpenToMultiplayer(new InternalHostSettings("Map", 4));
            That(!opened.Succeeded && opened.Verdict == NetRealmTransitionVerdict.WorldRefused && control.RealmCalls.Count == 0,
                "The actual host command refuses before touching the network provider.");
        }

        private static void TheRealmFollowsTheProvidersRole()
        {
            var (runtime, control, log) = Runtime(() => SessionPhase.Idle);
            That(runtime.Realm == NetRealm.Offline, "before attaching, the process is offline");
            That(runtime.Connect(), "the runtime attaches");
            foreach (var (role, realm) in new[]
                     {
                         (InternalSessionRole.ListenHost, NetRealm.ListenHost), (InternalSessionRole.Client, NetRealm.Client),
                         (InternalSessionRole.Solo, NetRealm.Offline)
                     })
            {
                var before = runtime.Realm;
                control.Role = role;
                That(runtime.Realm == before, "the realm waits for the pump that reads " + role);
                Frame(runtime);
                That(runtime.Realm == realm, role + " is " + realm);
            }

            control.Role = InternalSessionRole.Client;
            control.PumpFailure = new InvalidOperationException("broken");
            Frame(runtime);
            That(runtime.Realm == NetRealm.Offline, "a pump that fails reads no new role");
            control.PumpFailure = null;
            Frame(runtime);
            That(runtime.Realm == NetRealm.Client, "the next pump that works does");
            runtime.Disconnect();
            That(runtime.Realm == NetRealm.Offline, "detaching goes offline");
            var changes = log.Lines.Where(line => line.StartsWith("info: Multiplayer: this game is now", StringComparison.Ordinal)).ToArray();
            That(changes.Length == 5 && changes[^1] == "info: Multiplayer: this game is now offline (it was joined to a host).",
                "each change of realm is logged once, by name (" + string.Join(" | ", changes) + ")");
        }

        private static void OpeningToMultiplayerPromotesTheRunningSessionInPlace()
        {
            var phase = SessionPhase.Running;
            var (runtime, control, _) = Runtime(() => phase);
            var census = new RecordingCensus();
            runtime.PromotionCensus = census;
            runtime.Connect();

            // Spawns recorded in single player, before anyone hosts.
            var blocks = new SeamEntity();
            var platform = new SeamEntity();
            var gone = new SeamEntity();
            var cosmetic = new SeamEntity();
            runtime.Spawns.Record(blocks, Spawn("io.example.blocks", ModNetworkClass.Shared, ModExecutionContext.Process));
            runtime.Spawns.Record(platform, Content("io.example.arena", "arena/platform#0"));
            runtime.Spawns.Record(gone, Spawn("io.example.blocks", ModNetworkClass.Shared, ModExecutionContext.GamemodeServer));
            gone.IsAlive = false;
            That(runtime.Spawns.Record(cosmetic, Spawn("io.example.hats", ModNetworkClass.ClientLocal, ModExecutionContext.Process)) == null,
                "a client-local mod's spawn is never recorded");

            var opened = runtime.OpenToMultiplayer(new InternalHostSettings("Open", 4));
            That(opened.Succeeded && opened.Verdict == NetRealmTransitionVerdict.Allowed && opened.Effect == NetRealmTransitionEffect.PromoteInPlace,
                "with a world running, opening to multiplayer promotes the session in place (" + opened + ")");
            That(control.RealmCalls.SequenceEqual(new[] { "host" }) && census.Promotions.Count == 0 && runtime.Realm == NetRealm.Offline,
                "the provider is asked to host, and nothing changes until it does");
            Frame(runtime);
            That(census.Promotions.Count == 0, "hosting starts at a later pump");
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(runtime.Realm == NetRealm.ListenHost && census.Promotions.Count == 1, "the pump that shows this process hosting takes the census");
            var promotion = census.Promotions[0];
            That(promotion.Effect == NetRealmTransitionEffect.PromoteInPlace && promotion.Phase == SessionPhase.Running &&
                 promotion.Spawns.Select(record => record.Entity).SequenceEqual(new IEntity[] { blocks, platform }),
                "it baselines the live spawns recorded offline, in the order they happened");
            That(promotion.Spawns[1].Description.Source == MultiplayerSpawnSource.WorldContent && promotion.Spawns[1].Description.ContentKey == "arena/platform#0" &&
                 promotion.Spawns[1].Description.Profile.Context == ModExecutionContext.WorldOwner, "world content keeps its key and its world owner");
            Frame(runtime);
            That(census.Promotions.Count == 1, "the census is taken once per promotion");

            // Stopping and hosting again promotes again, from what is still alive.
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            blocks.IsAlive = false;
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(census.Promotions.Count == 2 && census.Promotions[1].Spawns.Single().Entity == platform, "a second promotion baselines what is left");

            // From the menu this game starts as a host instead.
            phase = SessionPhase.Idle;
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            var started = runtime.OpenToMultiplayer(new InternalHostSettings("Menu", 4));
            That(started.Succeeded && started.Effect == NetRealmTransitionEffect.StartAsHost, "from the menu the game starts as a host (" + started + ")");
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(census.Promotions.Count == 3 && census.Promotions[2].Effect == NetRealmTransitionEffect.StartAsHost && census.Promotions[2].Phase == SessionPhase.Idle,
                "and its census says so");

            // A joined session that starts and ends takes no census.
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            That(census.Promotions.Count == 3, "joining and leaving another host takes no census");
        }

        private static void HostingFollowsTheRealmRules()
        {
            foreach (var phase in Phases)
            {
                var settled = phase == SessionPhase.Idle || phase == SessionPhase.Running;
                ExpectHost(InternalSessionRole.Solo, phase, false,
                    settled ? NetRealmTransitionVerdict.Allowed : NetRealmTransitionVerdict.Busy, settled ? null : MultiplayerRuntime.SessionBusy);
                ExpectHost(InternalSessionRole.ListenHost, phase, false, NetRealmTransitionVerdict.Unchanged, MultiplayerRuntime.AlreadyHosting);
                ExpectHost(InternalSessionRole.Client, phase, false, NetRealmTransitionVerdict.RequiresLeave, MultiplayerRuntime.LeaveSessionFirst);
                ExpectHost(InternalSessionRole.Solo, phase, true, NetRealmTransitionVerdict.StandaloneRefused, "Solo refuses multiplayer; disable it to host.");
                ExpectHost(InternalSessionRole.Client, phase, true, NetRealmTransitionVerdict.RequiresLeave, MultiplayerRuntime.LeaveSessionFirst);
            }

            var (runtime, control, _) = Runtime(() => SessionPhase.Idle);
            runtime.Connect();
            control.HostingResult = OperationResult<bool>.Failure(ModErrorCode.InvalidState, "A join is already in progress.");
            var refused = runtime.OpenToMultiplayer(new InternalHostSettings("Busy", 4));
            That(refused.Verdict == NetRealmTransitionVerdict.Allowed && !refused.Succeeded && refused.Result.ErrorMessage == "A join is already in progress.",
                "a refusal of the provider's own comes back as it is");
        }

        private static void JoiningNeedsAnIdleSession()
        {
            foreach (var phase in Phases)
            {
                var offline = phase == SessionPhase.Idle ? NetRealmTransitionVerdict.Allowed
                    : phase == SessionPhase.Running ? NetRealmTransitionVerdict.RequiresLeave
                    : NetRealmTransitionVerdict.Busy;
                ExpectJoin(InternalSessionRole.Solo, phase, false, offline,
                    offline == NetRealmTransitionVerdict.Allowed ? null : offline == NetRealmTransitionVerdict.Busy ? MultiplayerRuntime.SessionBusy : MultiplayerRuntime.LeaveGameFirst);
                ExpectJoin(InternalSessionRole.ListenHost, phase, false, NetRealmTransitionVerdict.RequiresLeave, MultiplayerRuntime.StopHostingFirst);
                ExpectJoin(InternalSessionRole.Client, phase, false, NetRealmTransitionVerdict.Unchanged, MultiplayerRuntime.AlreadyJoined);
                ExpectJoin(InternalSessionRole.Solo, phase, true, NetRealmTransitionVerdict.StandaloneRefused, "Solo refuses multiplayer; disable it to join.");
            }

            // The interface asks first, so it can ask "Leave current game?"; after the player leaves the game, the join goes through.
            var phaseNow = SessionPhase.Running;
            var (runtime, control, _) = Runtime(() => phaseNow);
            runtime.Connect();
            That(runtime.EvaluateRealmChange(NetRealm.Client).Verdict == NetRealmTransitionVerdict.RequiresLeave && control.RealmCalls.Count == 0,
                "asking changes nothing");
            var refused = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(!refused.Succeeded && refused.Verdict == NetRealmTransitionVerdict.RequiresLeave && refused.Result.ErrorCode == ModErrorCode.InvalidState &&
                 control.RealmCalls.Count == 0, "a join while a game runs is refused with RequiresLeave, and nothing reaches the provider");
            phaseNow = SessionPhase.Idle;
            var joined = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(joined.Succeeded && joined.Effect == NetRealmTransitionEffect.ReplaceLocalSession && control.RealmCalls.SequenceEqual(new[] { "join" }),
                "from the menu the join replaces the idle local session");
            control.JoinResult = OperationResult<bool>.Failure(ModErrorCode.InvalidState, "A join is already in progress.");
            var again = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(again.Verdict == NetRealmTransitionVerdict.Allowed && again.Result.ErrorMessage == "A join is already in progress.",
                "the provider's own refusal comes back as it is");
            Throws<ArgumentNullException>(() => runtime.Join(null!, new InternalJoinSettings()), "a join needs a target");
            Throws<ArgumentNullException>(() => runtime.Join(new FakeJoinTarget(), null!), "and settings");
            Throws<ArgumentNullException>(() => runtime.OpenToMultiplayer(null!), "hosting needs settings");
        }

        private static void LeavingIsNeverRefused()
        {
            foreach (var role in new[] { InternalSessionRole.Solo, InternalSessionRole.ListenHost, InternalSessionRole.Client })
            {
                foreach (var phase in Phases)
                {
                    foreach (var standalone in new[] { false, true })
                    {
                        var (runtime, control) = InRealm(role, phase, standalone);
                        var left = runtime.Leave();
                        var online = role != InternalSessionRole.Solo;
                        That(left.Succeeded && left.Verdict == (online ? NetRealmTransitionVerdict.Allowed : NetRealmTransitionVerdict.Unchanged) &&
                             left.Effect == (online ? NetRealmTransitionEffect.EndOnlineSession : NetRealmTransitionEffect.None),
                            "leaving as " + role + " while " + phase + (standalone ? " with a standalone mod" : string.Empty) + " is never refused (" + left + ")");
                        var expected = role == InternalSessionRole.ListenHost ? new[] { "stop-hosting", "leave" } : new[] { "leave" };
                        That(control.RealmCalls.SequenceEqual(expected), role + " leaves through " + string.Join(",", control.RealmCalls));
                    }
                }
            }

            // Hosting that is still starting stops; an earlier hosting failure stays on record for the interface.
            var (starting, startingControl) = InRealm(InternalSessionRole.Solo, SessionPhase.Running, false);
            startingControl.HostPhase = InternalHostPhase.Preparing;
            That(starting.Leave().Succeeded && startingControl.RealmCalls.SequenceEqual(new[] { "stop-hosting", "leave" }), "hosting on its way is stopped");
            var (failed, failedControl) = InRealm(InternalSessionRole.Solo, SessionPhase.Idle, false);
            failedControl.HostPhase = InternalHostPhase.Failed;
            That(failed.Leave().Succeeded && failedControl.RealmCalls.SequenceEqual(new[] { "leave" }), "a failed host is left as it is");

            var log = new RecordingRuntimeLog();
            var none = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            That(none.Leave().Succeeded, "without a provider there is nothing to leave, and that succeeds");

            // A provider that breaks while leaving is reported, but never as a refusal; both calls are still made.
            var (broken, brokenControl) = InRealm(InternalSessionRole.ListenHost, SessionPhase.Running, false);
            var brokenLog = (RecordingRuntimeLog)broken.Log;
            brokenControl.LeaveFailure = new InvalidOperationException("the relay at 198.51.100.4:3478 is gone");
            var first = broken.Leave();
            var second = broken.Leave();
            That(!first.Succeeded && first.Verdict == NetRealmTransitionVerdict.Allowed && first.Result.ErrorCode == ModErrorCode.External &&
                 brokenControl.RealmCalls.SequenceEqual(new[] { "stop-hosting", "leave", "stop-hosting", "leave" }),
                "a failure is the provider's, not the realm's, and every step is still tried");
            var errors = brokenLog.Lines.Where(line => line.StartsWith("error:", StringComparison.Ordinal)).ToArray();
            That(!second.Succeeded && errors.Length == 1 && !errors[0].Contains("198.51.100.4", StringComparison.Ordinal) &&
                 errors[0].Contains("InvalidOperationException", StringComparison.Ordinal), "it is logged once, redacted (" + string.Join(" | ", errors) + ")");
        }

        private static void RealmRequestsNeedALiveProvider()
        {
            var log = new RecordingRuntimeLog();
            var none = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log) { SessionPhaseSource = () => SessionPhase.Running };
            That(!none.Connect(), "no provider attaches");
            foreach (var change in new[] { none.OpenToMultiplayer(new InternalHostSettings("Nobody", 4)), none.Join(new FakeJoinTarget(), new InternalJoinSettings()) })
            {
                That(change.Result.ErrorCode == ModErrorCode.Unavailable && change.Result.ErrorMessage == MultiplayerRuntime.ProviderMissing,
                    "without the provider, going online is unavailable, which no leaving would change");
            }

            var (runtime, control, _) = Runtime(() => SessionPhase.Running);
            runtime.Connect();
            control.IsLiveAvailable = false;
            var join = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            var host = runtime.OpenToMultiplayer(new InternalHostSettings("Off", 4));
            That(join.Result.ErrorMessage == MultiplayerRuntime.LiveSwitchedOff && host.Result.ErrorMessage == MultiplayerRuntime.LiveSwitchedOff &&
                 control.RealmCalls.Count == 0, "with live sessions switched off, neither reaches the provider");
        }

        private static void AnUnreadablePhaseCountsAsBusy()
        {
            Func<SessionPhase> source = () => throw new InvalidOperationException("phase at 203.0.113.7:27250");
            var (runtime, control, log) = Runtime(() => source());
            runtime.Connect();
            var first = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            var second = runtime.OpenToMultiplayer(new InternalHostSettings("Guess", 4));
            That(first.Verdict == NetRealmTransitionVerdict.Busy && second.Verdict == NetRealmTransitionVerdict.Busy && control.RealmCalls.Count == 0,
                "a phase that cannot be read is busy, so nothing goes online on a guess");
            var errors = log.Lines.Where(line => line.StartsWith("error:", StringComparison.Ordinal)).ToArray();
            That(errors.Length == 1 && !errors[0].Contains("203.0.113.7", StringComparison.Ordinal), "it is logged once, redacted");
            That(runtime.Leave().Succeeded, "and leaving still works");
            source = () => (SessionPhase)42;
            That(runtime.Join(new FakeJoinTarget(), new InternalJoinSettings()).Verdict == NetRealmTransitionVerdict.Busy, "an undefined phase is busy too");
            source = () => SessionPhase.Idle;
            That(runtime.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a readable phase decides again");
            source = () => throw new InvalidOperationException("again");
            runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(log.Lines.Count(line => line.StartsWith("error:", StringComparison.Ordinal)) == 2, "a failure after a recovery is logged again");
        }

        private static void CensusFailuresAreIsolated()
        {
            var (runtime, control, log) = Runtime(() => SessionPhase.Running);
            var census = new RecordingCensus { Failure = new InvalidOperationException("baseline for 203.0.113.8:27250 failed") };
            var trace = new List<string>();
            runtime.PromotionCensus = census;
            runtime.WorldEvents = new RecordingFrameStage("world", trace.Add);
            runtime.Connect();
            for (var round = 0; round < 2; round++)
            {
                control.Role = InternalSessionRole.ListenHost;
                Frame(runtime);
                control.Role = InternalSessionRole.Solo;
                Frame(runtime);
            }

            var errors = log.Lines.Where(line => line.Contains("census", StringComparison.Ordinal)).ToArray();
            That(census.Promotions.Count == 2 && errors.Length == 1 && !errors[0].Contains("203.0.113.8", StringComparison.Ordinal),
                "a census that fails is logged once until it recovers, redacted (" + string.Join(" | ", errors) + ")");
            That(trace.Count == 2, "and the host's frame goes on");
            census.Failure = null;
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            census.Failure = new InvalidOperationException("again");
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(log.Lines.Count(line => line.Contains("census", StringComparison.Ordinal)) == 2, "a failure after a recovery is logged again");
        }

        private static void NetworkClassesComeFromEffectiveManifests()
        {
            var (runtime, _, _) = Runtime(() => SessionPhase.Idle);
            var unsupported = Manifest("io.example.legacy");
            unsupported.SchemaVersion = ModManifest.ManifestV5SchemaVersion;
            var cases = new (ModManifest Manifest, ModNetworkClass Class)[]
            {
                (Manifest("io.example.plain"), ModNetworkClass.Shared),
                (Manifest("io.example.lobby", ModMultiplayerMetadata.SessionMode), ModNetworkClass.Shared),
                (Manifest("io.example.hats", ModMultiplayerMetadata.ClientLocalMode), ModNetworkClass.ClientLocal),
                (Manifest("io.example.rules", ModMultiplayerMetadata.ServerOnlyMode), ModNetworkClass.ServerOnly),
                (Manifest("io.example.solo", ModMultiplayerMetadata.StandaloneMode), ModNetworkClass.Standalone),
                (Manifest("io.example.future", "peer-to-peer"), ModNetworkClass.Standalone),
                (unsupported, ModNetworkClass.Standalone)
            };
            foreach (var (manifest, expected) in cases)
            {
                That(MultiplayerRuntime.NetworkClassOf(manifest) == expected, manifest.Id + " is " + expected);
                runtime.Host.Inventory.Add(manifest, "unused-" + manifest.Id);
            }

            foreach (var (manifest, expected) in cases)
            {
                That(runtime.TryGetNetworkClass(manifest.Id.ToUpperInvariant(), out var loaded) && loaded == expected,
                    "the runtime gives loaded " + manifest.Id + " its class, whatever the case of its id");
            }

            That(!runtime.TryGetNetworkClass("io.example.missing", out _), "a mod that did not load has no class");
            That(runtime.StandaloneMods.SequenceEqual(new[] { "Future", "Legacy", "Solo" }),
                "standalone mods, unreadable modes included, are named in order (" + string.Join(", ", runtime.StandaloneMods) + ")");
            runtime.Host.Inventory.Remove("io.example.solo");
            runtime.Host.Inventory.Remove("io.example.future");
            runtime.Host.Inventory.Remove("io.example.legacy");
            That(runtime.StandaloneMods.Count == 0 && runtime.EvaluateRealmChange(NetRealm.ListenHost).IsAllowed, "once they are gone, hosting is allowed");
            Throws<ArgumentNullException>(() => runtime.TryGetNetworkClass(null!, out _), "a mod id is required");
        }

        private static void StandaloneRefusalsNameTheMods()
        {
            That(MultiplayerRuntime.StandaloneRefusal(Array.Empty<string>(), "host") == "A standalone mod refuses multiplayer; disable it to host.", "none");
            That(MultiplayerRuntime.StandaloneRefusal(new[] { "Solo" }, "join") == "Solo refuses multiplayer; disable it to join.", "one");
            That(MultiplayerRuntime.StandaloneRefusal(new[] { "Solo", "Story" }, "host") == "Solo and Story refuse multiplayer; disable them to host.", "two");
            That(MultiplayerRuntime.StandaloneRefusal(new[] { "Solo", "Story", "Tutor" }, "host") == "Solo, Story and Tutor refuse multiplayer; disable them to host.", "three");
            That(MultiplayerRuntime.StandaloneRefusal(new[] { "A", "B", "C", "D", "E" }, "join") == "A, B, C and 2 more refuse multiplayer; disable them to join.", "five");
        }

        private static void ExpectHost(InternalSessionRole role, SessionPhase phase, bool standalone, NetRealmTransitionVerdict verdict, string? refusal) =>
            Expect(role, phase, standalone, verdict, refusal, runtime => runtime.OpenToMultiplayer(new InternalHostSettings("Rules", 4)), "host");

        private static void ExpectJoin(InternalSessionRole role, SessionPhase phase, bool standalone, NetRealmTransitionVerdict verdict, string? refusal) =>
            Expect(role, phase, standalone, verdict, refusal, runtime => runtime.Join(new FakeJoinTarget(), new InternalJoinSettings()), "join");

        private static void Expect(InternalSessionRole role, SessionPhase phase, bool standalone, NetRealmTransitionVerdict verdict, string? refusal,
            Func<MultiplayerRuntime, MultiplayerRealmChange> request, string call)
        {
            var (runtime, control) = InRealm(role, phase, standalone);
            var target = call == "host" ? NetRealm.ListenHost : NetRealm.Client;
            var asked = runtime.EvaluateRealmChange(target);
            var change = request(runtime);
            var label = call + " as " + role + " while " + phase + (standalone ? " with a standalone mod" : string.Empty);
            That(change.Verdict == verdict && asked.Equals(change.Transition), label + " is " + verdict + " (" + change + ")");
            if (refusal == null)
            {
                That(change.Succeeded && control.RealmCalls.SequenceEqual(new[] { call }), label + " reaches the provider");
                return;
            }

            var code = verdict == NetRealmTransitionVerdict.Busy ? ModErrorCode.Conflict : ModErrorCode.InvalidState;
            That(!change.Succeeded && change.Result.ErrorCode == code && change.Result.ErrorMessage == refusal && control.RealmCalls.Count == 0,
                label + " is refused with '" + refusal + "' before the provider hears of it (" + change.Result.ErrorMessage + ")");
        }

        private static (MultiplayerRuntime Runtime, FakeMultiplayerControl Control) InRealm(InternalSessionRole role, SessionPhase phase, bool standalone)
        {
            var (runtime, control, _) = Runtime(() => phase);
            if (standalone) runtime.Host.Inventory.Add(Manifest("io.example.solo", ModMultiplayerMetadata.StandaloneMode), "unused-solo");
            runtime.Connect();
            control.Role = role;
            Frame(runtime);
            return (runtime, control);
        }

        private static MultiplayerSpawnDescription Spawn(string mod, ModNetworkClass networkClass, ModExecutionContext context) =>
            new MultiplayerSpawnDescription(mod, new ModNetProfile(networkClass, context), MultiplayerSpawnSource.SdkSpawn,
                MultiplayerSpawnKinds.AssetPrefab, null, TransformState.Identity, null);

        private static MultiplayerSpawnDescription Content(string mod, string key) =>
            new MultiplayerSpawnDescription(mod, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.WorldOwner), MultiplayerSpawnSource.WorldContent,
                "worlds.arena", key, TransformState.Identity, null);
    }
}
