using System;
using TopiaForge.Chronos;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Chronos' multiplayer rules (WS3.13), Unity-free: the refusals in the game's own words, the native pause that holds the
    /// clock only offline, the restore that waits for it only offline, the session time Chronos describes, and a joined
    /// player's plan. Offline every answer is single player's own.
    /// </summary>
    internal static class ChronosNetRulesTests
    {
        internal static void Run()
        {
            AContextWithoutTheSeamIsSinglePlayer();
            TheNativePauseHoldsTheClockOnlyOffline();
            TheSessionTimeDescribesChronosState();
            AJoinedPlayerFollowsTheHostsPlan();
        }

        private static void AContextWithoutTheSeamIsSinglePlayer()
        {
            That(ChronosNetRules.Refusal(null) == null, "a context without the runtime's seam may change time, as in single player");
            foreach (var message in new[] { ChronosNetRules.HostRefusal, ChronosNetRules.ClientRefusal, ChronosNetRules.ClientLocalRefusal })
            {
                That(message.EndsWith(".", StringComparison.Ordinal) && message.Contains("time", StringComparison.OrdinalIgnoreCase) &&
                     !message.Contains("NotAuthoritative", StringComparison.Ordinal) && !message.Contains("GamemodeServer", StringComparison.Ordinal),
                    "a refusal is a sentence in the game's own words: " + message);
            }

            // A server controller's own code that resolved Chronos outside its session's scope meets the host's refusal too, so
            // it says where the server controller's time control comes from.
            That(ChronosNetRules.HostRefusal.Contains("the time control it resolves from its session's Context", StringComparison.Ordinal),
                "the host's refusal says where a server controller's time control comes from: " + ChronosNetRules.HostRefusal);
        }

        private static void TheNativePauseHoldsTheClockOnlyOffline()
        {
            var scales = new[] { 0f, 0.25f, 1f };
            foreach (var explicitPause in new[] { false, true })
            {
                foreach (var hasWritten in new[] { false, true })
                {
                    foreach (var owned in scales)
                    {
                        foreach (var observed in scales)
                        {
                            var label = "explicit " + explicitPause + ", written " + hasWritten + ", owned " + owned + ", observed " + observed;
                            That(ChronosNetRules.NativePauseHolds(true, explicitPause, hasWritten, owned, observed) ==
                                 TimeScaleOwnership.IsNativePaused(explicitPause, hasWritten, owned, observed), "offline the pause rule is single player's (" + label + ")");
                            That(!ChronosNetRules.NativePauseHolds(false, explicitPause, hasWritten, owned, observed), "online no pause ever holds the clock (" + label + ")");

                            var solo = TimeScaleOwnership.PlanRestore(explicitPause, hasWritten, owned, observed);
                            var offline = ChronosNetRules.PlanRestore(true, explicitPause, hasWritten, owned, observed);
                            That(offline.WriteBaseline == solo.WriteBaseline && offline.RetainOwnership == solo.RetainOwnership,
                                "offline the restore is single player's (" + label + ")");
                            var online = ChronosNetRules.PlanRestore(false, explicitPause, hasWritten, owned, observed);
                            That(online.WriteBaseline == hasWritten && !online.RetainOwnership, "online real time comes back at once (" + label + ")");
                        }
                    }
                }
            }
        }

        private static void TheSessionTimeDescribesChronosState()
        {
            That(ChronosNetRules.TryDescribe(1f, TimeMode.Realtime, out var real) && real.Equals(InternalSessionTime.Realtime), "real time");
            That(ChronosNetRules.TryDescribe(0f, TimeMode.Paused, out var frozen) && frozen.IsFrozen && frozen.Mode == InternalTimeMode.Paused, "a freeze");
            That(ChronosNetRules.TryDescribe(0.5f, TimeMode.Slowed, out var slow) && slow.Scale == 0.5f && slow.Mode == InternalTimeMode.Slowed, "a slow");
            That(ChronosNetRules.TryDescribe(1f, TimeMode.TurnBased, out var turns) && turns.Mode == InternalTimeMode.TurnBased, "an actor's turn");
            That(!ChronosNetRules.TryDescribe(float.NaN, TimeMode.Realtime, out _) && !ChronosNetRules.TryDescribe(1.5f, TimeMode.Slowed, out _),
                "a driver's scale that is not a number, or out of range, describes no session time and is never published");
        }

        private static void AJoinedPlayerFollowsTheHostsPlan()
        {
            var ledger = new LeaseLedger();
            var plain = TimeScalePlan.Follow(ledger, 0.5f, TimeMode.Slowed);
            That(plain.WorldScale == 0.5f && plain.Mode == TimeMode.Slowed && !plain.ExemptPlayer && plain.WriteNativeScale,
                "a joined player runs at the host's scale and mode, and always writes it");
            ledger.Add(LeaseKind.Freeze, "io.example.local", "ignored");
            ledger.Add(LeaseKind.ExemptPlayer, "io.example.local", "run");
            var exempt = TimeScalePlan.Follow(ledger, 0.5f, TimeMode.Slowed);
            That(exempt.WorldScale == 0.5f && exempt.ExemptPlayer, "its own leases only exempt its player: the host's scale wins over a local freeze");
            That(!TimeScalePlan.Follow(ledger, 1f, TimeMode.Realtime).ExemptPlayer, "at real time there is nothing to exempt the player from");
            That(TimeScalePlan.Follow(ledger, 0f, TimeMode.Paused).WriteNativeScale, "and a host's freeze is written even behind this player's pause menu");
        }
    }
}
