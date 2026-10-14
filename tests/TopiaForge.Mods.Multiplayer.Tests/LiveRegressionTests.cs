extern alias provider;

using System.Net;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What a review of the live flows found, pinned: a content refusal that reached the player with no report, passwords
/// with unpaired surrogates, cookies whose randomness a seed did not reach, host names in legacy numeric forms, and a
/// test rig whose deadline raced the work providers do on other threads.
/// </summary>
internal static class LiveRegressionTests
{
    /// <summary>
    /// A host's content admission refused with nothing but a message, so the player read the code as other and never
    /// learned what to install. The policy now builds the report from the content's code and problems.
    /// </summary>
    internal static void AContentRefusalTellsThePlayerWhatIsMissing()
    {
        using var rig = new LiveRig(180);
        rig.StartHosting(new HostSettings("Picky", 4) { Port = 0 });
        var stranger = rig.AddClient("stranger", new TestContent { Presents = new byte[] { 9 } });
        var refused = rig.Join(stranger, new JoinSettings("Stranger"));
        That(rig.Run(() => refused.Phase == JoinPhase.Ended) && refused.Failure == LiveFailure.Rejected && refused.RejectionCode == AdmissionCode.Content &&
             refused.RejectionProblems.SequenceEqual(new[] { "Unknown content." }) && refused.Detail == "Your packages do not match this session's.",
            "a content refusal reaches the player with its code and every problem (" + refused.RejectionCode + ": " + string.Join(", ", refused.RejectionProblems) + ")");

        rig.HostContent.Verdict = ContentAdmission.Refused(new[] { "This session runs TopiaForge 0.3.", "Zombies 0.2.0 is missing." }, AdmissionCode.Protocol);
        var guest = rig.AddClient("guest");
        var protocol = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => protocol.Phase == JoinPhase.Ended) && protocol.Failure == LiveFailure.ProtocolMismatch && protocol.RejectionCode == AdmissionCode.Protocol &&
             protocol.RejectionProblems.Count == 2, "a protocol refusal from the content reads as one, problems included (" + protocol.Failure + ")");
        rig.HostContent.Verdict = ContentAdmission.Refused(new[] { "\u0007 not display text", "Kept." });
        var filtered = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => filtered.Phase == JoinPhase.Ended) && filtered.RejectionCode == AdmissionCode.Content && filtered.RejectionProblems.SequenceEqual(new[] { "Kept." }),
            "a problem that is not display text is left out, and the report still reads");
        Throws<ArgumentOutOfRangeException>(() => ContentAdmission.Refused(Array.Empty<string>(), AdmissionCode.Banned), "content cannot refuse with the host's own codes");
    }

    /// <summary>
    /// A password with an unpaired surrogate passed the rule but could not be normalized, so hosting failed late and
    /// unexplained, and where normalizing did not throw, UTF-8 turned every lone surrogate into U+FFFD, so different
    /// passwords proved the same. Such a password is now refused where it is set.
    /// </summary>
    internal static void PasswordsWithUnpairedSurrogatesAreRefusedWhereTheyAreSet()
    {
        var hostKey = Enumerable.Repeat((byte)5, 32).ToArray();
        foreach (var password in new[] { "pass\uD800word", "pass\uDC00word", "\uDC00\uD800", "trailing\uD83D", "\uDE00leading" })
        {
            That(!PasswordProof.IsValidPassword(password), "an unpaired surrogate breaks the rule");
            Throws<ArgumentException>(() => PasswordProof.DeriveKey(password, hostKey), "and is never derived");
            Throws<ArgumentException>(() => new HostSettings("Locked") { Password = password }, "a host cannot set it, so hosting never starts with it");
            Throws<ArgumentException>(() => new JoinSettings("Guest") { Password = password }, "and a joining player cannot type it in advance");
        }

        That(PasswordProof.IsValidPassword("café 😀"), "a surrogate pair is one character, and allowed");
        var host = new HostSettings("Locked");
        try
        {
            host.Password = "pass\uD800word";
        }
        catch (ArgumentException refusal)
        {
            That(refusal.Message.StartsWith(PasswordProof.Rule, StringComparison.Ordinal), "the refusal states the rule (" + refusal.Message + ")");
        }

        That(host.Password == null, "and the setting keeps no part of it");

        using var rig = new LiveRig(181);
        rig.StartHosting(new HostSettings("Locked", 4) { Port = 0, Password = "open sesame" });
        var guest = rig.AddClient("guest");
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.AwaitingPassword), "a join waits for the password");
        Throws<ArgumentException>(() => join.SupplyPassword("open\uD800sesame"), "and refuses one with an unpaired surrogate when it is typed");
        That(join.Phase == JoinPhase.AwaitingPassword, "still waiting for a password it can use");
    }

    /// <summary>
    /// The rig used to seed an endpoint after building it, by which time its cookie guard had drawn its secrets from the
    /// platform's generator, so two runs of one seed sent different Retry cookies. Every endpoint now starts from the
    /// options its flow is given.
    /// </summary>
    internal static void IdenticallySeededRunsMatchThroughARetry()
    {
        var first = SeededJoinThroughRetry();
        var second = SeededJoinThroughRetry();
        That(first.Retries > 0, "the host answered with a Retry (" + first.Retries + ")");
        That(first.Transcript == second.Transcript, "two runs of one seed send identical datagrams, Retry cookies included");
    }

    private static (string Transcript, long Retries) SeededJoinThroughRetry()
    {
        using var rig = new LiveRig(182, seededTransports: true, configureHostTransport: options => options.AlwaysRequireCookie = true);
        Seed(rig, 1821);
        rig.StartHosting(new HostSettings("Cautious", 4) { Port = 0 });
        var guest = rig.AddClient("guest");
        LiveRig.SeedIdentity(rig.ClientFiles(guest), 1822);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins through a cookie (" + join.Failure + ")");
        join.Leave();
        rig.Run(() => false, 1_000_000);
        return (rig.Network.TranscriptHash() + " at " + rig.Network.Now, rig.HostFactory.Networks[^1].Stack.Endpoint.Statistics.RetriesSent);
    }

    /// <summary>
    /// The link grammar reads 0x7f000001 and 0x7f.1 as names, as the launcher does, but the game's resolver handed them to
    /// .NET, which reads them as 127.0.0.1, so relay-only joined them while the launcher refused them. They are now never
    /// resolved, whatever the privacy choice; the canonical literals resolve as before.
    /// </summary>
    internal static void NumericHostNamesInLegacyFormsAreNeverResolved()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var signed = SignedServerDescriptor.Sign(
            NetworkDescriptorTests.Build(fixture.GetProperty("valid")[0].GetProperty("descriptor")),
            NetworkFixtures.Hex(fixture.GetProperty("keys")[0].Text("signingSeed")));
        var contacted = new List<IPEndPoint>();
        var resolver = new NetworkTargetResolver(() => null, new KnownHosts(), () => { }, () => 1_000, (endpoint, _, _) =>
        {
            lock (contacted) contacted.Add(endpoint);
            return Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, signed, string.Empty));
        });
        ResolvedTarget Resolve(string host, bool allowDirect) =>
            resolver.ResolveAsync(JoinTarget.Direct(host, 27250), allowDirect, null, CancellationToken.None).GetAwaiter().GetResult();

        foreach (var host in new[] { "0x7f000001", "0X7F000001", "0x7f.1", "0x7f.0.0.1", "0xa.1", "0xc0.0xa8.1.20" })
        {
            That(SessionLink.TryNormalizeHost(host, out _) && IPAddress.TryParse(host, out _), host + " is a name to the link grammar and an address to .NET");
            var relayOnly = Resolve(host, allowDirect: false);
            That(relayOnly.Failure == LiveFailure.DirectNotAllowed && contacted.Count == 0, "relay-only refuses " + host + " as a name beyond the local network (" + relayOnly.Failure + ")");
            var direct = Resolve(host, allowDirect: true);
            That(direct.Failure == LiveFailure.Unreachable && contacted.Count == 0 && !direct.Detail.Contains(host, StringComparison.OrdinalIgnoreCase),
                "direct play does not resolve " + host + " either, and says why without repeating it (" + direct.Failure + ": " + direct.Detail + ")");
        }

        // Digits and dots alone never make a name: the grammar refuses these before anything resolves, as the launcher does.
        foreach (var host in new[] { "2130706433", "127.1", "0177.0.0.1", "017700000001", "1.2.3", "192.168.001.20" })
        {
            Throws<ArgumentException>(() => JoinTarget.Direct(host, 27250), host + " is not a host");
        }

        That(Resolve("127.0.0.1", allowDirect: false).Failure == LiveFailure.None && contacted.Count == 1, "the canonical IPv4 literal still resolves");
        That(Resolve("::1", allowDirect: false).Failure == LiveFailure.None && contacted.Count == 2, "and so does the IPv6 one");
    }

    /// <summary>
    /// The rig moved virtual time on while identity loads and password keys ran on other threads, sleeping a
    /// millisecond of real time every twenty steps, so a slow machine ran out of virtual time first and "the host starts"
    /// failed on a CI runner. The rig now waits for that work in real time and moves virtual time only once it is done,
    /// so however slow the work is, it lands at the same virtual moment.
    /// </summary>
    internal static void WorkOnOtherThreadsTakesNoVirtualTime()
    {
        var quick = HostAndJoin(readDelayMilliseconds: 0);
        var slow = HostAndJoin(readDelayMilliseconds: 150);
        That(quick == slow, "slow loads land at the same virtual moments as quick ones (" + quick + " vs " + slow + ")");
    }

    private static string HostAndJoin(int readDelayMilliseconds)
    {
        using var rig = new LiveRig(183, seededTransports: true);
        Seed(rig, 1831);
        if (readDelayMilliseconds > 0) rig.HostFiles.BeforeRead = _ => Task.Delay(readDelayMilliseconds);
        rig.StartHosting(new HostSettings("Unhurried", 4) { Port = 0, Password = "open sesame" });
        var hosting = rig.Network.Now;
        var guest = rig.AddClient("guest");
        LiveRig.SeedIdentity(rig.ClientFiles(guest), 1832);
        if (readDelayMilliseconds > 0) rig.ClientFiles(guest).BeforeRead = _ => Task.Delay(readDelayMilliseconds);
        var join = rig.Join(guest, new JoinSettings("Guest") { Password = "open sesame" });
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins (" + join.Failure + ")");
        return "hosting at " + hosting + ", in session at " + rig.Network.Now + ", " + rig.Network.TranscriptHash();
    }

    /// <summary>Gives the host an identity and a session the seed decides; a solo session's own id is random by design.</summary>
    private static void Seed(LiveRig rig, int seed)
    {
        LiveRig.SeedIdentity(rig.HostFiles, seed);
        rig.Host.Engine.ReplaceSession(new MultiplayerSessionId("seeded:" + seed), new SessionSeed((ulong)seed));
    }
}
