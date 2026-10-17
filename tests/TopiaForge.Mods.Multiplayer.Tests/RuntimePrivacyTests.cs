extern alias provider;

using System.Reflection;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderDiagnostic = provider::TopiaForge.Multiplayer.Engine.EngineDiagnostic;
using ProviderDiagnosticKind = provider::TopiaForge.Multiplayer.Engine.EngineDiagnosticKind;
using RoutedDiagnostics = provider::TopiaForge.Multiplayer.RoutedDiagnostics;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// No address crosses the internal SPI. Its types have no address-shaped member or parameter and no network type; a
/// typed host and port stays inside the provider's handle; and what the runtime logs and keeps for diagnostics is
/// redacted, whatever text reaches it, as is what the provider logs on its own.
/// </summary>
internal static class RuntimePrivacyTests
{
    private static readonly string[] AddressWords = { "address", "endpoint", "hostname", "socket" };

    internal static void TheSpiCarriesNoAddress()
    {
        var spi = typeof(IInternalMultiplayerControl).Assembly.GetTypes()
            .Where(type => type.Namespace == "TopiaForge.Mods.Internal" && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToArray();
        That(spi.Length >= 30 && spi.Contains(typeof(IInternalMultiplayerRuntimeHost)) && spi.Contains(typeof(IVoicePlaybackStream)),
            "the scan covers the whole SPI (" + spi.Length + " types)");
        var offenders = new List<string>();
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in spi)
        {
            if (IsAddressShaped(type.Name)) offenders.Add(type.Name);
            foreach (var member in type.GetMembers(All))
            {
                if (member.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                if (IsAddressShaped(member.Name) || Types(member).Any(IsNetworkType)) offenders.Add(type.Name + "." + member.Name);
                foreach (var parameter in Parameters(member))
                {
                    if (parameter.Name != null && IsAddressShaped(parameter.Name)) offenders.Add(type.Name + "." + member.Name + "(" + parameter.Name + ")");
                }
            }
        }

        That(offenders.Count == 0, "no SPI member, parameter or type is shaped like an address (" + string.Join(", ", offenders) + ")");
    }

    internal static void TypedHostsStayInsideTheProvider()
    {
        using var rig = new ControlRig(170);
        var peer = rig.AddPeer("typing");
        var serverId = new string('4', 64);
        var cases = new (string Text, InternalJoinTargetKind Kind, string Description, string? ServerId)[]
        {
            ("203.0.113.5:27260", InternalJoinTargetKind.Direct, "direct session", null),
            ("  192.168.1.20  ", InternalJoinTargetKind.Direct, "direct session", null),
            ("[2001:db8::7]:27261", InternalJoinTargetKind.Direct, "direct session", null),
            ("2001:db8::7", InternalJoinTargetKind.Direct, "direct session", null),
            ("play.example.org:27262", InternalJoinTargetKind.Direct, "direct session", null),
            ("topiaforge://join/direct/" + serverId + "/198.51.100.9/27263", InternalJoinTargetKind.Direct, "direct session", serverId),
            ("topiaforge://join/server/" + serverId, InternalJoinTargetKind.ListedServer, "listed server", serverId),
            ("abcde-12345", InternalJoinTargetKind.Invite, "invite ABCDE-12345", null)
        };

        foreach (var (text, kind, description, expectedServer) in cases)
        {
            That(peer.Control.TryCreateJoinTarget(text, out var target), "'" + text + "' makes a target");
            That(target!.Kind == kind && target.Description == description && target.ServerId == expectedServer && target.ToString() == description,
                "'" + text + "' is a " + kind + " shown as '" + description + "'");
            foreach (var secret in new[] { "203.0.113.5", "192.168.1.20", "2001:db8", "play.example.org", "198.51.100.9", "2726" })
            {
                That(!Strings(target).Any(value => value.Contains(secret, StringComparison.OrdinalIgnoreCase)), "'" + text + "' shows no address in its handle");
            }
        }

        foreach (var text in new[] { "", "   ", "host:0", "host:65536", "host:027250", "[::1", "a b", "topiaforge://join/nope/x", "-bad-.example" })
        {
            That(!peer.Control.TryCreateJoinTarget(text, out var refused) && refused == null, "'" + text + "' is no target");
        }

        // A join that fails at a typed address reports why without it: in the status, the log and the diagnostics.
        rig.Resolver.Override = ResolvedTarget.Failed(LiveFailure.Unreachable, "The host did not answer.");
        That(peer.Control.Join(peer.Target("203.0.113.5:27260"), new InternalJoinSettings { AllowDirect = true }).Succeeded, "the join starts");
        That(rig.Run(() => peer.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and fails");
        var status = peer.Control.GetStatus();
        var texts = new List<string> { status.JoinDetail };
        texts.AddRange(status.ContentProblems);
        texts.AddRange(peer.Log.Lines);
        texts.AddRange(peer.Runtime.RecentDiagnostics.Select(record => record.Text));
        That(status.JoinFailure == InternalLiveFailure.Unreachable && peer.Runtime.RecentDiagnostics.Any(record => record.Code == "live.join-ended"),
            "the failure is reported (" + status.JoinFailure + ")");
        That(!texts.Any(text => text.Contains("203.0.113.5", StringComparison.Ordinal) || text.Contains("27260", StringComparison.Ordinal)),
            "and nothing the player or the logs see names the address");
    }

    internal static void TheRuntimeRedactsWhatItLogs()
    {
        var log = new RecordingMultiplayerLog();
        var host = new MultiplayerRuntimeHost("2545", "0.2.0", log);
        host.ReportDiagnostic(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Warning, "test.leak-check",
            "peer 203.0.113.5:27250 and [2001:db8::1]:27250 via topiaforge://join/direct/" + new string('a', 64) + "/198.51.100.2/27250",
            new ParticipantId("p1"), new InvalidOperationException("relay at fe80::1%3 refused play.example.org:443")));
        var logged = log.Lines.Single();
        That(logged.StartsWith("warn: Multiplayer [test.leak-check]: ", StringComparison.Ordinal) && logged.Contains("(participant p1)", StringComparison.Ordinal) &&
             logged.Contains("InvalidOperationException", StringComparison.Ordinal), "a diagnostic keeps its code, participant and failure type");
        foreach (var secret in new[] { "203.0.113.5", "2001:db8", "198.51.100.2", "fe80::1", "play.example.org" })
        {
            That(!logged.Contains(secret, StringComparison.Ordinal) && !host.RecentDiagnostics.Single().Text.Contains(secret, StringComparison.Ordinal),
                "the log and the bundle never hold " + secret);
        }

        // Codes are fixed strings, but one built from an address is redacted where it is kept as well as in the log.
        host.ReportDiagnostic(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Info, "peer.203.0.113.7:27250", "via relay.198.51.100.9"));
        That(host.RecentDiagnostics[^1].Code == MultiplayerDiagnosticRedactor.Replacement &&
             !log.Lines[^1].Contains("203.0.113.7", StringComparison.Ordinal) && !log.Lines[^1].Contains("198.51.100.9", StringComparison.Ordinal),
            "a code is redacted in the bundle and the log alike, even when a dot runs into the address (" + host.RecentDiagnostics[^1].Code + ")");

        // The forms an address takes in exception messages and descriptor text: glued to labels, with zones and ports,
        // bracketed, mapped, percent-encoded, in paths and shares, as names in any case and under any top-level domain,
        // as short and hexadecimal IPv4, and as URLs.
        var forms = new (string Text, string Secret)[]
        {
            ("endpoint:2001:db8::1", "2001:db8"),
            ("host:fe80::1%3", "fe80"),
            ("remote fe80::1:27250", "fe80"),
            ("myhost:27250", "myhost"),
            ("DESKTOP-SAMPLE:27250", "DESKTOP-SAMPLE"),
            ("localhost:27250", "localhost"),
            ("resolved play.example.org", "example.org"),
            ("https://friend.duckdns.org/descriptor", "duckdns"),
            ("at2001:db8::1", "2001:db8"),
            ("%5B2001:db8::1%5D:27250", "2001:db8"),
            ("mapped ::ffff:203.0.113.5", "203.0.113.5"),
            ("mapped [::ffff:198.51.100.7]:27250", "198.51.100.7"),
            ("scoped [fe80::1%3]:27250", "fe80"),
            ("scoped [fe80::1%25eth0]:27250", "fe80"),
            ("encoded %5Bfe80::1%253%5D:27250", "fe80"),
            ("loopback ::1 and [::1]:27250", "::1"),
            ("in DESKTOP-SAMPLE.local", "DESKTOP-SAMPLE"),
            ("ping @play.example.org", "example.org"),
            ("name friend%2Educkdns%2Eorg", "duckdns"),
            ("glued cafe2001:db8::1", "2001:db8"),
            ("[myhost:27250]", "myhost"),
            ("myhost:27250:", "myhost"),
            ("myhost:27250/join", "myhost"),
            ("[play.example.org:27250]", "example.org"),
            ("play.example.org:27250:", "example.org"),
            ("host:play.example.org", "example.org"),
            ("play.example.org/join/ABCDE", "example.org"),
            ("%5Bplay.example.org%5D%3A27250", "example.org"),
            ("MyServer.duckdns.org", "duckdns"),
            ("Sams-MacBook-Pro.local", "MacBook"),
            ("Play.Example.Org", "Example"),
            ("game.example.il:27250", "example.il"),
            ("Game.Example.Il", "Example"),
            ("play.example.md", "example.md"),
            ("play.example.sh", "example.sh"),
            ("lab.example.edu", "example.edu"),
            ("relay.example.museum", "example.museum"),
            ("PLAY.EXAMPLE.NINJA", "EXAMPLE"),
            ("share \\\\fileserver.corp.local\\share", "fileserver"),
            ("10.20.7720", "7720"),
            ("0x0A.0x14.0x1E.0x28", "0x1E"),
            ("0x0A141E28", "0x0A141E28")
        };
        foreach (var (text, secret) in forms)
        {
            var redacted = MultiplayerDiagnosticRedactor.Redact(text);
            That(!redacted.Contains(secret, StringComparison.OrdinalIgnoreCase) && redacted.Contains(MultiplayerDiagnosticRedactor.Replacement, StringComparison.Ordinal),
                "'" + text + "' is redacted (" + redacted + ")");
        }

        // It errs towards removing too much: file names and mod ids read like host names and go too, while the stack
        // frame's code names stay.
        var frame = MultiplayerDiagnosticRedactor.Redact("at TopiaForge.Multiplayer.Network.LiveSessions.Pump() in /_/mods/TopiaForge.Multiplayer/Network/LiveSessions.cs:line 218");
        That(frame.StartsWith("at TopiaForge.Multiplayer.Network.LiveSessions.Pump() in " + MultiplayerDiagnosticRedactor.Replacement, StringComparison.Ordinal),
            "a stack frame keeps its method and loses its file (" + frame + ")");
        foreach (var (text, name) in new[] { ("at Mod.Update () in Lobby.cs:42", "Lobby.cs"), ("io.example.sandbox 0.2.0 is missing.", "io.example.sandbox") })
        {
            That(!MultiplayerDiagnosticRedactor.Redact(text).Contains(name, StringComparison.Ordinal), "'" + text + "' loses " + name);
        }

        // What only looks like one stays: versions, times, durations, diagnostic codes and code names.
        foreach (var text in new[]
                 {
                     "Sandbox 0.2.0 and protocol 1.0.0 at 12:30",
                     "live.join-ended for p1 at 00:21:04",
                     "content.admission-failed after 0.40 s for 12.5 KiB",
                     "System.Net.Sockets.SocketException: refused",
                     "at TopiaForge.Multiplayer.Network.LiveSessions.Pump() and Mod.Update ()",
                     "std::vector and Foo::Bar"
                 })
        {
            That(MultiplayerDiagnosticRedactor.Redact(text) == text, "'" + text + "' is not mistaken for an address (" + MultiplayerDiagnosticRedactor.Redact(text) + ")");
        }

        for (var index = 0; index < MultiplayerRuntimeHost.RecentDiagnosticCapacity + 10; index++)
        {
            host.ReportDiagnostic(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Info, "test.line-count", "line " + index));
        }

        var kept = host.RecentDiagnostics;
        That(kept.Count == MultiplayerRuntimeHost.RecentDiagnosticCapacity && kept[0].Text.EndsWith("line 10", StringComparison.Ordinal) &&
             kept[^1].Text.EndsWith("line 73", StringComparison.Ordinal),
            "the bundle keeps the newest 64");
    }

