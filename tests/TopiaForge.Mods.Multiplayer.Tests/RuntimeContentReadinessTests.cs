extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ContentOfferCodec = provider::TopiaForge.Multiplayer.ContentOfferCodec;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Packages copied in without an install receipt are hashed off the main thread, so while that work runs the provider
/// neither hosts nor joins with them: hosting is refused as not ready yet, a join and the developer switch wait, nothing is
/// announced, offered or admitted, nothing is reported as a failure, and nothing opens a network. Once the work finishes,
/// both go ahead. Installed packages are identified by their receipts at once and never wait.
/// </summary>
internal static class RuntimeContentReadinessTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a').WithoutReceipt();

    internal static void PendingContentIsNeitherAnnouncedNorAdmitted()
    {
        var root = Directory.CreateTempSubdirectory("TopiaForgePendingContent-").FullName;
        try
        {
            var work = new ManualWorkScheduler();
            var runtime = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog(), contentScheduler: work);
            runtime.Inventory.Add(Sandbox.ToManifest(), Sandbox.Install(root));
            var reports = new List<InternalMultiplayerDiagnostic>();
            var content = new RuntimeContent("2545", "0.2.0") { Runtime = runtime, Report = reports.Add };
            That(content.IsLocalContentPending && work.Pending == 1, "looking starts identifying, off this thread, and says it is pending");
            That(content.HostPackages() == null && content.ClientContent().Length == 0, "while it runs nothing is announced or offered");
            var offer = ContentOfferCodec.TryEncode(new InternalContentOffer("2545", "1.0.0", ">=1.0.0 <2.0.0", Array.Empty<InternalContentDescriptor>()))!;
            var admission = content.Admit(offer);
            That(!admission.IsAdmitted && admission.Code == AdmissionCode.NotReady && admission.Problems.SequenceEqual(new[] { RuntimeContent.HostNotReady }),
                "and nobody is admitted, as a host not ready yet rather than one that cannot read its mods");
            That(reports.Count == 0 && runtime.RecentDiagnostics.Count == 0, "nothing failed, so nothing is reported");
            work.RunAll();
            That(!content.IsLocalContentPending && content.HostPackages()!.Single().Id == Sandbox.Id, "once identified, the packages are announced");

            var installed = PackageSpec.Implicit("io.example.installed", "1.0.0", 'b');
            var receipted = new MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog(), contentScheduler: new ManualWorkScheduler());
            receipted.Inventory.Add(installed.ToManifest(), installed.Install(root));
            var ready = new RuntimeContent("2545", "0.2.0") { Runtime = receipted, Report = reports.Add };
            That(!ready.IsLocalContentPending && ready.HostPackages()!.Single().Id == installed.Id, "an installed package never waits: its receipt identifies it at once");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    internal static void HostingWaitsForIdentification()
    {
        using var rig = new ControlRig(181, Sandbox);
        var work = new ManualWorkScheduler();
        rig.ContentScheduler = work;
        var host = rig.AddPeer("patient-host", Sandbox);
        var refused = host.Control.StartHosting(new InternalHostSettings("Waiting", 4));
        That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Unavailable && refused.ErrorMessage == RuntimeContent.ContentNotReady,
            "hosting is refused as not ready while the mods are identified (" + refused.ErrorCode + ": " + refused.ErrorMessage + ")");
        That(host.Factory.Networks.Count == 0 && !host.Runtime.RecentDiagnostics.Any(record => record.Code == "content.read-failed"),
            "nothing opened, and nothing was reported as unreadable");
        work.RunAll();
        That(host.Control.StartHosting(new InternalHostSettings("Ready", 4)).Succeeded, "once identified, hosting starts");
        That(rig.Run(() => host.Control.GetStatus().HostPhase == InternalHostPhase.Hosting), "and serves");
    }

    internal static void JoinsAndTheDeveloperSwitchWaitForIdentification()
    {
        using var rig = new ControlRig(182, Sandbox);
        rig.StartHosting(new InternalHostSettings("Host", 8));
        var work = new ManualWorkScheduler();
        rig.ContentScheduler = work;
        var guest = rig.AddPeer("patient-guest", Sandbox);
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the join is accepted");
        rig.Run(() => false, maximumMicros: 2_000_000);
        var waiting = guest.Control.GetStatus();
        That(waiting.JoinPhase == InternalJoinPhase.Resolving && guest.Factory.Networks.Count == 0 && guest.Control.Role == InternalSessionRole.Solo,
            "it waits for the mods to be identified, contacting nothing (" + waiting.JoinPhase + ", " + guest.Factory.Networks.Count + " networks)");
        work.RunAll();
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession), "once identified, it goes ahead and gets in");

        var devWork = new ManualWorkScheduler();
        rig.ContentScheduler = devWork;
        var dev = rig.AddPeer("dev-waits", Sandbox);
        var logger = new RecordingModLogger();
        var developer = new provider::TopiaForge.Multiplayer.DeveloperSessions(
            dev.Provider.Live!, logger, null, JoinTarget.Direct("host.test", 27250), "Dev", 27250, () => dev.Content.IsLocalContentPending);
        rig.Run(() =>
        {
            developer.Pump();
            return false;
        }, maximumMicros: 2_000_000);
        That(dev.Provider.Live!.Join == null && dev.Factory.Networks.Count == 0, "the developer switch waits too, starting nothing");
        devWork.RunAll();
        That(rig.Run(() =>
        {
            developer.Pump();
            return dev.Provider.Live!.Join?.Phase == JoinPhase.InSession;
        }), "and joins once the mods are identified");
    }
}
