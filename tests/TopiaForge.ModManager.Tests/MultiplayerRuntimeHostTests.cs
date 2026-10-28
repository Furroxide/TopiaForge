using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// ModManager's side of the internal multiplayer SPI: describing the packages that loaded, admitting players through
    /// the planner, checking a host's packages, the background setting, and the composition root's frame.
    /// </summary>
    internal static class MultiplayerRuntimeHostTests
    {
        private const string Lock = ModMultiplayerMetadata.ContractLockFileName;

        internal static void Run(string root)
        {
            DescriptorsFollowTheManifests();
            PackageHashesComeFromReceiptsOrTheFilesThemselves(Path.Combine(root, "multiplayer-hashes"));
            UnreadablePackagesAreNamedAndReadAgain(Path.Combine(root, "multiplayer-locked"));
            AdmissionNamesEveryProblem();
            HostChecksAreExactAndListWhatToChange();
            TheBackgroundSettingIsRestored();
            IdentityValuesAreBounded();
            TheCompositionRootRunsItsStagesByRole();
            FrameFailuresAreIsolatedAndLoggedOnce();
            Console.WriteLine("Multiplayer runtime host tests passed.");
        }

        private static void DescriptorsFollowTheManifests()
        {
            var inventory = new MultiplayerContentInventory(MultiplayerWorkSchedulers.Inline);
            inventory.Add(Manifest("io.example.zeta", "1.0.0"), "unused-a");
            inventory.Add(Session("io.example.lobby", "2.0.0", "optional", ">=2.0.0 <3.0.0", Lock, new string('A', 64)), "unused-b");
            inventory.Add(Mode("io.example.cosmetic", ModMultiplayerMetadata.ClientLocalMode), "unused-c");
            inventory.Add(Mode("io.example.rules", ModMultiplayerMetadata.ServerOnlyMode), "unused-d");
            inventory.Add(Mode("io.example.solo", ModMultiplayerMetadata.StandaloneMode), "unused-e");
            inventory.Add(Mode("io.example.future", "peer-to-peer"), "unused-f");
            var broken = Session("io.example.broken", "1.0.0", "required", null, Lock, new string('b', 64));
            broken.Multiplayer!.Protocol = null;
            inventory.Add(broken, "unused-g");
            inventory.Add(Manifest(new string('x', 70), "1.0.0"), "unused-h");

            var descriptors = inventory.Descriptors;
            Assert(descriptors.Select(item => item.Id).SequenceEqual(new[]
                {
                    "io.example.broken", "io.example.cosmetic", "io.example.future", "io.example.lobby", "io.example.rules", "io.example.solo", "io.example.zeta"
                }),
                "every describable package is listed, sorted by id");
            Assert(inventory.Skipped.SequenceEqual(new[] { new string('x', 70) }), "a package past the descriptor's bounds is left out and named");
            var byId = descriptors.ToDictionary(item => item.Id);
            Assert(byId["io.example.zeta"].Mode == InternalContentMode.ImplicitShared && byId["io.example.zeta"].DisplayName == "Zeta", "no block is implicit sharing");
            var lobby = byId["io.example.lobby"];
            Assert(lobby.Mode == InternalContentMode.Session && lobby.Presence == InternalContentPresence.Optional && lobby.ProtocolVersion == "2.0.0" &&
                   lobby.PeerVersionRange == ">=2.0.0 <3.0.0" && lobby.SynchronizedFiles.Single().Path == Lock &&
                   lobby.SynchronizedFiles.Single().Sha256 == new string('a', 64), "a session mod carries its presence, protocol and synchronized hashes");
            Assert(byId["io.example.cosmetic"].Mode == InternalContentMode.ClientLocal && byId["io.example.rules"].Mode == InternalContentMode.ServerOnly &&
                   byId["io.example.solo"].Mode == InternalContentMode.Standalone, "the other modes are kept");
            Assert(byId["io.example.future"].Mode == InternalContentMode.Standalone && byId["io.example.broken"].Mode == InternalContentMode.Standalone,
                "an unknown mode or a session mod without a protocol never takes part in a session");
            Assert(ReferenceEquals(descriptors, inventory.Descriptors), "the description is computed once");
            inventory.Remove("io.example.zeta");
            Assert(!inventory.Descriptors.Any(item => item.Id == "io.example.zeta"), "and again once the packages change");
        }

        private static void PackageHashesComeFromReceiptsOrTheFilesThemselves(string root)
        {
            var receipted = Directory.CreateDirectory(Path.Combine(root, "receipted")).FullName;
            File.WriteAllText(Path.Combine(receipted, "Mod.dll"), "code");
            JsonUtil.SaveFile(Path.Combine(receipted, PackageInstallReceipt.FileName), new PackageInstallReceipt { SourceSha256 = new string('c', 64) });
            Assert(MultiplayerContentInventory.PackageSha256(receipted) == new string('c', 64), "an install receipt names the archive's hash");

            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            foreach (var folder in new[] { first, second })
            {
                Directory.CreateDirectory(Path.Combine(folder, "assets"));
                File.WriteAllText(Path.Combine(folder, "Mod.dll"), "code");
                File.WriteAllText(Path.Combine(folder, "assets", "world.bundle"), "bundle");
            }

            var hash = MultiplayerContentInventory.PackageSha256(first);
            Assert(InternalText.IsSha256(hash) && hash == MultiplayerContentInventory.PackageSha256(second), "identical files hash alike wherever they are installed");
            File.WriteAllText(Path.Combine(second, PackageInstallReceipt.FileName), "{ not json");
            Assert(MultiplayerContentInventory.PackageSha256(second) == hash, "an unreadable receipt falls back to the files, and is not one of them");
            File.WriteAllText(Path.Combine(second, PackageInstallReceipt.FileName), JsonUtil.SerializeBounded(new PackageInstallReceipt { SourceSha256 = new string('C', 64) }));
            Assert(MultiplayerContentInventory.PackageSha256(second) == hash, "a receipt hash that is not lowercase hexadecimal is not trusted");
            File.WriteAllText(Path.Combine(second, "assets", "world.bundle"), "changed");
            Assert(MultiplayerContentInventory.PackageSha256(second) != hash, "any changed file changes the hash");
            File.WriteAllText(Path.Combine(second, "assets", "world.bundle"), "bundle");
            File.Move(Path.Combine(second, "assets", "world.bundle"), Path.Combine(second, "assets", "other.bundle"));
            Assert(MultiplayerContentInventory.PackageSha256(second) != hash, "and so does a renamed one");
        }

        private static void UnreadablePackagesAreNamedAndReadAgain(string root)
        {
            // A package without a receipt whose file another program holds open while the runtime hashes it. Inline
            // identification keeps the old order here; MultiplayerContentIdentificationTests covers the worker.
            var folder = Directory.CreateDirectory(Path.Combine(root, "devtools")).FullName;
            var file = Path.Combine(folder, "Devtools.dll");
            File.WriteAllText(file, "code");
            var inventory = new MultiplayerContentInventory(MultiplayerWorkSchedulers.Inline);
            inventory.Add(Manifest("io.example.devtools", "0.1.0"), folder);
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var read in new Action[] { () => _ = inventory.Descriptors, () => _ = inventory.AdmissionMods })
                {
                    IOException? failure = null;
                    try
                    {
                        read();
                    }
                    catch (IOException exception)
                    {
                        failure = exception;
                    }

                    Assert(failure != null && failure.Message == "Devtools 0.1.0 could not be read to identify it for multiplayer (IOException)." &&
                           failure.InnerException is IOException && !failure.Message.Contains(root, StringComparison.OrdinalIgnoreCase),
                        "a package whose files cannot be read is named, and its folder is not (" + failure?.Message + ")");
                }
            }

            // The failed read kept nothing, so the next one, with the file free, hashes it.
            Assert(inventory.Descriptors.Single().PackageSha256 == MultiplayerContentInventory.PackageSha256(folder) &&
                   inventory.AdmissionMods.Single().PackageSha256 == MultiplayerContentInventory.PackageSha256(folder),
                "once the file is free the package is read again");
        }

        private static void AdmissionNamesEveryProblem()
        {
            var host = new List<MultiplayerAdmissionMod>
            {
                Mod(Manifest("io.example.sandbox", "0.2.0"), 'a'),
                Mod(Session("io.example.lobby", "1.0.0", "required", ">=1.0.0 <2.0.0", Lock, new string('e', 64)), 'c'),
                Mod(Session("io.example.extras", "1.0.0", "optional", null, Lock, new string('e', 64)), 'd'),
                Mod(Mode("io.example.hostui", ModMultiplayerMetadata.ClientLocalMode), '1'),
                Mod(Mode("io.example.rules", ModMultiplayerMetadata.ServerOnlyMode), '2')
            };

            var sandbox = Offered("io.example.sandbox", "0.2.0", 'a', InternalContentMode.ImplicitShared);
            var lobby = OfferedSession("io.example.lobby", "1.0.0", 'c', "1.0.0", ">=1.0.0 <2.0.0", new string('e', 64));
            Assert(MultiplayerContentAdmission.Evaluate("2545", host, Offer("2545", sandbox, lobby)).IsAdmitted,
                "the same shared packages are admitted, without the host's optional, client-local or server-only mods");

            Expect(host, Offer("2478", sandbox, lobby), "The host runs Robotopia build 2545; this game runs 2478.");
            Expect(host, new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", new[] { sandbox, lobby }), "The host's multiplayer protocol cannot play with this game's.");
            Expect(host, Offer("2545", lobby), "Sandbox 0.2.0 is missing.");
            Expect(host, Offer("2545", sandbox), "Lobby 1.0.0 is missing.");
            Expect(host, Offer("2545", Offered("io.example.mine", "1.0.0", 'f', InternalContentMode.ImplicitShared), sandbox, lobby),
                "The host does not run io.example.mine; disable it to join.");
            Expect(host, Offer("2545", Offered("io.example.sandbox", "0.2.0", 'b', InternalContentMode.ImplicitShared), lobby), "Sandbox 0.2.0 differs from the host's copy.");
            Expect(host, Offer("2545", Offered("io.example.sandbox", "0.3.0", 'b', InternalContentMode.ImplicitShared), lobby), "Sandbox: the host runs 0.2.0; this game has 0.3.0.");
            Expect(host, Offer("2545", sandbox, OfferedSession("io.example.lobby", "2.0.0", 'c', "2.0.0", ">=2.0.0 <3.0.0", new string('e', 64))),
                "Lobby: the host's 1.0.0 cannot play with this game's 2.0.0.");
            Expect(host, Offer("2545", sandbox, OfferedSession("io.example.lobby", "1.0.0", 'c', "1.0.0", ">=1.0.0 <2.0.0", new string('9', 64))),
                "Lobby: its synchronized files differ from the host's.");
            Expect(host, Offer("2545", sandbox, lobby, Offered("io.example.rules", "1.0.0", '2', InternalContentMode.ServerOnly)),
                "Rules runs only on a host; disable it to join.");
            Expect(host, Offer("2545", sandbox, lobby, Offered("io.example.solo", "1.0.0", '3', InternalContentMode.Standalone)),
                "io.example.solo refuses multiplayer; disable it to join.");
            var lockless = new InternalContentDescriptor("io.example.lobby", "1.0.0", new string('c', 64), InternalContentMode.Session,
                InternalContentPresence.Required, "1.0.0", ">=1.0.0 <2.0.0");
            Expect(host, Offer("2545", sandbox, lockless), "This game's copy of Lobby cannot be used in multiplayer.");
            var optionalDiffers = OfferedSession("io.example.extras", "1.0.0", 'd', "9.0.0", null, new string('e', 64), InternalContentPresence.Optional);
            Assert(MultiplayerContentAdmission.Evaluate("2545", host, Offer("2545", optionalDiffers, lobby, sandbox)).IsAdmitted,
                "an optional session mod that does not match stays inactive and blocks nothing");

            var several = MultiplayerContentAdmission.Evaluate("2545", host, Offer("2478"));
            Assert(several.Problems.SequenceEqual(new[]
                {
                    "The host runs Robotopia build 2545; this game runs 2478.",
                    "Lobby 1.0.0 is missing.",
                    "Sandbox 0.2.0 is missing."
                }),
                "several problems come in the planner's order (" + string.Join(" | ", several.Problems) + ")");

            var longName = Manifest("io.example.long", "1.0.0");
            longName.Name = new string('N', 300);
            var bounded = MultiplayerContentAdmission.Evaluate("2545", new[] { Mod(longName, 'a') }, Offer("2545"));
            Assert(bounded.Problems.Single().Length == 256 && bounded.Problems.Single().EndsWith("…", StringComparison.Ordinal),
                "a line stays within the report's 256-character display bound");
        }

        private static void HostChecksAreExactAndListWhatToChange()
        {
            var hosted = new[]
            {
                new InternalHostPackage("io.example.sandbox", "0.2.0", new string('a', 64), InternalHostPackageRole.Implicit),
                new InternalHostPackage("io.example.lobby", "1.0.0", new string('c', 64), InternalHostPackageRole.SessionRequired),
                new InternalHostPackage("io.example.extras", "1.0.0", new string('d', 64), InternalHostPackageRole.SessionOptional)
            };

            var fits = new[]
            {
                Offered("io.example.sandbox", "0.2.0", 'a', InternalContentMode.ImplicitShared),
                OfferedSession("io.example.lobby", "1.0.0", 'c', "1.0.0", null, new string('e', 64)),
                OfferedSession("io.example.notes", "1.0.0", '5', "1.0.0", null, new string('e', 64), InternalContentPresence.Optional),
                Offered("io.example.hud", "1.0.0", '6', InternalContentMode.ClientLocal)
            };

            Assert(MultiplayerContentAdmission.CheckHost(fits, hosted).IsAdmitted,
                "the exact packages fit; an optional extra here, a client-local mod and the host's optional mod do not matter");

            // A required session mod needs only to be here: its version and protocol are the host's planner's to judge.
            var newerLobby = new[]
            {
                Offered("io.example.sandbox", "0.2.0", 'a', InternalContentMode.ImplicitShared),
                OfferedSession("io.example.lobby", "1.0.1", 'f', "1.0.0", null, new string('e', 64))
            };

            Assert(MultiplayerContentAdmission.CheckHost(newerLobby, hosted).IsAdmitted,
                "a required session mod at another version passes this game's check and goes to the host's planner");
            var mismatched = new[]
            {
                Offered("io.example.sandbox", "0.2.0", 'b', InternalContentMode.ImplicitShared),
                Offered("io.example.lobby", "1.0.0", 'c', InternalContentMode.ClientLocal),
                Offered("io.example.mine", "1.0.0", '7', InternalContentMode.ImplicitShared),
                OfferedSession("io.example.party", "1.0.0", '8', "1.0.0", null, new string('e', 64)),
                Offered("io.example.rules", "1.0.0", '9', InternalContentMode.ServerOnly),
                Offered("io.example.solo", "1.0.0", '0', InternalContentMode.Standalone)
            };

            var verdict = MultiplayerContentAdmission.CheckHost(mismatched, hosted);
            Assert(verdict.Problems.SequenceEqual(new[]
                {
                    "io.example.sandbox 0.2.0 differs from the host's copy.",
                    "io.example.lobby 1.0.0 is missing.",
                    "The host does not run io.example.mine; disable it to join.",
                    "The host does not run io.example.party, which every player needs; disable it to join.",
                    "io.example.rules runs only on a host; disable it to join.",
                    "io.example.solo refuses multiplayer; disable it to join."
                }),
                "each package to install and each mod to disable is named (" + string.Join(" | ", verdict.Problems) + ")");
        }

        private static void TheBackgroundSettingIsRestored()
        {
            var platform = new RecordingMultiplayerPlatform(initial: false);
            var host = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingRuntimeLog(), platform);
            host.SetRunInBackground(false);
            Assert(platform.Writes.Count == 0, "turning it off before multiplayer turned it on changes nothing");
            host.SetRunInBackground(true);
            host.SetRunInBackground(true);
            Assert(platform.RunInBackground && host.HoldsRunInBackground, "online, the game runs in the background");
            host.SetRunInBackground(false);
            Assert(!platform.RunInBackground && !host.HoldsRunInBackground && platform.Writes.SequenceEqual(new[] { true, true, false }),
                "offline again, the game's own setting comes back");

            var alreadyInBackground = new RecordingMultiplayerPlatform(initial: true);
            var defaultHost = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingRuntimeLog(), alreadyInBackground);
            defaultHost.SetRunInBackground(true);
            defaultHost.SetRunInBackground(false);
            Assert(alreadyInBackground.RunInBackground, "a game that already ran in the background keeps doing so in single player");
        }

        private static void IdentityValuesAreBounded()
        {
            var host = new MultiplayerRuntimeHost("build 2545", "", new RecordingRuntimeLog());
            Assert(host.GameBuild == "unknown" && host.RuntimeVersion == TopiaForgeVersions.LoaderVersion, "a build or version outside the descriptor's form is replaced");
            Assert(host.DisplayName == MultiplayerRuntimeHost.DefaultDisplayName, "the display name starts as Player");
            host.PreferredDisplayName = "Robo Fan";
            Assert(host.DisplayName == "Robo Fan", "the settings rename the player");
            foreach (var bad in new[] { "", " padded ", new string('n', 25), "tab\tname" })
            {
                AssertThrows<ArgumentException>(() => host.PreferredDisplayName = bad, "'" + bad + "' is not a display name");
            }

            Assert(MultiplayerRuntimeHost.GameBuildOf(new RuntimeInfo("0.0.2545")) == "2545" && MultiplayerRuntimeHost.GameBuildOf(new RuntimeInfo()) == "unknown",
                "the build is the game version's patch number");
        }

        private static void TheCompositionRootRunsItsStagesByRole()
        {
            var trace = new List<string>();
            var control = new FakeMultiplayerControl(trace.Add);
            var log = new RecordingRuntimeLog();
            var platform = new RecordingMultiplayerPlatform(initial: true);
            var runtime = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log, platform), log)
            {
                WorldEvents = new RecordingFrameStage("world", trace.Add),
                PresenceSend = new RecordingFrameStage("presence", trace.Add),
                HostSnapshots = new RecordingFrameStage("snapshots", trace.Add),
                ReplicaInterpolation = new RecordingFrameStage("replicas", trace.Add)
            };

            Frame(runtime);
            Assert(trace.Count == 0, "before connecting, the frame touches nothing");
            Assert(runtime.Connect() && trace.SequenceEqual(new[] { "mp-attach" }), "connecting attaches the runtime host");
            trace.Clear();
            Frame(runtime);
            Assert(trace.SequenceEqual(new[] { "mp-pump" }), "in single player only the pump runs");

            control.Role = InternalSessionRole.ListenHost;
            trace.Clear();
            Frame(runtime);
            Assert(trace.SequenceEqual(new[] { "mp-pump", "world", "presence", "snapshots", "mp-flush" }), "a host runs every send stage, then flushes");
            control.Role = InternalSessionRole.Client;
            trace.Clear();
            Frame(runtime);
            Assert(trace.SequenceEqual(new[] { "mp-pump", "world", "presence", "mp-flush", "replicas" }),
                "a joined player sends no snapshots and interpolates replicas in LateUpdate (" + string.Join(",", trace) + ")");

            runtime.Disconnect();
            Assert(control.Detaches == 1 && runtime.Role == InternalSessionRole.Solo, "disconnecting detaches and goes offline");
            trace.Clear();
            Frame(runtime);
            Assert(trace.Count == 0, "after which the frame touches nothing again");
            control.RefuseAttach = true;
            Assert(!runtime.Connect() && log.Lines.Any(line => line.StartsWith("warn:", StringComparison.Ordinal)), "a control that refuses is reported");
        }

        private static void FrameFailuresAreIsolatedAndLoggedOnce()
        {
            var control = new FakeMultiplayerControl { Role = InternalSessionRole.ListenHost, PumpFailure = new InvalidOperationException("pump at 203.0.113.5:27250") };
            var log = new RecordingRuntimeLog();
            var stage = new RecordingFrameStage("presence", _ => { }) { Failure = new InvalidOperationException("stage") };
            var healthy = new RecordingFrameStage("snapshots", _ => { });
            var runtime = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log) { PresenceSend = stage, HostSnapshots = healthy };
            runtime.Connect();
            Frame(runtime);
            Frame(runtime);
            Frame(runtime);
            Assert(log.Lines.Count(line => line.Contains("pump failed", StringComparison.Ordinal)) == 1, "a failing pump is logged once");
            Assert(log.Lines.All(line => !line.Contains("203.0.113.5", StringComparison.Ordinal)), "without the address its message held");
            Assert(log.Lines.Count(line => line.Contains("frame stage failed", StringComparison.Ordinal)) == 1 && control.Flushes == 3,
                "a failing stage is logged once, a healthy one beside it does not re-arm it, and the flush still runs");
            control.PumpFailure = null;
            Frame(runtime);
            control.PumpFailure = new InvalidOperationException("again");
            Frame(runtime);
            Assert(log.Lines.Count(line => line.Contains("pump failed", StringComparison.Ordinal)) == 2, "a pump that recovered and fails again is logged again");
            control.FlushFailure = new InvalidOperationException("flush");
            control.PumpFailure = null;
            Frame(runtime);
            Frame(runtime);
            Assert(log.Lines.Count(line => line.Contains("flush failed", StringComparison.Ordinal)) == 1 && runtime.Control != null,
                "a failing flush is logged once and the provider stays attached");
        }

        private static void Frame(MultiplayerRuntime runtime)
        {
            runtime.BeforeModsUpdate(0.016f);
            runtime.AfterModsUpdate(0.016f);
            runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
        }

        private static void Expect(IReadOnlyList<MultiplayerAdmissionMod> host, InternalContentOffer offer, string problem)
        {
            var verdict = MultiplayerContentAdmission.Evaluate("2545", host, offer);
            Assert(!verdict.IsAdmitted && verdict.Problems.Contains(problem), "the host says '" + problem + "' (" + string.Join(" | ", verdict.Problems) + ")");
        }

        private static InternalContentOffer Offer(string build, params InternalContentDescriptor[] mods) =>
            new InternalContentOffer(build, MultiplayerModule.ProtocolVersion, MultiplayerModule.PeerVersionRange,
                mods.OrderBy(mod => mod.Id, StringComparer.Ordinal).ToArray());

        private static InternalContentDescriptor Offered(string id, string version, char hash, InternalContentMode mode) =>
            new InternalContentDescriptor(id, version, new string(hash, 64), mode);

        private static InternalContentDescriptor OfferedSession(string id, string version, char hash, string protocol, string? range, string lockHash,
            InternalContentPresence presence = InternalContentPresence.Required) =>
            new InternalContentDescriptor(id, version, new string(hash, 64), InternalContentMode.Session, presence, protocol, range,
                new[] { new InternalSynchronizedFile(Lock, lockHash) });

        private static MultiplayerAdmissionMod Mod(ModManifest manifest, char hash) => new MultiplayerAdmissionMod(manifest, new string(hash, 64));

        private static ModManifest Manifest(string id, string version)
        {
            var last = id.Substring(id.LastIndexOf('.') + 1);
            return new ModManifest { SchemaVersion = 6, Id = id, Name = char.ToUpperInvariant(last[0]) + last.Substring(1), Version = version };
        }

        private static ModManifest Mode(string id, string mode)
        {
            var manifest = Manifest(id, "1.0.0");
            manifest.Multiplayer = new ModMultiplayerMetadata { Mode = mode };
            return manifest;
        }

        private static ModManifest Session(string id, string version, string presence, string? range, string file, string hash)
        {
            var manifest = Manifest(id, version);
            manifest.Multiplayer = new ModMultiplayerMetadata
            {
                Mode = ModMultiplayerMetadata.SessionMode,
                Presence = presence,
                Protocol = new ModMultiplayerProtocol { Version = version, PeerVersionRange = range ?? string.Empty },
                SynchronizedFiles = new List<string> { file }
            };
            manifest.Hashes[file] = hash;
            return manifest;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Multiplayer runtime host test failed: " + message);
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Multiplayer runtime host test failed: " + message);
        }
    }
}
