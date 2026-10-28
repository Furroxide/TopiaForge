extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ContentOfferCodec = provider::TopiaForge.Multiplayer.ContentOfferCodec;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Content through the runtime: the host's descriptor lists the runtime's shared packages, the host admits through the
/// runtime's admission planner, and a joining player checks the host's packages before connecting. Refusals reach the
/// player as display lines naming mods and versions, and packages that cannot be read stop hosting and joining cleanly.
/// </summary>
internal static class RuntimeAdmissionTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');
    private static readonly PackageSpec Lobby = PackageSpec.Session("io.example.lobby", "1.0.0", 'c');
    private static readonly PackageSpec Optional = PackageSpec.Session("io.example.extras", "1.0.0", 'd', "optional");
    private static readonly PackageSpec Devtools = PackageSpec.Implicit("io.example.devtools", "0.1.0", 'e');

    internal static void PackagesThatCannotBeReadStopHostingAndJoining()
    {
        // A build copied by hand has no install receipt, so the runtime hashes its files the first time multiplayer looks at
        // them, on the main thread. Here another program holds its only file open.
        using var rig = new ControlRig(154, Sandbox);
        var hostCopy = Path.Combine(rig.PackageRoot, "host-copy");
        var locked = LockedPackage(hostCopy);
        try
        {
            rig.Host.Runtime.Inventory.Add(Devtools.ToManifest(), hostCopy);
            var refused = rig.Host.Control.StartHosting(new InternalHostSettings("Locked", 4));
            That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Io && refused.ErrorMessage == RuntimeContent.ContentUnreadable,
                "hosting is refused as content that cannot be read (" + refused.ErrorCode + ": " + refused.ErrorMessage + ")");
            var reported = rig.Host.Runtime.RecentDiagnostics[^1];
            That(reported.Code == "content.read-failed" && reported.Text.Contains("Devtools 0.1.0 could not be read", StringComparison.Ordinal) &&
                 !reported.Text.Contains(rig.PackageRoot, StringComparison.OrdinalIgnoreCase),
                "the failure is reported, naming the package and never its folder (" + reported.Text + ")");

            // The developer switch drives the live sessions directly: the host fails before anything binds.
            var live = rig.Host.Provider.Live!;
            live.StartHosting(new HostSettings("Locked too"));
            That(rig.Run(() => live.HostPhase == HostPhase.Failed), "hosting the live sessions directly fails");
            That(live.HostFailure == LiveFailure.ContentUnreadable && rig.Host.Control.GetStatus().HostFailure == InternalLiveFailure.ContentUnreadable &&
                 rig.Host.Factory.Networks.Count == 0,
                "as content that cannot be read, with no network opened (" + live.HostFailure + ", " + rig.Host.Factory.Networks.Count + " networks)");
        }
        finally
        {
            locked.Dispose();
        }

        // Once the file is free the next read hashes it, and the host opens.
        rig.StartHosting(new InternalHostSettings("Free", 4));

        // A joining player with such a package stops before anything connects, in this game's own words.
        var guest = rig.AddPeer("guest", Sandbox);
        var guestCopy = Path.Combine(rig.PackageRoot, "guest-copy");
        locked = LockedPackage(guestCopy);
        try
        {
            guest.Runtime.Inventory.Add(Devtools.ToManifest(), guestCopy);
            That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest tries to join");
            That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and stops");
            var status = guest.Control.GetStatus();
            That(status.JoinFailure == InternalLiveFailure.ContentUnreadable && status.JoinDetail == "This game's mods could not be read." &&
                 status.ContentProblems.SequenceEqual(new[] { RuntimeContent.ContentUnreadable }) && !status.ContentProblemsFromHost,
                "as content that cannot be read (" + status.JoinFailure + ": " + status.JoinDetail + " | " + string.Join(" | ", status.ContentProblems) + ")");
            That(guest.Factory.Networks.Count == 0 && guest.Control.Role == InternalSessionRole.Solo && rig.Host.Control.GetStatus().ConnectedPlayers == 1,
                "nothing connected, and the guest plays on alone");
        }
        finally
        {
            locked.Dispose();
        }

        That(!rig.Host.Log.Lines.Concat(guest.Log.Lines).Any(line => line.Contains("pump failed", StringComparison.Ordinal) || line.Contains("flush failed", StringComparison.Ordinal)),
            "and no frame ever failed");

        // Every other content path refuses the same way: a running host that cannot read its own packages admits nobody,
        // and nothing is listed or offered.
        var reports = new List<InternalMultiplayerDiagnostic>();
        var content = new RuntimeContent("2545", "0.2.0") { Runtime = new UnreadableContent(new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog())), Report = reports.Add };
        var offer = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", TopiaForge.Mods.MultiplayerModule.ProtocolVersion, TopiaForge.Mods.MultiplayerModule.PeerVersionRange, Array.Empty<InternalContentDescriptor>()))!;
        var admission = content.Admit(offer);
        That(!admission.IsAdmitted && admission.Code == AdmissionCode.NotReady && admission.Problems.SequenceEqual(new[] { RuntimeContent.HostContentUnreadable }),
            "the host refuses the player as not ready, saying why");
        That(content.HostPackages() == null && content.ClientContent().Length == 0, "no descriptor can be signed and no offer made");
        That(reports.Count == 3 && reports.All(report => report.Code == "content.read-failed" && report.Exception is IOException), "and each failure is reported");
    }

    internal static void TheHostAdmitsThroughTheRuntimesPlanner()
    {
        using var rig = new ControlRig(150, Sandbox, Lobby, Optional, PackageSpec.Local("io.example.hostui", "1.0.0", '1'));
        rig.StartHosting(new InternalHostSettings("Planned", 8));
        var listed = rig.Host.Provider.Live!.Host!.Descriptor!.Descriptor.Packages;
        That(listed.Select(package => package.Id + ":" + package.Role).SequenceEqual(new[]
        {
            "io.example.extras:SessionOptional", "io.example.lobby:SessionRequired", "io.example.sandbox:Implicit"
        }), "the descriptor lists the shared packages by role and never the host's client-local ones");

        // The same packages, plus a client-local mod of the player's own: admitted.
        var fits = rig.AddPeer("fits", Sandbox, Lobby, PackageSpec.Local("io.example.hud", "2.0.0", '2'));
        rig.JoinAndWait(fits);

        // A stale player that skips its own check presents what it has; only the host's admission refuses it.
        var stale = rig.AddPeer("stale", PackageSpec.Implicit(Sandbox.Id, "0.2.1", 'b'));
        stale.UseRuntime(new UncheckedRuntimeHost(stale.Runtime));
        That(stale.Control.Join(stale.Target(), new InternalJoinSettings()).Succeeded, "the stale player tries to join");
        That(rig.Run(() => stale.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and is refused");
        var refused = stale.Control.GetStatus();
        That(refused.JoinFailure == InternalLiveFailure.Rejected && refused.RejectionCode == InternalAdmissionCode.Content,
            "as a content refusal (" + refused.JoinFailure + ", " + refused.RejectionCode + ")");
        That(refused.ContentProblems.SequenceEqual(new[] { "Lobby 1.0.0 is missing.", "Sandbox: the host runs 0.2.0; this game has 0.2.1." }) &&
             refused.ContentProblemsFromHost, "with the host's report naming each problem, marked as the host's (" + string.Join(" | ", refused.ContentProblems) + ")");
        That(stale.Control.Role == InternalSessionRole.Solo && rig.Host.Control.GetStatus().ConnectedPlayers == 2, "and the stale player plays on alone");
    }

    internal static void JoiningPlayersCheckTheHostsPackagesBeforeConnecting()
    {
        using var rig = new ControlRig(151, Sandbox, Lobby);
        rig.StartHosting(new InternalHostSettings("Strict", 8));
        var stale = rig.AddPeer("stale", PackageSpec.Implicit(Sandbox.Id, "0.2.1", 'b'), PackageSpec.Standalone("io.example.solo", "1.0.0", '3'));
        That(stale.Control.Join(stale.Target(), new InternalJoinSettings()).Succeeded, "the join starts");
        That(rig.Run(() => stale.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and ends before connecting");
        var status = stale.Control.GetStatus();
        That(status.JoinFailure == InternalLiveFailure.ContentMismatch && status.JoinDetail == "This game's mods do not match the host's.",
            "as a content mismatch (" + status.JoinFailure + ": " + status.JoinDetail + ")");
        That(status.ContentProblems.SequenceEqual(new[]
            {
                "io.example.lobby 1.0.0 is missing.",
                "Sandbox: the host runs 0.2.0; this game has 0.2.1.",
                "Solo refuses multiplayer; disable it to join."
            }),
            "listing what to install and what to disable (" + string.Join(" | ", status.ContentProblems) + ")");
        That(rig.Host.Control.GetStatus().ConnectedPlayers == 1 && stale.Factory.Networks.Count == 0, "nothing ever connected or opened a network");
    }

    internal static void SessionModsAtCompatibleVersionsJoin()
    {
        // The host runs Lobby 1.0.0; this player has 1.0.1, a different archive, with the same protocol. The player's own
        // check needs only that Lobby is here, and the host's planner, which judges session mods, admits it.
        using var rig = new ControlRig(152, Sandbox, Lobby);
        rig.StartHosting(new InternalHostSettings("Compatible", 4));
        var newer = rig.AddPeer("newer", Sandbox, PackageSpec.Session(Lobby.Id, "1.0.1", 'b'));
        rig.JoinAndWait(newer);
        That(newer.Control.Role == InternalSessionRole.Client && rig.Host.Control.GetStatus().ConnectedPlayers == 2,
            "a session mod at a compatible version joins");

        // Without it at all, the player is told what to install before anything connects.
        var without = rig.AddPeer("without", Sandbox);
        That(without.Control.Join(without.Target(), new InternalJoinSettings()).Succeeded, "a player without Lobby tries to join");
        That(rig.Run(() => without.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and is stopped");
        var status = without.Control.GetStatus();
        That(status.JoinFailure == InternalLiveFailure.ContentMismatch && !status.ContentProblemsFromHost &&
             status.ContentProblems.SequenceEqual(new[] { "io.example.lobby 1.0.0 is missing." }),
            "by this game's own check (" + string.Join(" | ", status.ContentProblems) + ")");
    }

    internal static void ContentOffersRoundTripAndRefuseMalformedBytes()
    {
        var session = new InternalContentDescriptor("io.example.lobby", "1.0.0", new string('c', 64), InternalContentMode.Session,
            InternalContentPresence.Required, "1.0.0", ">=1.0.0 <2.0.0", new[] { new InternalSynchronizedFile("topiaforge.multiplayer.lock.json", new string('e', 64)) });
        var plain = new InternalContentDescriptor("io.example.sandbox", "0.2.0", new string('a', 64), InternalContentMode.ImplicitShared);
        var exact = new InternalContentDescriptor("io.example.tools", "3.0.0", new string('9', 64), InternalContentMode.Session,
            InternalContentPresence.Optional, "2.0.0");
        var offer = new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", new[] { session, plain, exact });
        var bytes = ContentOfferCodec.TryEncode(offer)!;
        That(ContentOfferCodec.TryDecode(bytes, out var decoded) && Describe(decoded!) == Describe(offer), "an offer round-trips exactly");
        That(bytes.SequenceEqual(ContentOfferCodec.TryEncode(decoded!)!), "and has one spelling");

        That(!ContentOfferCodec.TryDecode(Array.Empty<byte>(), out _), "no bytes is no offer");
        var badVersion = (byte[])bytes.Clone();
        badVersion[0] = 2;
        That(!ContentOfferCodec.TryDecode(badVersion, out _), "another format version is refused");
        That(!ContentOfferCodec.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), out _), "a truncated offer is refused");
        That(!ContentOfferCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _), "trailing bytes are refused");
        var unsorted = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", new[] { plain, session }))!;
        That(!ContentOfferCodec.TryDecode(unsorted, out _), "mods out of order are refused");
        var repeated = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", new[] { plain, plain }))!;
        That(!ContentOfferCodec.TryDecode(repeated, out _), "a mod listed twice is refused");
        var local = new InternalContentDescriptor("io.example.zz", "1.0.0", new string('5', 64), InternalContentMode.ClientLocal);
        var leaked = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", new[] { plain, local }))!;
        That(!ContentOfferCodec.TryDecode(leaked, out _), "a client-local mod is never part of an offer");
        var badMode = (byte[])ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", "x", new[] { plain }))!.Clone();
        badMode[^1] = 9;
        That(!ContentOfferCodec.TryDecode(badMode, out _), "an unknown mode is refused");
        var badBuild = ContentOfferCodec.TryEncode(new InternalContentOffer("build 2545", "1.0.0", "x", new[] { plain }))!;
        That(!ContentOfferCodec.TryDecode(badBuild, out _), "a game build outside the descriptor's form is refused");

        // 512 session mods with 256 files each can never fit the join request.
        var files = Enumerable.Range(0, 256).Select(index => new InternalSynchronizedFile("f/" + index.ToString("D3") + ".bin", new string('7', 64))).ToArray();
        var heavy = Enumerable.Range(0, 512).Select(index => new InternalContentDescriptor("io.example.mod" + index.ToString("D3"), "1.0.0", new string('6', 64),
            InternalContentMode.Session, InternalContentPresence.Required, "1.0.0", null, files)).ToArray();
        That(ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", "x", heavy)) == null, "an offer past the join request's bound does not encode");
    }

    internal static void TheContentSeamRefusesWithoutARuntime()
    {
        var content = new RuntimeContent("2545", "0.2.0");
        That(content.HostPackages() is { Count: 0 } && content.ClientContent().Length == 0, "without a runtime nothing is listed or presented");
        var refusal = content.Admit(Array.Empty<byte>());
        That(!refusal.IsAdmitted && refusal.Code == AdmissionCode.NotReady && refusal.Problems.SequenceEqual(new[] { RuntimeContent.HostNotReady }),
            "and nobody is admitted, for a runtime not ready rather than for the player's packages");

        var reports = new List<InternalMultiplayerDiagnostic>();
        var runtime = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog());
        content.Runtime = new ThrowingAdmission(runtime);
        content.Report = reports.Add;
        var malformed = content.Admit(new byte[] { 1, 2, 3 });
        That(!malformed.IsAdmitted && malformed.Code == AdmissionCode.Protocol && malformed.Problems.SequenceEqual(new[] { RuntimeContent.OfferUnreadable }),
            "malformed content is a protocol refusal");
        var offer = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", TopiaForge.Mods.MultiplayerModule.ProtocolVersion, TopiaForge.Mods.MultiplayerModule.PeerVersionRange, Array.Empty<InternalContentDescriptor>()))!;
        var failing = content.Admit(offer);
        That(!failing.IsAdmitted && failing.Code == AdmissionCode.NotReady && reports.Single().Code == "content.admission-failed",
            "a failing admission check refuses the player as not ready and is reported, never admits");
    }

    internal static void TheOfferFollowsTheLoadedPackages()
    {
        var root = Directory.CreateTempSubdirectory("TopiaForgeOffer-").FullName;
        try
        {
            var runtime = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog(), contentScheduler: MultiplayerWorkSchedulers.Inline);
            var content = new RuntimeContent("2545", "0.2.0") { Runtime = runtime };
            var empty = content.ClientContent();
            That(ContentOfferCodec.TryDecode(empty, out var none) && none!.Mods.Count == 0, "a runtime without packages offers none");
            That(ReferenceEquals(empty, content.ClientContent()), "and encodes its offer once while they stay the same");

            runtime.Inventory.Add(Sandbox.ToManifest(), Sandbox.Install(root));
            That(ContentOfferCodec.TryDecode(content.ClientContent(), out var loaded) && loaded!.Mods.Count == 1 && loaded.Mods[0].Id == Sandbox.Id,
                "a package that loads after the runtime attached is offered");
            runtime.Inventory.Remove(Sandbox.Id);
            That(ContentOfferCodec.TryDecode(content.ClientContent(), out var removed) && removed!.Mods.Count == 0, "and one that is removed is not");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Describe(InternalContentOffer offer) =>
        offer.GameBuild + "|" + offer.ProtocolVersion + "|" + offer.PeerVersionRange + "|" + string.Join(";", offer.Mods.Select(mod =>
            mod.Id + "," + mod.Version + "," + mod.PackageSha256 + "," + mod.Mode + "," + mod.Presence + "," + mod.ProtocolVersion + "," +
            (mod.PeerVersionRange ?? "-") + "," + string.Join("+", mod.SynchronizedFiles.Select(file => file.Path + "=" + file.Sha256))));

    /// <summary>Writes a package without an install receipt into <paramref name="folder"/>, its only file held open with no sharing.</summary>
    private static FileStream LockedPackage(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Devtools.dll");
        File.WriteAllBytes(file, new byte[4096]);
        return new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>A runtime whose packages cannot be read, as the inventory reports a file another program holds open.</summary>
    private sealed class UnreadableContent : IInternalMultiplayerRuntimeHost
    {
        private readonly MultiplayerRuntimeHost inner;

        internal UnreadableContent(MultiplayerRuntimeHost inner)
        {
            this.inner = inner;
        }

        public string GameBuild => inner.GameBuild;

        public string RuntimeVersion => inner.RuntimeVersion;

        public string DisplayName => inner.DisplayName;

        public IReadOnlyList<InternalContentDescriptor> LocalContent => throw new IOException("Devtools 0.1.0 could not be read to identify it for multiplayer (IOException).");

        public InternalContentReadiness ContentReadiness => new InternalContentReadiness(InternalContentState.Unreadable, 0, 0, 0, 0);

        public InternalContentVerdict EvaluateAdmission(InternalContentOffer offer) => inner.EvaluateAdmission(offer);

        public InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages) => inner.CheckHostContent(hostPackages);

        public void SetRunInBackground(bool enabled) => inner.SetRunInBackground(enabled);

        public void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic) => inner.ReportDiagnostic(diagnostic);

        public string? ServiceOrigin => inner.ServiceOrigin;
    }

    /// <summary>A runtime whose admission check fails, as a bug in it would.</summary>
    internal sealed class ThrowingAdmission : IInternalMultiplayerRuntimeHost
    {
        private readonly MultiplayerRuntimeHost inner;

        internal ThrowingAdmission(MultiplayerRuntimeHost inner)
        {
            this.inner = inner;
        }

        public string GameBuild => inner.GameBuild;

        public string RuntimeVersion => inner.RuntimeVersion;

        public string DisplayName => inner.DisplayName;

        public IReadOnlyList<InternalContentDescriptor> LocalContent => inner.LocalContent;

        public InternalContentReadiness ContentReadiness => inner.ContentReadiness;

        public InternalContentVerdict EvaluateAdmission(InternalContentOffer offer) => throw new InvalidOperationException("broken planner");

        public InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages) => inner.CheckHostContent(hostPackages);

        public void SetRunInBackground(bool enabled) => inner.SetRunInBackground(enabled);

        public void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic) => inner.ReportDiagnostic(diagnostic);

        public string? ServiceOrigin => inner.ServiceOrigin;
    }
}
