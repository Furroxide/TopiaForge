using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        // Keep deterministic uplink behavior testable even when optional live conversation services are enabled.
        private static void DisabledShopSkipsCreditsAndRequisitionFeedback()
        {
            var config = FastConfig();
            config.ScorePerKill = 100;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "a disabled-shop score target should spawn");
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);
            harness.Controller.TestingFireZapper(charged: false);
            harness.Advance(0.01f);

            var hud = FindSurface(harness.Context, "zombies-status");
            Assert(harness.Controller.TestingScore == 100
                && harness.Controller.TestingCredits == 0
                && harness.Controller.TestingPhase == ZombiesPhase.InterWave
                && hud.Body.IndexOf("CREDITS", StringComparison.Ordinal) < 0
                && string.Equals(LastToast(harness.Context), "Wave clear.", StringComparison.Ordinal),
                "disabling requisitions should remove its economy, HUD line, and wave-clear prompt without affecting score");
        }

        private static void UplinkFailuresExplainWithoutSpendingCharge()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.SuggestibilityMin = 1f;
            config.SuggestibilityMax = 1f;
            config.LoyaltyMin = 0f;
            config.LoyaltyMax = 0f;
            config.CorruptionBase = 0f;
            config.CorruptionPerWave = 0f;
            config.BiasAmplitude = 0f;
            config.OverrideResistGrunt = 0f;
            config.OverrideDifficulty = 0.25f;
            // Short enough that clearing it does not simulate 22 seconds of wave, which would strand and despawn
            // the responder and end the phase this case is about.
            config.BroadcastCooldownSeconds = 0.5f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "an uplink feedback target should spawn");
            var enemy = harness.Controller.TestingEnemies[0];
            var initialCharges = harness.Controller.TestingUplinkCharges;

            harness.Context.Input.SetValue("jack-in", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);
            Assert(LastToast(harness.Context).IndexOf("targeted", StringComparison.OrdinalIgnoreCase) >= 0
                && harness.Controller.TestingUplinkCharges == initialCharges,
                "JACK IN should explain a missing target without consuming charge");

            enemy.Freeze(10f, standDown: false);
            Assert(!harness.Controller.BroadcastStandDown().Succeeded
                && LastToast(harness.Context).IndexOf("No hostile", StringComparison.OrdinalIgnoreCase) >= 0
                && harness.Controller.TestingUplinkCharges == initialCharges,
                "stand-down should explain an empty responder set without consuming charge");

            // Finding nobody costs no charge, but it does start the cooldown — otherwise holding the key is a
            // free proximity scanner that eventually catches a robot wandering into range.
            enemy.RestoreHostile(config);
            Assert(!harness.Controller.BroadcastStandDown().Succeeded
                && LastToast(harness.Context).IndexOf("cooling down", StringComparison.OrdinalIgnoreCase) >= 0,
                "an empty responder set must still rate-limit the transmitter");

            harness.Advance(config.BroadcastCooldownSeconds + 0.05f);
            Assert(harness.Controller.BroadcastStandDown().Succeeded,
                "the deterministic responder should acknowledge an in-range stand-down");
            var afterSuccess = harness.Controller.TestingUplinkCharges;
            Assert(!harness.Controller.BroadcastStandDown().Succeeded
                && LastToast(harness.Context).IndexOf("cooling down", StringComparison.OrdinalIgnoreCase) >= 0
                && harness.Controller.TestingUplinkCharges == afterSuccess,
                "cooldown rejection should be visible and must not double-spend uplink charge");
        }

        private static void UplinkEconomyRunsOnTheWorldClock()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.OverrideCharges = 2;
            config.OverrideChargeRegenSeconds = 4f;
            using var harness = new Harness(config, withChronos: true);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "an economy target should spawn");

            Assert(harness.Controller.BroadcastStandDown().Succeeded, "the first broadcast should land");
            var spent = harness.Controller.TestingUplinkCharges;
            Assert(spent < config.OverrideCharges, "a successful broadcast spends charge");

            // A stand-down freezes the world. On the control clock the cooldown and charge regeneration kept
            // running through the freeze the player had just created, so broadcasts could be chained forever.
            var frozen = harness.Chronos!.Freeze("test-freeze");
            Assert(frozen.Succeeded, "the fake should freeze");
            harness.Advance(config.OverrideChargeRegenSeconds * 3f);
            Assert(harness.Controller.TestingUplinkCharges == spent,
                "uplink charge must not regenerate while the world is frozen");

            frozen.Value!.Dispose();
            harness.Advance(config.OverrideChargeRegenSeconds + 0.05f);
            Assert(harness.Controller.TestingUplinkCharges > spent,
                "uplink charge regenerates once the world is running again");
        }

        private static void AllyCrossfireIsNotReportedAsPlayerFire()
        {
            var config = FastConfig();
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "a crossfire target should spawn");
            var enemy = harness.Controller.TestingEnemies[0];

            // WasRecentlyShot has exactly one consumer: the ground truth handed to the brain. Attributing an
            // ally's crossfire to the human makes the robot argue against something the player did not do.
            enemy.ApplyDamage(1f, byPlayer: false);
            Assert(!enemy.WasRecentlyShot, "ally crossfire must not read as player fire");

            enemy.ApplyDamage(1f, byPlayer: true);
            Assert(enemy.WasRecentlyShot, "player fire still reads as player fire");
        }

        private static void FullyStabilizedAllyCheckIsFreeAtZeroCharge()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.LoyaltySeedMin = 1f;
            config.LoyaltySeedMax = 1f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f))
                && harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "a stable ally and a remaining hostile should spawn");
            var ally = harness.Controller.TestingEnemies[0];
            ally.Convert(config, 1f);
            harness.Controller.TestingSetUplinkCharges(0);
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);

            harness.Context.Input.SetValue("jack-in", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);

            Assert(ally.Loyalty == 1f
                && harness.Controller.TestingUplinkCharges == 0
                && LastToast(harness.Context).IndexOf("already fully stabilized", StringComparison.OrdinalIgnoreCase) >= 0,
                "checking a fully stabilized ally should acknowledge success even with an empty battery and remain free");
        }

        private static void LiveJackInCompositionFailureCleansUpAndFallsBack()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.UseLiveBrain = true;
            config.ConversationEnabled = true;
            config.SuggestibilityMin = 1f;
            config.SuggestibilityMax = 1f;
            config.LoyaltyMin = 0f;
            config.LoyaltyMax = 0f;
            config.CorruptionBase = 0f;
            config.CorruptionPerWave = 0f;
            config.BiasAmplitude = 0f;
            config.OverrideResistGrunt = 0f;
            config.OverrideDifficulty = 0.25f;
            using var harness = new Harness(config, withChronos: true);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f)),
                "a JACK IN composition-failure target should spawn");
            var enemy = harness.Controller.TestingEnemies[0];
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);
            harness.Context.Ui.FailNextContentUpdate(
                "zombies-jack-in",
                ModErrorCode.External,
                "synthetic JACK IN composition failure");
            var initialCharges = harness.Controller.TestingUplinkCharges;

            harness.Context.Input.SetValue("jack-in", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);

            Assert(enemy.IsAlly
                && harness.Controller.TestingUplinkCharges == initialCharges - 1,
                "failed live composition should consume one charge and take the deterministic conversion fallback");
            Assert(harness.Robots.Conversations.ActiveConversationCount == 0
                && harness.Context.LocalPlayer.ActiveControlLeaseCount == 0
                && harness.Chronos!.ActiveLeaseCount == 0
                && harness.Context.Ui.Surfaces.Count == 1,
                "failed live composition must release its conversation, control, time, and window ownership");
        }



        private static void LiveJackInUsesRobotKitConversationAndReleasesControl()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.UseLiveBrain = true;
            config.ConversationEnabled = true;
            config.ConversationMaxTurns = 1;
            config.ChargeShotEnabled = true;
            config.ConvSeedBias = 1f;
            config.ConvertThreshold = 0.1f;
            config.ConvertResistanceWeight = 0f;
            config.ConvertNudge = 1f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f)),
                "a live JACK IN target should spawn");
            var agent = (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0];
            AimAtProxy(harness, agent);
            harness.Robots.Conversations.EnqueueTurn("I remember who I was.", "CONVERT");

            harness.Context.Input.SetValue("zapper-fire", 1f);
            harness.Context.Input.SetValue("jack-in", 1f);
            var integrityBeforeOpen = harness.Controller.TestingIntegrity;
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);
            var channel = FindSurface(harness.Context, "zombies-jack-in");
            Assert(harness.Robots.Conversations.ActiveConversationCount == 1
                && harness.Context.LocalPlayer.ActiveControlLeaseCount == 1
                && harness.Controller.TestingIntegrity == integrityBeforeOpen
                && !harness.Controller.TestingCharging,
                "live JACK IN blocks the final horde tick and clears any charge whose release may be consumed by UI focus");
            Assert(channel.ChangeText("jack-in-text", "You can choose your own side.").Succeeded
                && channel.ActivateButton("jack-in-submit").Succeeded,
                "typed dialogue submits through declarative TopiaForgeUi controls");
            harness.Advance(0.01f);

            Assert(harness.Controller.TestingEnemies[0].IsAlly
                && harness.Robots.Conversations.ActiveConversationCount == 0
                && harness.Context.LocalPlayer.ActiveControlLeaseCount == 0,
                "a brain CONVERT decision still passes the engine disposition gate and releases every channel lease");
        }

        private static void DismissingLiveJackInRefusesWithoutDeterministicFallback()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.UseLiveBrain = true;
            config.ConversationEnabled = true;
            config.SuggestibilityMin = 1f;
            config.SuggestibilityMax = 1f;
            config.LoyaltyMin = 0f;
            config.LoyaltyMax = 0f;
            config.CorruptionBase = 0f;
            config.CorruptionPerWave = 0f;
            config.BiasAmplitude = 0f;
            config.OverrideResistGrunt = 0f;
            config.OverrideDifficulty = 1f;
            config.EnrageDispositionFloor = 0f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f)),
                "a deterministic-convert target should spawn for JACK IN dismissal");
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);

            harness.Context.Input.SetValue("jack-in", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);
            var agent = (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0];
            var channel = FindSurface(harness.Context, "zombies-jack-in");
            Assert(!agent.IsMoving && agent.MovementTarget == null,
                "the player-control fallback explicitly stops RobotKit chase intents while JACK IN is open");
            channel.Hide();
            harness.Advance(0.01f);

            Assert(harness.Controller.TestingEnemies[0].State == HijackState.Hostile
                && harness.Robots.Conversations.ActiveConversationCount == 0
                && harness.Context.LocalPlayer.ActiveControlLeaseCount == 0
                && agent.IsMoving && agent.MovementTarget != null,
                "window X or Escape dismissal refuses without deterministic fallback and resumes the stopped horde");
        }

        private static void LiveJackInDefaultsToTextAndSamplesVoiceWithUiFocus()
        {
            var config = FastConfig();
            config.OverrideEnabled = true;
            config.UseLiveBrain = true;
            config.ConversationEnabled = true;
            config.UseVoiceInput = true;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f)),
                "a voice-capable JACK IN target should spawn");
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);

            harness.Context.Input.SetValue("jack-in", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("jack-in", 0f);
            var channel = FindSurface(harness.Context, "zombies-jack-in");
            Assert(channel.ChangeText("jack-in-text", "Text must work first.").Succeeded,
                "a voice-capable JACK IN channel still opens in text mode");
            Assert(channel.ActivateButton("jack-in-input-mode").Succeeded,
                "voice mode is selected explicitly through the focus-safe UI control");

            harness.Context.Input.IsUiFocused = true;
            harness.Context.Input.SetValue("jack-in-voice", 1f);
            harness.Advance(0.01f);
            Assert(harness.Robots.DialogueInput.ActiveCaptureCount == 1,
                "push-to-talk remains sampleable while the JACK IN text window owns UI focus");

            channel.Hide();
            harness.Advance(0.01f);
            Assert(harness.Robots.DialogueInput.ActiveCaptureCount == 0
                && harness.Robots.Conversations.ActiveConversationCount == 0,
                "closing a focused voice channel releases both capture and conversation handles");
        }

        private static void PlayerEntityRebindsAfterNativeRecreation()
        {
            var config = FastConfig();
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "a hostile should spawn for player-identity rebinding");
            var oldPlayer = (FakeEntity)harness.Robots.Agents.PlayerEntity!;
            oldPlayer.Destroy();
            var replacement = new FakeEntity("replacement-player", "Player", new Vec3(9f, 0f, 0f));
            harness.Robots.Agents.PlayerEntity = replacement;

            harness.Advance(0.1f);
            var agent = (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0];
            Assert(agent.MovementTarget == replacement.Position,
                "a recreated native player entity is reacquired and every hostile chase is rebound");
        }

        private static void AllyRetargetCadenceAndCapAreEnforced()
        {
            var config = FastConfig();
            config.MaxConvertedAllies = 1;
            config.AllyRetargetSeconds = 0.5f;
            config.AllyDamage = 1f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 0f))
                && harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f))
                && harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 2f)),
                "an ally and two hostiles should spawn for retargeting");
            var ally = harness.Controller.TestingEnemies[0];
            var farther = harness.Controller.TestingEnemies[1];
            var nearer = harness.Controller.TestingEnemies[2];
            ally.Convert(config, 1f);

            harness.Advance(0.01f);
            Assert(ally.AllyTargetId == nearer.Agent.Id,
                "an ally initially targets the nearest hostile");
            ((FakeRobotAgent)nearer.Agent).AutoCompleteMovement = true;
            nearer.Agent.MoveTo(new Vec3(0f, 0f, 20f));
            ((FakeRobotAgent)nearer.Agent).AutoCompleteMovement = false;
            harness.Advance(0.1f);
            Assert(ally.AllyTargetId == nearer.Agent.Id,
                "an ally keeps its current target until the configured retarget cadence expires");
            harness.Advance(0.5f);
            Assert(ally.AllyTargetId == farther.Agent.Id,
                "an ally selects a closer hostile after the retarget cadence expires");

            harness.Controller.TestingApplyOverride(
                farther,
                new OverrideResolution(HijackOutcome.Convert, enraged: false));
            Assert(!farther.IsAlly && farther.State == HijackState.Frozen,
                "all conversion paths honor the maximum converted ally cap");
        }
    }
}
