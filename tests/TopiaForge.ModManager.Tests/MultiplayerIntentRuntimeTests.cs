using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;
using static TopiaForge.ModManager.Tests.MultiplayerIntentTests;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The runtime's intent tracking (WS3.4): it follows the realm, the kit's hook opens UI activations and carries deferred
    /// callbacks, commands act for the player who ran them, the replication seam answers whose intent a call carries, and
    /// the motion lease and forwarded-request seams open what WS3.7 needs.
    /// </summary>
    internal static class MultiplayerIntentRuntimeTests
    {
        private const string Builder = "io.example.builder";
        private const string Neighbour = "io.example.neighbour";

        internal static void Run()
        {
            IntentFollowsTheRealm();
            UiActivationsComeThroughTheKitsHook();
            TheKitCarriesIntentOverWhatItDefers();
            TheKitSurvivesASinkThatFails();
            CommandsActForThePlayerWhoRanThem();
            TheSeamAnswersWhoseIntentACallCarries();
            TheMotionLeaseAndForwardedSeams();
            TheTrackingCarriesNoAddress();
            Console.WriteLine("MultiplayerIntentRuntimeTests passed.");
        }

        /// <summary>
        /// The privacy rule for intent tracking: it names players by participant id alone, so no member, parameter or type of
        /// it is shaped like an address or names a network type. The replication SPI's own scan covers the record it answers.
        /// </summary>
        private static void TheTrackingCarriesNoAddress()
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            var words = new[] { "address", "endpoint", "hostname", "socket" };
            var offenders = new List<string>();
            foreach (var type in new[]
                     {
                         typeof(IntentRecord), typeof(IntentScope), typeof(IntentScope.Token), typeof(IntentEntryRules), typeof(IntentFlowRules),
                         typeof(MultiplayerIntents), typeof(ModInputActivity), typeof(NetInputService), typeof(NetInputAction), typeof(NetScheduler),
                         typeof(NetInteractionService), typeof(InteractionIntent), typeof(MultiplayerIntentKitHook), typeof(InternalIntentRecord),
                         typeof(ITopiaForgeIntentSink), typeof(TopiaForgeIntent)
                     })
            {
                foreach (var member in type.GetMembers(all))
                {
                    if (member.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                    var name = member.Name.ToLowerInvariant();
                    if (Array.Exists(words, word => name.Contains(word, StringComparison.Ordinal))) offenders.Add(type.Name + "." + member.Name);
                    var types = member is System.Reflection.MethodInfo method
                        ? new[] { method.ReturnType }.Concat(Array.ConvertAll(method.GetParameters(), parameter => parameter.ParameterType))
                        : member is System.Reflection.PropertyInfo property ? new[] { property.PropertyType }
                        : member is System.Reflection.FieldInfo field ? new[] { field.FieldType } : Array.Empty<Type>();
                    foreach (var used in types)
                    {
                        if (used.Namespace != null && used.Namespace.StartsWith("System.Net", StringComparison.Ordinal)) offenders.Add(type.Name + "." + member.Name + " uses " + used.Name);
                    }

                    if (member is System.Reflection.MethodBase body)
                    {
                        foreach (var parameter in body.GetParameters())
                        {
                            var parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                            if (Array.Exists(words, word => parameterName.Contains(word, StringComparison.Ordinal))) offenders.Add(type.Name + "." + member.Name + "(" + parameter.Name + ")");
                        }
                    }
                }
            }

            True(offenders.Count == 0, "nothing in intent tracking is shaped like an address (" + string.Join(", ", offenders) + ")");
        }

        private static void IntentFollowsTheRealm()
        {
            var (runtime, control, _) = Seam.Runtime(() => SessionPhase.Idle);
            var intents = runtime.Intents;
            True(!intents.IsRecording && intents.Epoch == 0 && intents.Realm == NetRealm.Offline, "a runtime starts offline, recording nothing");
            runtime.Connect();
            control.Role = InternalSessionRole.ListenHost;
            True(!intents.IsRecording, "the realm moves only at a pump");
            Seam.Frame(runtime);
            True(intents.IsRecording && intents.Epoch == 1 && intents.Realm == NetRealm.ListenHost, "a listen host records, in a new epoch");
            var hosted = intents.LocalRecord(ref slot, Builder, IntentSource.UiCallback);
            using (intents.Scope.Enter(hosted))
            {
                True(Seen(intents, Builder) == "UiCallback/local", "the mod observes its own intent while it flows");
                control.Role = InternalSessionRole.Client;
                Seam.Frame(runtime);
                True(intents.Epoch == 2 && Seen(intents, Builder) == "none", "a change of realm ends it, even mid-scope (RealmChange)");
            }

            var joined = intents.LocalRecord(ref slot, Builder, IntentSource.UiCallback);
            True(!ReferenceEquals(joined, hosted) && joined.Epoch == 2, "the new epoch makes a new record");
            Seam.Frame(runtime);
            True(intents.Epoch == 2, "a pump that changes nothing keeps the epoch");
            control.Role = InternalSessionRole.Solo;
            Seam.Frame(runtime);
            True(!intents.IsRecording && intents.Epoch == 3, "single player records nothing");
            control.Role = InternalSessionRole.Client;
            Seam.Frame(runtime);
            runtime.Disconnect();
            True(!intents.IsRecording && intents.Epoch == 5 && intents.Realm == NetRealm.Offline, "detaching goes offline in an epoch of its own");
            True(intents.EnterUiActivation(Builder).Restore == null && intents.Scope.Flowing == null, "offline no entry point opens anything");
        }

        private static IntentRecord? slot;

        private static void UiActivationsComeThroughTheKitsHook()
        {
            var (runtime, _) = Online(InternalSessionRole.Client);
            var intents = runtime.Intents;
            var hook = new MultiplayerIntentKitHook(intents);
            var restore = hook.BeginPlayerAction(Builder);
            True(restore != null && Seen(intents, Builder) == "UiCallback/local", "a control's activation opens its owner's UI intent");
            True(Seen(intents, Neighbour) == "none", "which another mod's code inside it never sees");
            var nested = hook.BeginPlayerAction(Builder);
            True(nested == null, "the same owner's nested activation changes nothing");
            var other = hook.BeginPlayerAction(Neighbour);
            True(Seen(intents, Neighbour) == "UiCallback/local" && Seen(intents, Builder) == "none", "another owner's control inside it is that owner's");
            hook.End(other);
            True(Seen(intents, Builder) == "UiCallback/local", "and the outer activation returns after it");
            hook.End(restore);
            True(intents.Scope.Flowing == null, "ending the activation ends the scope");
            True(hook.BeginPlayerAction(string.Empty) == null, "a control without an owner acts for nobody");

            // The kit's own side: installed, the kit opens scopes through it; uninstalled, it opens nothing.
            TopiaForgeIntent.Reset();
            using (TopiaForgeIntent.PlayerAction(Builder)) True(intents.Scope.Flowing == null, "without a sink the kit opens nothing");
            TopiaForgeIntent.Install(hook);
            using (TopiaForgeIntent.PlayerAction(Builder)) True(Seen(intents, Builder) == "UiCallback/local", "with one the kit opens the owner's activation");
            using (TopiaForgeIntent.PlayerAction(null)) True(intents.Scope.Flowing == null, "a control without an owner opens nothing");
            True(intents.Scope.Flowing == null, "and the kit's scope ends with its block");
            TopiaForgeIntent.Uninstall(new MultiplayerIntentKitHook(intents));
            True(TopiaForgeIntent.IsInstalled, "another runtime's sink never removes this one");
            TopiaForgeIntent.Uninstall(hook);
            True(!TopiaForgeIntent.IsInstalled, "its own runtime removes it");

            // The cost of one activation, setting and restoring the record: the kit's controls fire on the player's input only.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1_000; index++) hook.End(hook.BeginPlayerAction(Builder));
            Console.WriteLine("A UI activation's scope on .NET: " + (GC.GetAllocatedBytesForCurrentThread() - before) / 1_000.0 + " bytes each.");
        }

        private static void TheKitCarriesIntentOverWhatItDefers()
        {
            var (runtime, control) = Online(InternalSessionRole.Client);
            var intents = runtime.Intents;
            var hook = new MultiplayerIntentKitHook(intents);
            True(hook.Capture() == null, "scripted code captures nothing for the kit to carry");
            var restore = hook.BeginPlayerAction(Builder);
            var captured = hook.Capture();
            hook.End(restore);
            True(captured is IntentRecord && intents.Scope.Flowing == null, "a close animation begun by the player's click captures the click's intent");
            var resumed = hook.Resume(captured);
            True(Seen(intents, Builder) == "UiCallback/local", "its completion, frames later, runs with it (DeferredUiCallback)");
            hook.End(resumed);
            True(intents.Scope.Flowing == null, "and ends with it");

            // What another mod's code captured is carried for that mod alone; after a realm change, for nobody.
            var neighbour = hook.BeginPlayerAction(Neighbour);
            var theirs = hook.Capture();
            hook.End(neighbour);
            var back = hook.Resume(theirs);
            True(Seen(intents, Neighbour) == "UiCallback/local" && Seen(intents, Builder) == "none", "a captured intent keeps its own mod");
            hook.End(back);
            control.Role = InternalSessionRole.Solo;
            Seam.Frame(runtime);
            control.Role = InternalSessionRole.Client;
            Seam.Frame(runtime);
            var stale = hook.Resume(captured);
            True(stale == null && intents.Scope.Flowing == null, "a completion after the realm changed runs scripted");
            hook.End(stale);
        }

        private static void TheKitSurvivesASinkThatFails()
        {
            TopiaForgeIntent.Reset();
            var failing = new FailingSink();
            TopiaForgeIntent.Install(failing);
            var ran = false;
            using (TopiaForgeIntent.PlayerAction(Builder)) ran = true;
            var captured = TopiaForgeIntent.Capture();
            using (TopiaForgeIntent.Resume(captured)) { }
            True(ran && captured == null && TopiaForgeIntent.SinkFailures == 3, "a sink that throws loses its scopes, never the click (" + TopiaForgeIntent.SinkFailures + " failures)");
            failing.FailOnEnd = true;
            failing.FailOnBegin = false;
            using (TopiaForgeIntent.PlayerAction(Builder)) { }
            True(TopiaForgeIntent.SinkFailures == 4, "nor the end of one");
            TopiaForgeIntent.Reset();
            True(!TopiaForgeIntent.IsInstalled && TopiaForgeIntent.SinkFailures == 0, "teardown resets the kit's hook");
        }

        private static void CommandsActForThePlayerWhoRanThem()
        {
            var (runtime, _) = Online(InternalSessionRole.ListenHost);
            var intents = runtime.Intents;
            var registry = new ModServiceRegistry();
            var builder = new OwnerCommandService(Builder, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var neighbour = new OwnerCommandService(Neighbour, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var heard = new List<string>();
            neighbour.Register(new CommandDefinition("ping", "Ping"), _ => Answer(heard, "ping", intents, Neighbour));
            builder.Register(new CommandDefinition("boost", "Boost"), _ => Answer(heard, "boost", intents, Builder));

            builder.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            var click = intents.EnterUiActivation(Builder);
            builder.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            builder.TryExecute("boost", Array.Empty<string>(), out _);
            True(Seen(intents, Builder) == "UiCallback/local", "the click's own intent returns after each command");
            click.Dispose();

            // The neighbour's own intent never reaches its command through a mod that saw none.
            var borrowed = intents.EnterUiActivation(Neighbour);
            builder.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            borrowed.Dispose();

            registry.TryExecutePlayerCommand(Builder + ":boost", Array.Empty<string>(), intents, out var played);
            using (intents.EnterForwarded(Builder, IntentSource.InteractionHandler, "participant-2"))
            {
                builder.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            }

            Sequence(new[]
            {
                "ping:none", "ping:OwnerCommand/local", "boost:OwnerCommand/local", "ping:none", "boost:OwnerCommand/local", "ping:OwnerCommand/participant-2"
            }, heard, "a command acts for the player whose intent ran it, as its own mod, and scripted runs carry none (CommandRun)");
            True(played != null && played.Succeeded, "the player's own command ran");
            True(!registry.TryExecutePlayerCommand("boost", Array.Empty<string>(), intents, out _) &&
                 !registry.TryExecutePlayerCommand(Builder + ":missing", Array.Empty<string>(), intents, out _),
                "the player's command needs a qualified name of a registered command");
            True(intents.Scope.Flowing == null, "and nothing is left flowing");

            static OperationResult<string> Answer(List<string> heard, string name, MultiplayerIntents intents, string modId)
            {
                heard.Add(name + ":" + Seen(intents, modId));
                return OperationResult<string>.Success(name);
            }
        }

        private static void TheSeamAnswersWhoseIntentACallCarries()
        {
            foreach (var role in new[] { InternalSessionRole.Solo, InternalSessionRole.ListenHost, InternalSessionRole.Client })
            {
                var (runtime, _) = Online(role);
                var services = new MultiplayerReplication(runtime);
                var shared = services.Bind(Builder, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime());
                var server = services.Bind(Builder, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), new OwnerModLifetime());
                var cosmetic = services.Bind(Builder, new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process), new OwnerModLifetime());
                var other = services.Bind(Neighbour, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime());
                var realm = runtime.Realm;
                True(!shared.CurrentIntent.IsPlayerIntent && shared.CurrentIntent.Equals(default(InternalIntentRecord)), realm + ": scripted code carries none");
                True(shared.RouteCurrentIntent() == shared.Route(InternalIntent.None), realm + ": and routes as scripted");
                using (runtime.Intents.EnterUiActivation(Builder))
                {
                    var expected = role == InternalSessionRole.Solo ? InternalIntent.None : InternalIntent.UiCallback;
                    True(shared.CurrentIntent.Source == expected && (expected == InternalIntent.None || shared.CurrentIntent.IsLocalPlayer),
                        realm + ": a click carries the local player's UI intent online, and nothing offline (" + shared.CurrentIntent + ")");
                    True(shared.RouteCurrentIntent() == shared.Route(expected), realm + ": and routes by it");
                    True(!other.CurrentIntent.IsPlayerIntent, realm + ": another mod's seam sees none");
                    True(server.CurrentIntent.Source == expected, realm + ": every context of the mod sees its intent");
                    if (role == InternalSessionRole.Client)
                    {
                        True(shared.RouteCurrentIntent() == InternalRoute.Forwarded, "a joined player's click forwards to the host");
                        True(server.RouteCurrentIntent() == InternalRoute.NotRun, "a gamemode server never runs on a joined player");
                        True(cosmetic.RouteCurrentIntent() == InternalRoute.LocalOnly, "a client-local mod's stays local");
                    }
                }

                if (role == InternalSessionRole.Client) True(shared.RouteCurrentIntent() == InternalRoute.Shadow, "a joined player's scripted call is a shadow of the host's");
                True(shared.Intents == runtime.Intents && shared.VisibleIntent == null, realm + ": the seam reads the runtime's own tracking");
            }

            True(new InternalIntentRecord(InternalIntent.OwnerCommand, "participant-4").ToString() == "OwnerCommand of a participant",
                "a record never writes out its participant's id");
            Throws<ArgumentException>(() => new InternalIntentRecord(InternalIntent.None, "participant-4"), "scripted code acts for nobody");
            Throws<ArgumentException>(() => new InternalIntentRecord(InternalIntent.UiCallback, " spaced"), "a participant id is printable text");
            Throws<ArgumentOutOfRangeException>(() => new InternalIntentRecord((InternalIntent)6, null), "an undefined source");
        }

        private static void TheMotionLeaseAndForwardedSeams()
        {
            var (host, _) = Online(InternalSessionRole.ListenHost);
            var intents = host.Intents;
            using (intents.EnterMotionLease(Builder)) True(Seen(intents, Builder) == "MotionLease/local", "the motion lease seam opens the holder's intent");
            using (intents.EnterForwarded(Builder, IntentSource.UiCallback, "participant-9"))
            {
                True(Seen(intents, Builder) == "UiCallback/participant-9", "a host acts for the participant whose request it carries out");
                True(intents.VisibleTo(Builder)!.ParticipantId == "participant-9" && !intents.VisibleTo(Builder)!.IsLocalPlayer, "by its participant id alone");
            }

            Throws<ArgumentOutOfRangeException>(() => intents.EnterForwarded(Builder, IntentSource.None, "participant-9"), "a forwarded request carries intent");
            Throws<ArgumentException>(() => intents.EnterForwarded(Builder, IntentSource.UiCallback, " participant-9"), "and names its participant by a bounded id");
            Throws<ArgumentException>(() => intents.EnterForwarded(Builder, IntentSource.UiCallback, new string('p', 129)), "of at most 128 characters");
            var (client, _) = Online(InternalSessionRole.Client);
            using (client.Intents.EnterForwarded(Builder, IntentSource.UiCallback, "participant-9"))
            {
                True(client.Intents.Scope.Flowing == null, "a joined player never acts for another participant");
            }

            var (offline, _) = Online(InternalSessionRole.Solo);
            using (offline.Intents.EnterMotionLease(Builder)) True(offline.Intents.Scope.Flowing == null, "offline the lease opens nothing");
        }

        private sealed class FailingSink : ITopiaForgeIntentSink
        {
            internal bool FailOnBegin { get; set; } = true;

            internal bool FailOnEnd { get; set; }

            public object? BeginPlayerAction(string ownerId) => FailOnBegin ? throw new InvalidOperationException("broken sink") : new object();

            public object? Capture() => throw new InvalidOperationException("broken sink");

            public object? Resume(object? captured) => throw new InvalidOperationException("broken sink");

            public void End(object? restore)
            {
                if (FailOnEnd) throw new InvalidOperationException("broken sink");
            }
        }
    }
}
