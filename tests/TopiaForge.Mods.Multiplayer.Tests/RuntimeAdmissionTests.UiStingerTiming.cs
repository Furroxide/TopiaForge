using System.Text;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void StingerReservationsUseTheLongerExit()
    {
        StingerReservation(197, null, matchingGuest: false, instant: false, before: 1_050_000, after: 1_241_000);
        StingerReservation(198, 480, matchingGuest: true, instant: false, before: 1_350_000, after: 1_481_000);
        StingerReservation(199, 100, matchingGuest: false, instant: false, before: 1_180_000, after: 1_241_000);
        StingerReservation(200, null, matchingGuest: false, instant: true, before: 1_050_000, after: 1_241_000);
        Console.WriteLine("Addressed stingers verified the stock, declared, fallback and reduced-motion exit reservations.");
    }

    private static void StingerReservation(int seed, int? slowMs, bool matchingGuest, bool instant, long before, long after)
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        AdmittedUiCatalogue? catalogue = null;
        if (slowMs.HasValue)
        {
            var definition = $$$"""
                {"schema":1,"id":"stinger-exit","motion":{"fastMs":40,"baseMs":80,"slowMs":{{{slowMs.Value}}}}}
                """;
            var parsed = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(definition));
            That(parsed.Succeeded, parsed.ErrorMessage);
            var theme = parsed.Value!;
            catalogue = new AdmittedUiCatalogue(null, new Dictionary<string, UiThemeDescriptor> { [theme.Id] = theme });
        }

        using var rig = new ControlRig(seed, Sandbox);
        long? clock = null;
        using var host = new UiDeliveryEndpoint(rig.Host, () => clock ?? rig.Network.Now, author: true, art: catalogue);
        var peer = rig.AddPeer("Stinger timing", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => clock ?? rig.Network.Now, author: false,
            art: matchingGuest ? catalogue : null);
        guest.Ui.StandardStingerExit = !instant;
        guest.Ui.StingerTimeOverride = () => (clock ?? rig.Network.Now) / 1_000_000d;
        host.Ui.StingerFontFit = slowMs.HasValue ? UiStingerFontFit.BothMeasured : UiStingerFontFit.KitOnly;
        rig.StartHosting(new InternalHostSettings("Stinger timing", 4));
        rig.JoinAndWait(peer);
        var target = OverlayTarget(rig, host, peer);
        if (catalogue != null)
            That(host.Ui.Themes.UseTheme(new UiThemeRef("stinger-exit")).Succeeded, "The author selects its authenticated exit duration.");
        var originalStream = host.Players.Channel!.GetGeneration(target.Id);
        var start = rig.Network.Now;
        clock = start;
        for (var index = 0; index < 4; index++)
            That(target.Ui.ShowStinger(AStinger("Timed " + index, 1d)).Succeeded, "The addressed scope admits its first four announcements.");
        That(rig.Run(() => guest.Ui.RemoteStingers.Count == 4), "All four original timed announcements arrive.");
        That(guest.Ui.RemoteStingers.All(item => item.FontFit == host.Ui.StingerFontFit), "The actual sender propagates its measured font mode.");

        clock = start + 500_000;
        if (slowMs == 480)
            That(host.Ui.Themes.UseTheme(null).Succeeded, "Existing accepted entries keep their long exit after the author changes theme.");
        That(target.Ui.ShowStinger(AStinger("Timed 0", 5d)).Succeeded, "A duplicate joins the existing reservation without replacing its duration.");
        clock = start + before;
        var refused = target.Ui.ShowStinger(AStinger("Fifth", 1d));
        That(refused.ErrorCode == ModErrorCode.RateLimited && guest.Ui.RemoteStingers.Count == 4,
            $"The fifth addressed offer cannot dispatch before the longer reserved exit. Got {refused.ErrorCode} for {slowMs}.");
        clock = start + after;
        That(target.Ui.ShowStinger(AStinger("After exit", 1d)).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == 5), "The first complete exit makes room for the fifth delivery.");

        if (seed != 197) return;
        var resets = guest.Ui.StingerResets;
        That(rig.Host.Control.Kick(target.Id, "Retire admission stream.").Succeeded &&
            rig.Run(() => (host.Players.Channel?.GetGeneration(target.Id) ?? 0) == 0 && guest.Ui.StingerResets > resets),
            "Both ends retire the original reservation stream.");
        rig.JoinAndWait(peer);
        var replacement = OverlayTarget(rig, host, peer);
        var replacementStream = host.Players.Channel?.GetGeneration(replacement.Id) ?? 0;
        That(replacementStream != 0 && replacementStream != originalStream &&
            replacement.Ui.ShowStinger(AStinger("Replacement stream", 1d)).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == 6),
            "A replacement authenticated stream can send while the retired reservation would still be full.");
    }

    private static void FullReceivedQueuesEmitBoundedDiagnostics()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(201, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Delayed stinger receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Delayed stinger", 4));
        rig.JoinAndWait(peer);
        var target = OverlayTarget(rig, host, peer);
        guest.Ui.StandardStingerExit = true;
        guest.Ui.StingerTimeOverride = () => 1d;
        var channel = host.Players.Channel!;
        var to = InternalSystemTarget.Only(target.Id);
        var envelope = UiPresentationTheme.EventEnvelope(Sandbox.Id, SignedStingerDigest(rig, Sandbox.Id), null);

        for (var index = 0; index < 4; index++)
            That(channel.Send(TestStingerFrame(target.Id.Value, envelope, AStinger("Delayed original " + index)).Segment, to).Succeeded,
                "The authenticated test sender fills a single receiver scope.");
        That(rig.Run(() => guest.Ui.RemoteStingers.Count == 4), "All four entries reached the receiver before the late offers.");
        var refused = guest.Players.Rejected;
        foreach (var address in new[] { target.Id.Value, string.Empty })
            for (var index = 0; index < 2; index++)
                That(channel.Send(TestStingerFrame(address, envelope, AStinger("Private refused " + index)).Segment, to).Succeeded,
                    "The provider sends an authenticated event that reaches the full guest queue.");
        That(rig.Run(() => guest.Players.Rejected == refused + 4) && guest.Ui.RemoteStingers.Count == 4,
            "All late addressed and broadcast offers are refused by the same full receiver scope.");
        That(peer.Log.Lines.Count(line => line.Contains("an addressed stinger was dropped", StringComparison.Ordinal)) == 1 &&
            peer.Log.Lines.Count(line => line.Contains("a broadcast stinger was dropped", StringComparison.Ordinal)) == 1 &&
            !peer.Log.Lines.Any(line => line.Contains("Private refused", StringComparison.Ordinal)),
            "Full queue diagnostics are bounded to one per delivery pattern and omit announcement text.");
    }
}
