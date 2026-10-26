using System;
using System.Linq;
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
            Console.WriteLine("Multiplayer acceptance probe: 8 behavioral checks passed.");
        }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Acceptance probe: " + message); }
    }
}