    internal static void TheProvidersOwnLogRedactsWhatNoRuntimeTakes()
    {
        // Addresses in a diagnostic's message, its participant, its failure, the failure's inner failure and its stack, from
        // the engine and from the provider itself.
        var secrets = new[] { "203.0.113.9", "2001:db8", "play.example.org", "198.51.100.4", "fe80::7" };
        var failure = Thrown();
        var participant = new ParticipantId("203.0.113.9:27250");
        void Feed(RoutedDiagnostics routed)
        {
            routed.Report(new ProviderDiagnostic(ProviderDiagnosticKind.ProtocolViolation, "peer [2001:db8::5]:27250 broke the protocol", participant, failure));
            routed.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Warning, "live.join-ended", "lost play.example.org:27250", participant));
            routed.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "content.read-failed", "while 198.51.100.4 waited", null, failure));
        }

        // What the runtime writes for them, and keeps for the bundle.
        var runtimeLog = new RecordingMultiplayerLog();
        Feed(new RoutedDiagnostics(null) { Runtime = new MultiplayerRuntimeHost("2545", "0.2.0", runtimeLog) });
        That(runtimeLog.Lines.Count == 3 && runtimeLog.Lines.All(line => line.Contains(MultiplayerDiagnosticRedactor.Replacement, StringComparison.Ordinal)),
            "the runtime redacts all three");

        // The provider's own log, before any runtime attaches and behind one that cannot take a diagnostic, gets the same lines.
        foreach (var failing in new[] { false, true })
        {
            var logger = new RecordingModLogger();
            var routed = new RoutedDiagnostics(logger);
            if (failing) routed.Runtime = new RefusingRuntime(new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog()));
            Feed(routed);
            var path = failing ? "behind a failing runtime" : "with no runtime";
            That(logger.Lines.SequenceEqual(runtimeLog.Lines), path + ", the provider logs the runtime's own redacted lines (" + string.Join(" | ", logger.Lines) + ")");
            foreach (var secret in secrets)
            {
                That(!logger.Lines.Any(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase)), path + ", its log never holds " + secret);
            }
        }

        // A log that throws loses the line, and the diagnostic never breaks the frame.
        Feed(new RoutedDiagnostics(new ThrowingModLogger()));
    }

    internal static void HostWordsStayOutOfLogsAndMods()
    {
        const string Words = "Bye, player at 10.20.30.40";

        // The developer switch drives a join and logs its phases: a kick is logged by its failure's name only.
        using (var rig = new ControlRig(172))
        {
            rig.StartHosting(new InternalHostSettings("Words", 4));
            var player = rig.AddPeer("dev");
            var logger = new RecordingModLogger();
            var developer = new provider::TopiaForge.Multiplayer.DeveloperSessions(player.Provider.Live!, logger, null, JoinTarget.Direct("host.test", 27250), "Dev", 27250);
            That(rig.Run(() =>
            {
                developer.Pump();
                return player.Provider.Live!.Join?.Phase == JoinPhase.InSession;
            }), "the developer switch joins");
            That(rig.Host.Control.Kick(rig.Host.IdOf("Dev"), Words).Succeeded, "the host kicks with its own words");
            That(rig.Run(() =>
            {
                developer.Pump();
                return player.Provider.Live!.Join!.Phase == JoinPhase.Ended;
            }), "the join ends");
            developer.Pump();
            var status = player.Control.GetStatus();
            That(logger.Lines.Any(line => line.Contains("Ended (Kicked)", StringComparison.Ordinal)), "the log says the join ended and why");
            That(!logger.Lines.Concat(player.Log.Lines).Any(line => line.Contains("10.20.30.40", StringComparison.Ordinal) || line.Contains("Bye", StringComparison.Ordinal)),
                "and no log holds the host's words");
            That(status.HostMessage == Words && !status.JoinDetail.Contains("10.20.30.40", StringComparison.Ordinal),
                "the interface gets the host's words apart from this game's own");
        }

        // A host's build is its own text: the join's detail names this game's build, never the host's.
        using (var hostile = new ControlRig(173, "10.20.30.40"))
        {
            hostile.StartHosting(new InternalHostSettings("Odd build", 4));
            var player = hostile.AddPeer("player");
            That(player.Control.Join(player.Target(), new InternalJoinSettings()).Succeeded, "the player tries to join");
            That(hostile.Run(() => player.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and is stopped before connecting");
            var status = player.Control.GetStatus();
            That(status.JoinFailure == InternalLiveFailure.WrongGameBuild && !status.JoinDetail.Contains("10.20.30.40", StringComparison.Ordinal) &&
                 status.JoinDetail.Contains("2545", StringComparison.Ordinal) && status.Server?.GameBuild == "10.20.30.40",
                "the detail names this game's build, and the host's stays in its description (" + status.JoinDetail + ")");
        }

        // A mod's submission that a kick cancels fails with this game's words, never the host's.
        using var harness = new EngineHarness();
        var codec = new JsonTestCodec<CounterValue>();
        var requests = new JsonTestCodec<AddRequest>();
        var add = new MultiplayerCommandType<AddRequest, CounterValue>("add");
        harness.HostSession.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            add, requests, codec, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount))));
        var guest = harness.AddClient("guest");
        guest.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            add, requests, codec, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount))));
        harness.Flush();
        var submission = guest.Session.SubmitAsync(add, new AddRequest(1), CancellationToken.None);
        harness.Host.Kick(new ParticipantId("guest"), MultiplayerSessionEndReason.Kicked, Words);
        harness.Flush();
        var outcome = submission.GetAwaiter().GetResult().Result;
        That(!outcome.Succeeded && !outcome.ErrorMessage.Contains("10.20.30.40", StringComparison.Ordinal) && !outcome.ErrorMessage.Contains("Bye", StringComparison.Ordinal),
            "the mod sees this game's text (" + outcome.ErrorMessage + ")");

        // An object id is the host's choice too: a diagnostic about the object names the type this game registered.
        var client = new RawHostClient();
        var box = new ReplicatedObjectType<CounterValue, AddRequest>("box");
        client.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            box, codec, requests, (_, state, _) => OperationResult<CounterValue>.Success(state)));
        client.Join();
        client.Host.Send((frame, body) =>
        {
            ReplicationMessages.WriteObjectSpawned(frame, body, client.Intern, new ObjectWireMessage
            {
                Owner = EngineHarness.Owner,
                TypeId = "box",
                ObjectId = "10.20.30.40",
                Bytes = codec.Encode(new CounterValue(1)).Value!
            });
            SessionMessages.WriteCommit(frame, body, 1, 0);
        });
        client.Pump();
        client.Host.Send((frame, body) =>
        {
            ReplicationMessages.WriteObjectDespawned(frame, body, "10.20.30.40", 2, new[] { (byte)'{' });
            SessionMessages.WriteCommit(frame, body, 2, 0);
        });
        client.Pump();
        var failure = client.Diagnostics.Reports.Single(report => report.Kind == EngineDiagnosticKind.CodecFailed);
        That(!failure.Message.Contains("10.20.30.40", StringComparison.Ordinal) && failure.Message.Contains("'" + EngineHarness.Owner + "/box'", StringComparison.Ordinal),
            "the diagnostic names the object's type, never the id the host chose (" + failure.Message + ")");
    }

    private static bool IsAddressShaped(string name)
    {
        foreach (var word in AddressWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return name.EndsWith("Port", StringComparison.Ordinal) || name == "port" || name.EndsWith("Ip", StringComparison.Ordinal) || name == "ip";
    }

    private static IEnumerable<Type> Types(MemberInfo member) => member switch
    {
        PropertyInfo property => new[] { property.PropertyType },
        FieldInfo field => new[] { field.FieldType },
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        EventInfo handler => handler.EventHandlerType == null ? Array.Empty<Type>() : new[] { handler.EventHandlerType },
        _ => Array.Empty<Type>()
    };

    private static IEnumerable<ParameterInfo> Parameters(MemberInfo member) => member switch
    {
        MethodBase method => method.GetParameters(),
        _ => Array.Empty<ParameterInfo>()
    };

    private static bool IsNetworkType(Type type)
    {
        if (type.HasElementType) return IsNetworkType(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(IsNetworkType)) return true;
        return type.Namespace != null && (type.Namespace == "System.Net" || type.Namespace.StartsWith("System.Net.", StringComparison.Ordinal));
    }

    /// <summary>Every string the handle shows through its interface.</summary>
    private static IEnumerable<string> Strings(IInternalJoinTarget target)
    {
        yield return target.Description;
        yield return target.ToString() ?? string.Empty;
        if (target.ServerId != null) yield return target.ServerId;
    }

    /// <summary>A thrown failure with addresses in its message and in its inner failure's, and a stack.</summary>
    private static Exception Thrown()
    {
        try
        {
            try
            {
                throw new IOException("relay fe80::7%3 dropped the link");
            }
            catch (IOException inner)
            {
                throw new InvalidOperationException("relay at 198.51.100.4:3478 refused play.example.org", inner);
            }
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    /// <summary>A runtime whose diagnostics sink fails, with an address in its own failure.</summary>
    private sealed class RefusingRuntime : IInternalMultiplayerRuntimeHost
    {
        private readonly MultiplayerRuntimeHost inner;

        internal RefusingRuntime(MultiplayerRuntimeHost inner)
        {
            this.inner = inner;
        }

        public string GameBuild => inner.GameBuild;

        public string RuntimeVersion => inner.RuntimeVersion;

        public string DisplayName => inner.DisplayName;

        public IReadOnlyList<InternalContentDescriptor> LocalContent => inner.LocalContent;

        public InternalContentVerdict EvaluateAdmission(InternalContentOffer offer) => inner.EvaluateAdmission(offer);

        public InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages) => inner.CheckHostContent(hostPackages);

        public void SetRunInBackground(bool enabled) => inner.SetRunInBackground(enabled);

        public void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic) => throw new InvalidOperationException("the log at 203.0.113.9 is closed");
    }

    /// <summary>A mod log that fails at every line.</summary>
    private sealed class ThrowingModLogger : IModLogger
    {
        public void Debug(string message) => throw new InvalidOperationException("closed");

        public void Info(string message) => throw new InvalidOperationException("closed");

        public void Warn(string message) => throw new InvalidOperationException("closed");

        public void Error(string message) => throw new InvalidOperationException("closed");

        public void Error(Exception exception, string message) => throw new InvalidOperationException("closed");
    }
}
