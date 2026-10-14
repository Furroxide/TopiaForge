using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Presentation routing, HUD coalescing, and toast and cue throttling (WS3.12).</summary>
    internal static class MultiplayerPresentationTests
    {
        private static readonly PresentationKind[] Kinds =
        {
            PresentationKind.HudSurface, PresentationKind.Window, PresentationKind.Tool,
            PresentationKind.Modal, PresentationKind.Toast, PresentationKind.AudioCue
        };

        public static void Run()
        {
            RoutesFollowThePlanTable();
            TargetedCallsReachOnePlayer();
            HudMirrorsAreCoalescedToTenHertz();
            HudMirrorsNeverExceedTenHertzAndEndOnTheLatestValue();
            BroadcastsAreDeduplicatedAndRateLimited();
            Console.WriteLine("MultiplayerPresentationTests passed.");
        }

        private static void RoutesFollowThePlanTable()
        {
            Equal(PresentationRoute.MirrorToEveryone, Route(PresentationKind.HudSurface), "HUD surfaces are mirrored");
            Equal(PresentationRoute.BroadcastToEveryone, Route(PresentationKind.Toast), "toasts are broadcast");
            Equal(PresentationRoute.BroadcastToEveryone, Route(PresentationKind.AudioCue), "audio cues are broadcast");
            Equal(PresentationRoute.HostOnly, Route(PresentationKind.Window), "windows stay with the host");
            Equal(PresentationRoute.HostOnly, Route(PresentationKind.Tool), "tools stay with the host");
            Equal(PresentationRoute.HostOnly, Route(PresentationKind.Modal), "modals stay with the host");

            var checkedCases = 0;
            foreach (var realm in new[] { NetRealm.Offline, NetRealm.ListenHost, NetRealm.Client })
            {
                for (var context = ModExecutionContext.Process; context <= ModExecutionContext.Participant; context++)
                {
                    foreach (var kind in Kinds)
                    {
                        checkedCases++;
                        if (realm == NetRealm.ListenHost && context == ModExecutionContext.GamemodeServer) continue;
                        Equal(PresentationRoute.Local, PresentationRoutingPolicy.Route(realm, context, kind), "every other context renders locally");
                    }
                }
            }

            Equal(72, checkedCases, "every realm, context and kind");
            Throws<ArgumentOutOfRangeException>(() => PresentationRoutingPolicy.Route(NetRealm.Offline, ModExecutionContext.Process, (PresentationKind)6), "undefined kind");

            static PresentationRoute Route(PresentationKind kind) =>
                PresentationRoutingPolicy.Route(NetRealm.ListenHost, ModExecutionContext.GamemodeServer, kind);
        }

        private static void TargetedCallsReachOnePlayer()
        {
            foreach (var kind in Kinds)
            {
                foreach (var realm in new[] { NetRealm.Offline, NetRealm.ListenHost, NetRealm.Client })
                {
                    Equal(PresentationRoute.Local, PresentationRoutingPolicy.RouteTargeted(realm, true, kind), "the local player renders locally");
                }

                Equal(PresentationRoute.TargetPlayer, PresentationRoutingPolicy.RouteTargeted(NetRealm.ListenHost, false, kind), "the host targets a player");
                Equal(PresentationRoute.Refused, PresentationRoutingPolicy.RouteTargeted(NetRealm.Client, false, kind), "a client cannot drive another player's UI");
                Equal(PresentationRoute.Refused, PresentationRoutingPolicy.RouteTargeted(NetRealm.Offline, false, kind), "single player has nobody else");
            }

            True(PresentationRoutingPolicy.IsInteractive(PresentationKind.Window), "windows stay interactive remotely");
            True(PresentationRoutingPolicy.IsInteractive(PresentationKind.Modal), "modals stay interactive remotely");
            True(PresentationRoutingPolicy.IsInteractive(PresentationKind.Tool), "tools stay interactive remotely");
            True(!PresentationRoutingPolicy.IsInteractive(PresentationKind.HudSurface), "HUD surfaces are read-only");
            True(!PresentationRoutingPolicy.IsInteractive(PresentationKind.Toast), "toasts are read-only");
        }

        private static void HudMirrorsAreCoalescedToTenHertz()
        {
            var hud = new HudMirrorCoalescer();
            Equal(HudMirrorDecision.SendNow, hud.Offer(1, 0), "the first change goes at once");
            Equal(long.MaxValue, hud.NextDueMicros, "nothing waits");
            Equal(HudMirrorDecision.Deferred, hud.Offer(2, 50_000), "a change inside 100 ms waits");
            Equal(HudMirrorDecision.Deferred, hud.Offer(3, 60_000), "a newer change replaces it");
            Equal(100_000L, hud.NextDueMicros, "due 100 ms after the last send");
            True(!hud.TryFlush(99_999, out _), "not before then");
            True(hud.TryFlush(100_000, out var flushed) && flushed == 3, "the newest value goes when due");
            Equal(HudMirrorDecision.Unchanged, hud.Offer(3, 120_000), "the value peers already have");
            Equal(HudMirrorDecision.Deferred, hud.Offer(4, 150_000), "a change waits");
            Equal(HudMirrorDecision.Unchanged, hud.Offer(3, 160_000), "changing back cancels it");
            True(!hud.HasPending && !hud.TryFlush(200_000, out _), "so nothing is sent");
            Equal(HudMirrorDecision.SendNow, hud.Offer(5, 200_000), "100 ms after the last send a change goes at once");
            Throws<ArgumentOutOfRangeException>(() => new HudMirrorCoalescer(0), "interval");
        }

        private static void HudMirrorsNeverExceedTenHertzAndEndOnTheLatestValue()
        {
            var random = new SeededRandom(53);
            var hud = new HudMirrorCoalescer();
            var sends = new List<long>();
            ulong lastSent = 0;
            ulong lastOffered = 0;
            var now = 0L;
            for (var frame = 0; frame < 20_000; frame++)
            {
                now += 16_667;
                if (random.NextInt(0, 3) == 0)
                {
                    lastOffered = (ulong)random.NextInt(0, 6);
                    if (hud.Offer(lastOffered, now) == HudMirrorDecision.SendNow)
                    {
                        sends.Add(now);
                        lastSent = lastOffered;
                    }
                }

                if (hud.TryFlush(now, out var flushed))
                {
                    sends.Add(now);
                    lastSent = flushed;
                }
            }

            now += PresentationRoutingPolicy.HudMirrorIntervalMicros;
            if (hud.TryFlush(now, out var final))
            {
                sends.Add(now);
                lastSent = final;
            }

            for (var index = 1; index < sends.Count; index++)
            {
                True(sends[index] - sends[index - 1] >= PresentationRoutingPolicy.HudMirrorIntervalMicros, "two mirrored updates under 100 ms apart");
            }

            True(sends.Count > 1000, "the mirror still updates often: " + sends.Count);
            Equal(lastOffered, lastSent, "peers always end on the latest value");
        }

        private static void BroadcastsAreDeduplicatedAndRateLimited()
        {
            var toasts = new PresentationBroadcastLimiter(0);
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 1, 0), "a toast");
            for (var time = 1; time <= 10; time++)
            {
                Equal(BroadcastDecision.Duplicate, toasts.Offer(PresentationKind.Toast, 1, time), "the same toast again");
            }

            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 2, 11), "duplicates cost no tokens (2)");
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 3, 11), "duplicates cost no tokens (3)");
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 4, 11), "duplicates cost no tokens (4)");
            Equal(BroadcastDecision.RateLimited, toasts.Offer(PresentationKind.Toast, 5, 11), "a burst of four");
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.AudioCue, 5, 11), "cues have their own budget");
            // 22 micro-tokens were left at 11 us; two tokens a second complete the next token at exactly 500 ms.
            Equal(BroadcastDecision.RateLimited, toasts.Offer(PresentationKind.Toast, 5, 499_999), "two a second: not yet");
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 5, 500_000), "a rate-limited toast was not remembered");
            Equal(BroadcastDecision.Duplicate, toasts.Offer(PresentationKind.Toast, 1, 1_999_999), "within two seconds");
            Equal(BroadcastDecision.Send, toasts.Offer(PresentationKind.Toast, 1, 2_000_000), "after two seconds");

            var cues = new PresentationBroadcastLimiter(0);
            Equal(BroadcastDecision.Send, cues.Offer(PresentationKind.AudioCue, 9, 0), "a cue");
            Equal(BroadcastDecision.Duplicate, cues.Offer(PresentationKind.AudioCue, 9, 249_999), "the same cue inside 250 ms");
            Equal(BroadcastDecision.Send, cues.Offer(PresentationKind.AudioCue, 9, 250_000), "after 250 ms");
            for (ulong hash = 100; hash < 107; hash++) Equal(BroadcastDecision.Send, cues.Offer(PresentationKind.AudioCue, hash, 250_000), "cue burst");
            Equal(BroadcastDecision.RateLimited, cues.Offer(PresentationKind.AudioCue, 200, 250_000), "eight cues then four a second");
            Equal(BroadcastDecision.Duplicate, cues.Offer(PresentationKind.AudioCue, 9, 250_001), "a repeat is caught after other sends");

            Throws<ArgumentException>(() => cues.Offer(PresentationKind.Window, 1, 0), "only toasts and cues are broadcast");
            Throws<ArgumentOutOfRangeException>(() => cues.Offer(PresentationKind.Toast, 1, -1), "negative time");
        }
    }
}
