using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Realm, network-class, execution-context and intent routing rules (WS3.1 and WS3.4).</summary>
    internal static class MultiplayerRealmRulesTests
    {
        private static readonly NetRealm[] Realms = { NetRealm.Offline, NetRealm.ListenHost, NetRealm.Client };

        private static readonly ModExecutionContext[] Contexts =
        {
            ModExecutionContext.Process,
            ModExecutionContext.WorldOwner,
            ModExecutionContext.GamemodeServer,
            ModExecutionContext.Participant
        };

        private static readonly ModNetworkClass[] Classes =
        {
            ModNetworkClass.Shared,
            ModNetworkClass.ClientLocal,
            ModNetworkClass.ServerOnly,
            ModNetworkClass.Standalone
        };

        public static void Run()
        {
            ClassifiesManifests();
            ClassesRunWhereThePlanSays();
            ContextsRunWhereThePlanSays();
            RealmTransitionsFollowThePlan();
            RealmTransitionInvariantsHoldForEveryInput();
            IntentSourcesAreClassified();
            RoutesFollowTheExecutionModelTable();
            TimePauseAndLocalPlayerRules();
            Console.WriteLine("MultiplayerRealmRulesTests passed.");
        }

        private static void ClassifiesManifests()
        {
            Equal(ModNetworkClass.Shared, ModNetworkClasses.FromEffectiveMode(ModMultiplayerEffectiveMode.ImplicitShared), "implicit");
            Equal(ModNetworkClass.Shared, ModNetworkClasses.FromEffectiveMode(ModMultiplayerEffectiveMode.Session), "session");
            Equal(ModNetworkClass.ClientLocal, ModNetworkClasses.FromEffectiveMode(ModMultiplayerEffectiveMode.ClientLocal), "client-local");
            Equal(ModNetworkClass.ServerOnly, ModNetworkClasses.FromEffectiveMode(ModMultiplayerEffectiveMode.ServerOnly), "server-only");
            Equal(ModNetworkClass.Standalone, ModNetworkClasses.FromEffectiveMode(ModMultiplayerEffectiveMode.Standalone), "standalone");
            Throws<ArgumentOutOfRangeException>(() => ModNetworkClasses.FromEffectiveMode((ModMultiplayerEffectiveMode)99), "undefined mode");

            True(ModNetworkClasses.TryFromManifest(Manifest(null), out var implicitClass), "implicit manifest reads");
            Equal(ModNetworkClass.Shared, implicitClass, "a mod with no multiplayer block is implicitly shared");
            Equal(ModNetworkClass.Shared, ModNetworkClasses.Classify(Manifest("session")), "declared session mod");
            Equal(ModNetworkClass.ClientLocal, ModNetworkClasses.Classify(Manifest("client-local")), "client-local opts out");
            Equal(ModNetworkClass.ServerOnly, ModNetworkClasses.Classify(Manifest("server-only")), "server-only");
            Equal(ModNetworkClass.Standalone, ModNetworkClasses.Classify(Manifest("standalone")), "standalone refuses multiplayer");

            True(!ModNetworkClasses.TryFromManifest(Manifest("lan-party"), out var unknown), "an unknown mode is unreadable");
            Equal(ModNetworkClass.Standalone, unknown, "an unreadable mode never goes online");
            var oldSchema = Manifest(null);
            oldSchema.SchemaVersion = ModManifest.ManifestV5SchemaVersion;
            True(!ModNetworkClasses.TryFromManifest(oldSchema, out _), "an unsupported schema is unreadable");
            Equal(ModNetworkClass.Standalone, ModNetworkClasses.Classify(oldSchema), "an unsupported schema never goes online");
            Throws<ArgumentNullException>(() => ModNetworkClasses.Classify(null!), "null manifest");
            True(ModNetworkClasses.RefusesMultiplayer(ModNetworkClass.Standalone), "standalone refuses");
            True(!ModNetworkClasses.RefusesMultiplayer(ModNetworkClass.ClientLocal), "client-local does not refuse");
        }

        private static void ClassesRunWhereThePlanSays()
        {
            // Offline, ListenHost, Client
            Expect(ModNetworkClass.Shared, true, true, true);
            Expect(ModNetworkClass.ClientLocal, true, true, true);
            Expect(ModNetworkClass.ServerOnly, true, true, false);
            Expect(ModNetworkClass.Standalone, true, false, false);
            Throws<ArgumentOutOfRangeException>(() => ModNetworkClasses.RunsOn((ModNetworkClass)7, NetRealm.Offline), "undefined class");
            Throws<ArgumentOutOfRangeException>(() => ModNetworkClasses.RunsOn(ModNetworkClass.Shared, (NetRealm)3), "undefined realm");

            static void Expect(ModNetworkClass networkClass, bool offline, bool host, bool client)
            {
                Equal(offline, ModNetworkClasses.RunsOn(networkClass, NetRealm.Offline), networkClass + " offline");
                Equal(host, ModNetworkClasses.RunsOn(networkClass, NetRealm.ListenHost), networkClass + " on the host");
                Equal(client, ModNetworkClasses.RunsOn(networkClass, NetRealm.Client), networkClass + " on a client");
            }
        }

        private static void ContextsRunWhereThePlanSays()
        {
            foreach (var realm in Realms)
            {
                True(ModExecutionContexts.RunsOn(ModExecutionContext.Process, realm), "Process runs on every peer");
                True(ModExecutionContexts.RunsOn(ModExecutionContext.WorldOwner, realm), "WorldOwner runs on every peer");
                True(ModExecutionContexts.RunsOn(ModExecutionContext.Participant, realm), "Participant runs on every interactive peer");
            }

            True(ModExecutionContexts.RunsOn(ModExecutionContext.GamemodeServer, NetRealm.Offline), "single player runs the factory");
            True(ModExecutionContexts.RunsOn(ModExecutionContext.GamemodeServer, NetRealm.ListenHost), "the host runs the factory");
            True(!ModExecutionContexts.RunsOn(ModExecutionContext.GamemodeServer, NetRealm.Client), "a client never runs the factory");
            True(ModExecutionContexts.IsPerParticipant(ModExecutionContext.Participant), "participant is per player");
            True(!ModExecutionContexts.IsPerParticipant(ModExecutionContext.GamemodeServer), "the server is per process");
            Throws<ArgumentOutOfRangeException>(() => ModExecutionContexts.RunsOn((ModExecutionContext)4, NetRealm.Offline), "undefined context");
            Throws<ArgumentOutOfRangeException>(() => new ModNetProfile(ModNetworkClass.Shared, (ModExecutionContext)9), "profile context");
            Throws<ArgumentOutOfRangeException>(() => new ModNetProfile((ModNetworkClass)9, ModExecutionContext.Process), "profile class");

            var serverOnlyGamemode = new ModNetProfile(ModNetworkClass.ServerOnly, ModExecutionContext.GamemodeServer);
            True(serverOnlyGamemode.RunsOn(NetRealm.ListenHost) && !serverOnlyGamemode.RunsOn(NetRealm.Client), "both gates apply");
            var standaloneParticipant = new ModNetProfile(ModNetworkClass.Standalone, ModExecutionContext.Participant);
            True(standaloneParticipant.RunsOn(NetRealm.Offline) && !standaloneParticipant.RunsOn(NetRealm.ListenHost), "standalone gate");
        }

        private static void RealmTransitionsFollowThePlan()
        {
            Expect(NetRealm.Offline, NetRealm.ListenHost, SessionPhase.Running, false,
                NetRealmTransitionVerdict.Allowed, NetRealmTransitionEffect.PromoteInPlace, "open to multiplayer mid-session");
            Expect(NetRealm.Offline, NetRealm.ListenHost, SessionPhase.Idle, false,
                NetRealmTransitionVerdict.Allowed, NetRealmTransitionEffect.StartAsHost, "start as host from idle");
            Expect(NetRealm.Offline, NetRealm.Client, SessionPhase.Idle, false,
                NetRealmTransitionVerdict.Allowed, NetRealmTransitionEffect.ReplaceLocalSession, "join from idle replaces the session");
            Expect(NetRealm.Offline, NetRealm.Client, SessionPhase.Running, false,
                NetRealmTransitionVerdict.RequiresLeave, NetRealmTransitionEffect.None, "join while playing asks to leave first");
            foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Stopping })
            {
                Expect(NetRealm.Offline, NetRealm.ListenHost, phase, false,
                    NetRealmTransitionVerdict.Busy, NetRealmTransitionEffect.None, "no promotion during " + phase);
                Expect(NetRealm.Offline, NetRealm.Client, phase, false,
                    NetRealmTransitionVerdict.Busy, NetRealmTransitionEffect.None, "no join during " + phase);
            }

            Expect(NetRealm.Offline, NetRealm.ListenHost, SessionPhase.Running, true,
                NetRealmTransitionVerdict.StandaloneRefused, NetRealmTransitionEffect.None, "standalone blocks promotion");
            Expect(NetRealm.Offline, NetRealm.Client, SessionPhase.Idle, true,
                NetRealmTransitionVerdict.StandaloneRefused, NetRealmTransitionEffect.None, "standalone blocks joining");
            Expect(NetRealm.ListenHost, NetRealm.Client, SessionPhase.Running, false,
                NetRealmTransitionVerdict.RequiresLeave, NetRealmTransitionEffect.None, "a host must stop hosting to join");
            Expect(NetRealm.Client, NetRealm.ListenHost, SessionPhase.Running, false,
                NetRealmTransitionVerdict.RequiresLeave, NetRealmTransitionEffect.None, "a client must leave to host");
            Expect(NetRealm.Client, NetRealm.Offline, SessionPhase.LoadingWorld, false,
                NetRealmTransitionVerdict.Allowed, NetRealmTransitionEffect.EndOnlineSession, "leaving works mid-load");
            Expect(NetRealm.Client, NetRealm.Client, SessionPhase.Running, false,
                NetRealmTransitionVerdict.Unchanged, NetRealmTransitionEffect.None, "same realm");
            Throws<ArgumentOutOfRangeException>(
                () => NetRealms.EvaluateTransition(NetRealm.Offline, NetRealm.Client, (SessionPhase)17, false), "undefined phase");
            Throws<ArgumentOutOfRangeException>(
                () => NetRealms.EvaluateTransition((NetRealm)(-1), NetRealm.Client, SessionPhase.Idle, false), "undefined realm");

            static void Expect(
                NetRealm from,
                NetRealm to,
                SessionPhase phase,
                bool standalone,
                NetRealmTransitionVerdict verdict,
                NetRealmTransitionEffect effect,
                string message)
            {
                var transition = NetRealms.EvaluateTransition(from, to, phase, standalone);
                Equal(new NetRealmTransition(verdict, effect), transition, message);
                Equal(verdict == NetRealmTransitionVerdict.Allowed, transition.IsAllowed, message + " (allowed flag)");
            }
        }

        private static void RealmTransitionInvariantsHoldForEveryInput()
        {
            var checkedCases = 0;
            var standaloneOverBusy = 0;
            foreach (var from in Realms)
            {
                foreach (var to in Realms)
                {
                    for (var phase = SessionPhase.Idle; phase <= SessionPhase.Stopping; phase++)
                    {
                        foreach (var standalone in new[] { false, true })
                        {
                            var transition = NetRealms.EvaluateTransition(from, to, phase, standalone);
                            var name = from + " to " + to + " during " + phase + (standalone ? " with a standalone mod" : string.Empty);
                            var busy = phase != SessionPhase.Idle && phase != SessionPhase.Running;
                            checkedCases++;
                            if (!transition.IsAllowed) Equal(NetRealmTransitionEffect.None, transition.Effect, name + ": refusals have no effect");
                            if (from == to)
                            {
                                Equal(NetRealmTransitionVerdict.Unchanged, transition.Verdict, name + ": same realm is unchanged");
                                continue;
                            }

                            if (to == NetRealm.Offline)
                            {
                                Equal(Allowed(NetRealmTransitionEffect.EndOnlineSession), transition, name + ": leaving is never refused");
                                continue;
                            }

                            // The refusals in precedence order: an online realm must leave first, whatever else holds;
                            // then a standalone mod is named before a busy phase, so the player hears the lasting reason.
                            if (from != NetRealm.Offline)
                            {
                                Equal(Refused(NetRealmTransitionVerdict.RequiresLeave), transition, name + ": online realms never switch directly");
                                continue;
                            }

                            if (standalone)
                            {
                                Equal(Refused(NetRealmTransitionVerdict.StandaloneRefused), transition, name + ": a standalone mod never goes online");
                                if (busy) standaloneOverBusy++;
                                continue;
                            }

                            if (busy)
                            {
                                Equal(Refused(NetRealmTransitionVerdict.Busy), transition, name + ": busy phases never go online");
                                continue;
                            }

                            var expected = to == NetRealm.ListenHost
                                ? Allowed(phase == SessionPhase.Idle ? NetRealmTransitionEffect.StartAsHost : NetRealmTransitionEffect.PromoteInPlace)
                                : phase == SessionPhase.Idle
                                    ? Allowed(NetRealmTransitionEffect.ReplaceLocalSession)
                                    : Refused(NetRealmTransitionVerdict.RequiresLeave);
                            Equal(expected, transition, name);
                        }
                    }
                }
            }

            Equal(108, checkedCases, "every realm, phase and standalone combination");
            Equal(8, standaloneOverBusy, "a standalone mod outranks each busy phase, hosting or joining");

            static NetRealmTransition Allowed(NetRealmTransitionEffect effect) =>
                new NetRealmTransition(NetRealmTransitionVerdict.Allowed, effect);

            static NetRealmTransition Refused(NetRealmTransitionVerdict verdict) =>
                new NetRealmTransition(verdict, NetRealmTransitionEffect.None);
        }

        private static void IntentSourcesAreClassified()
        {
            True(!IntentRules.IsPlayerIntent(IntentSource.None), "scripted code is not intent");
            foreach (var source in new[]
                     {
                         IntentSource.InputActiveFrame, IntentSource.UiCallback, IntentSource.OwnerCommand,
                         IntentSource.InteractionHandler, IntentSource.MotionLease
                     })
            {
                True(IntentRules.IsPlayerIntent(source), source + " is player intent");
            }

            Throws<ArgumentOutOfRangeException>(() => IntentRules.IsPlayerIntent((IntentSource)6), "undefined source");
        }

        private static void RoutesFollowTheExecutionModelTable()
        {
            var process = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process);
            var gamemode = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer);
            var participant = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant);
            var world = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.WorldOwner);

            Equal(ExecutionRoute.Canonical, ExecutionRouting.Route(NetRealm.ListenHost, process, IntentSource.UiCallback), "host intent");
            Equal(ExecutionRoute.Canonical, ExecutionRouting.Route(NetRealm.ListenHost, process, IntentSource.None), "host scripted");
            Equal(ExecutionRoute.Forwarded, ExecutionRouting.Route(NetRealm.Client, process, IntentSource.InputActiveFrame), "client intent");
            Equal(ExecutionRoute.Shadow, ExecutionRouting.Route(NetRealm.Client, process, IntentSource.None), "client scripted shadow");
            Equal(ExecutionRoute.Shadow, ExecutionRouting.Route(NetRealm.Client, world, IntentSource.None), "client world build");
            Equal(ExecutionRoute.Forwarded, ExecutionRouting.Route(NetRealm.Client, world, IntentSource.InteractionHandler), "world intent");
            Equal(ExecutionRoute.Canonical, ExecutionRouting.Route(NetRealm.ListenHost, gamemode, IntentSource.None), "gamemode on host");
            Equal(ExecutionRoute.NotRun, ExecutionRouting.Route(NetRealm.Client, gamemode, IntentSource.OwnerCommand), "gamemode on client");
            Equal(ExecutionRoute.Canonical, ExecutionRouting.Route(NetRealm.ListenHost, participant, IntentSource.None), "host's participant");
            Equal(ExecutionRoute.Forwarded, ExecutionRouting.Route(NetRealm.Client, participant, IntentSource.None), "client participant");
            Equal(ExecutionRoute.Forwarded, ExecutionRouting.Route(NetRealm.Client, process, IntentSource.MotionLease), "motion lease");

            var checkedCases = 0;
            foreach (var realm in Realms)
            {
                foreach (var networkClass in Classes)
                {
                    foreach (var context in Contexts)
                    {
                        for (var source = IntentSource.None; source <= IntentSource.MotionLease; source++)
                        {
                            var profile = new ModNetProfile(networkClass, context);
                            var route = ExecutionRouting.Route(realm, profile, source);
                            checkedCases++;
                            if (!profile.RunsOn(realm))
                            {
                                Equal(ExecutionRoute.NotRun, route, profile + " does not run on " + realm);
                                continue;
                            }

                            if (networkClass == ModNetworkClass.ClientLocal)
                            {
                                Equal(ExecutionRoute.LocalOnly, route, "client-local is never networked");
                                continue;
                            }

                            if (realm != NetRealm.Client)
                            {
                                Equal(ExecutionRoute.Canonical, route, "offline and on the host everything is canonical");
                                continue;
                            }

                            var expected = context == ModExecutionContext.Participant || source != IntentSource.None
                                ? ExecutionRoute.Forwarded
                                : ExecutionRoute.Shadow;
                            Equal(expected, route, profile + " with " + source + " on a client");
                        }
                    }
                }
            }

            Equal(288, checkedCases, "every realm, class, context and source combination");
        }

        private static void TimePauseAndLocalPlayerRules()
        {
            // A client-local mod's gamemode server on the host used to control everyone's time.
            var localGamemode = new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.GamemodeServer);
            Equal(TimeControlRoute.NotAuthoritative, ExecutionRouting.TimeControl(NetRealm.ListenHost, localGamemode), "a client-local gamemode on the host");
            Equal(TimeControlRoute.Apply, ExecutionRouting.TimeControl(NetRealm.Offline, localGamemode), "keeps Chronos in single player");
            var serverGamemode = new ModNetProfile(ModNetworkClass.ServerOnly, ModExecutionContext.GamemodeServer);
            Equal(TimeControlRoute.Apply, ExecutionRouting.TimeControl(NetRealm.ListenHost, serverGamemode), "a server-only gamemode on the host");

            var checkedCases = 0;
            foreach (var networkClass in Classes)
            {
                foreach (var context in Contexts)
                {
                    var profile = new ModNetProfile(networkClass, context);
                    checkedCases++;
                    Equal(TimeControlRoute.Apply, ExecutionRouting.TimeControl(NetRealm.Offline, profile), profile + ": single player keeps Chronos");
                    Equal(TimeControlRoute.NotAuthoritative, ExecutionRouting.TimeControl(NetRealm.Client, profile), profile + ": clients follow the scale");
                    var networked = networkClass == ModNetworkClass.Shared || networkClass == ModNetworkClass.ServerOnly;
                    var expected = context == ModExecutionContext.GamemodeServer && networked
                        ? TimeControlRoute.Apply
                        : TimeControlRoute.NotAuthoritative;
                    Equal(expected, ExecutionRouting.TimeControl(NetRealm.ListenHost, profile), profile + ": host time control");
                }
            }

            Equal(16, checkedCases, "every class and context in every realm");

            Equal(NativePauseRoute.FreezesWorld, ExecutionRouting.NativePause(NetRealm.Offline), "single player pause");
            Equal(NativePauseRoute.LocalMenuOnly, ExecutionRouting.NativePause(NetRealm.ListenHost), "the host never freezes the world");
            Equal(NativePauseRoute.LocalMenuOnly, ExecutionRouting.NativePause(NetRealm.Client), "a client never freezes the world");
            Equal(LocalPlayerRoute.Local, ExecutionRouting.LocalPlayerAction(NetRealm.Offline), "single player damage");
            Equal(LocalPlayerRoute.OwnerAuthoritative, ExecutionRouting.LocalPlayerAction(NetRealm.ListenHost), "host player damage");
            Equal(LocalPlayerRoute.OwnerAuthoritative, ExecutionRouting.LocalPlayerAction(NetRealm.Client), "client player damage");
        }

        private static ModManifest Manifest(string? mode) =>
            new ModManifest
            {
                SchemaVersion = ModManifest.ManifestV6SchemaVersion,
                Id = "io.github.example.realm",
                Name = "Realm",
                Version = "1.0.0",
                Multiplayer = mode == null ? null : new ModMultiplayerMetadata { Mode = mode }
            };
    }
}
