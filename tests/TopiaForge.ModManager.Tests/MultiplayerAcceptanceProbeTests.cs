using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Xml.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.MultiplayerAcceptance;

namespace TopiaForge.ModManager.Tests
{
    internal static class MultiplayerAcceptanceProbeTests
    {
        internal static void Run()
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var context = new FakeModContext();
            context.Config.Seed(1, new AcceptanceConfig
            {
                Challenge = new string('a', 64),
                Peer = "host",
                Topology = "listen-pair",
                Checks = new[]
                {
                    new AcceptanceCheck { Id = "multiplayer.host-start", Mode = "automatic", Steps = "Confirm hosting." },
                    new AcceptanceCheck { Id = "multiplayer.invite", Mode = "automatic", Steps = "Join an invite." },
                    new AcceptanceCheck { Id = "multiplayer.avatar-pose", Mode = "manual", Steps = "Inspect both avatars." }
                }
            });
            context.Extensions.Register(rig.Server.Session);
            using var runner = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(context);
            runner.Load();
            Check(context.Ui.Surfaces.Count == 1, "The probe creates its kit window.");
            var surface = context.Ui.Surfaces.Single();
            surface.ActivateButton("pass-multiplayer.avatar-pose");
            Check(!context.Logger.Entries.Any(e => e.Message.StartsWith("TF-MP-")), "Solo play and premature clicks cannot attest to a pair.");
            rig.AddRemoteClient("guest");
            rig.Advance(8);
            Check(context.Logger.Entries.Count(e => e.Message.StartsWith("TF-MP-SESSION|READY|")) == 1, "The public session confirms two peers once.");
            Check(context.Logger.Entries.Any(e => e.Message.Contains("|PASS|") && e.Message.Contains("host-start")), "Hosting marker follows pair readiness.");
            Check(context.Logger.Entries.Any(e => e.Message.Contains("|NOT-RUN|") && e.Message.Contains("invite")), "A service scenario cannot pass without its driver.");
            Check(!context.Logger.Entries.Any(e => e.Message.Contains("avatar-pose")), "Manual observations require an action.");
            surface.ActivateButton("pass-multiplayer.avatar-pose");
            surface.ActivateButton("fail-multiplayer.avatar-pose");
            Check(context.Logger.Entries.Any(e => e.Level == CapturedLogLevel.Error && e.Message.Contains("|FAIL|") && e.Message.Contains("avatar-pose")), "A later failure cannot be hidden by a pass.");
            var count = context.Logger.Entries.Count;
            rig.Advance(100);
            Check(context.Logger.Entries.Count == count, "Steady session updates do not repeat evidence.");
            runner.Unload();
            context.AssertNoLeaks();
            RoleBinding();
            ProbeCompatibility();
            Console.WriteLine("Multiplayer acceptance probe: 8 behavioral checks passed.");
        }
        private static void ProbeCompatibility()
        {
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root != null && !File.Exists(Path.Combine(root.FullName, "TopiaForge.slnx"))) root = root.Parent;
            Check(root != null, "Locate the current source manifests.");
            var directory = Path.Combine(root!.FullName, "tests", "TopiaForge.MultiplayerAcceptanceMod");
            var manifest = ModManifestJson.Deserialize(File.ReadAllText(Path.Combine(directory, "topiaforge.mod.json")));
            var errors = new List<string>(ManifestValidator.Validate(manifest,
                new ManifestValidationContext("0.0.2545", requireKnownGameVersion: true,
                    platform: "windows", architecture: "x64", contentTargets: new[] { "code" }, enforceRuntimeCompatibility: true)));
            foreach (var name in new[] { "Multiplayer", "Worlds" })
            {
                var provider = ModManifestJson.Deserialize(File.ReadAllText(Path.Combine(root.FullName, "mods", "TopiaForge." + name, "topiaforge.mod.json")));
                if (!manifest.Dependencies.TryGetValue(provider.Id, out var range) || !VersionUtil.AllowsRange(provider.Version, range))
                    errors.Add("The probe must admit the current " + name + " provider " + provider.Version + ".");
            }
            var project = XDocument.Load(Path.Combine(directory, "TopiaForge.MultiplayerAcceptanceMod.csproj"));
            if (manifest.Version != TopiaForgeVersions.LoaderVersion || project.Descendants("Version").Single().Value != manifest.Version)
                errors.Add("The probe project and manifest must follow the current release version.");
            Check(errors.Count == 0, string.Join(" ", errors));
            Console.WriteLine("Multiplayer acceptance current package: 5 compatibility checks passed.");
        }
        private static void RoleBinding()
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            var guest = rig.AddRemoteClient("guest");
            rig.Advance(8);
            foreach (var peer in new[] { "host", "guest" })
                foreach (var server in new[] { true, false })
                {
                    using var context = new FakeModContext();
                    context.Config.Seed(1, new AcceptanceConfig
                    {
                        Challenge = new string('b', 64),
                        Peer = peer,
                        Topology = "listen-pair"
                    });
                    context.Extensions.Register(server ? rig.Server.Session : guest.Session);
                    using var runner = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(context);
                    runner.Load();
                    var ready = context.Logger.Entries.Count(e => e.Message == "TF-MP-SESSION|READY|" + new string('b', 64) + "|" + peer);
                    Check(ready == (server == (peer == "host") ? 1 : 0), "The config role must match the actual public session role.");
                    runner.Unload();
                    context.AssertNoLeaks();
                }
            using var ordinary = new FakeModContext();
            ordinary.Extensions.Register(rig.Server.Session);
            using var unconfigured = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(ordinary);
            unconfigured.Load();
            Check(ordinary.Ui.Surfaces.Count == 0 && !ordinary.Logger.Entries.Any(e => e.Message.StartsWith("TF-MP-")),
                "An ordinary unconfigured load cannot expose acceptance controls or evidence.");
            unconfigured.Unload();
            ordinary.AssertNoLeaks();
            Console.WriteLine("Multiplayer acceptance role/config: 5 behavioral checks passed.");
        }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Acceptance probe: " + message); }
    }
}
