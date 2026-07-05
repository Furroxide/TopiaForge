using System;
using System.Collections.Generic;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Sandbox
{
    /// <summary>
    /// Robots reacting to each other: proximity greetings between idle robots (emotes + a toast — free), an
    /// opt-in LLM banter upgrade (one brain token per exchange, config-gated and globally cooled down), and the
    /// courier-delivery reaction (<see cref="IRobotObjectiveService.ProgramDelivered"/> → toast + emotes on both
    /// robots). Scans on a slow tick over the spawn registry; owns no objects of its own — disposal just unhooks
    /// the delivery event and abandons any in-flight banter query.
    /// </summary>
    internal sealed class RobotAmbience : IDisposable
    {
        private const float ScanIntervalSeconds = 0.5f;
        private const float GreetDistanceMeters = 4f;
        private const float PairCooldownSeconds = 60f;
        private const float EmoteClearSeconds = 4f;
        private const float BanterSecondLineDelay = 1.5f;
        private const int MaxBanterLineChars = 90;
        private const int MaxTrackedPairs = 64;

        private readonly SandboxConfig config;
        private readonly UiHost ui;
        private readonly SpawnRegistry registry;
        private readonly IRobotObjectiveService objectives;
        private readonly IRobotBrainQueryService? brains;
        private readonly Func<bool> chatBusy;
        private readonly IModLogger logger;

        private readonly List<SpawnRegistry.SpawnedEntry> scanBuffer = new List<SpawnRegistry.SpawnedEntry>();
        private readonly Dictionary<string, float> pairCooldowns = new Dictionary<string, float>();
        private readonly List<(IRobotAgent Robot, float ClearAt)> emoteClears = new List<(IRobotAgent, float)>();
        private readonly List<string> expiredPairs = new List<string>();

        private float nextScanAt;
        private float banterAllowedAt;
        private IRobotBrainQuery? banterQuery;
        private string banterNameA = string.Empty;
        private string banterNameB = string.Empty;
        private string? queuedToast;
        private float queuedToastAt;
        private bool disposed;

        public RobotAmbience(
            SandboxConfig config,
            UiHost ui,
            SpawnRegistry registry,
            IRobotObjectiveService objectives,
            IRobotBrainQueryService? brains,
            Func<bool> chatBusy,
            IModLogger logger)
        {
            this.config = config;
            this.ui = ui;
            this.registry = registry;
            this.objectives = objectives;
            this.brains = brains;
            this.chatBusy = chatBusy;
            this.logger = logger;
            objectives.ProgramDelivered += OnProgramDelivered;
        }

        public void Update()
        {
            if (disposed)
            {
                return;
            }

            var now = Time.unscaledTime;

            // These drains run unconditionally so in-flight work still lands if the config flips mid-session.
            DrainEmoteClears(now);
            DrainBanter(now);
            DrainQueuedToast(now);

            if (!config.AmbientGreetings || chatBusy())
            {
                return;
            }

            if (now < nextScanAt)
            {
                return;
            }

            nextScanAt = now + ScanIntervalSeconds;
            Scan(now);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            objectives.ProgramDelivered -= OnProgramDelivered;
            banterQuery = null; // abandoned; the query service times it out on its own
            emoteClears.Clear();
            queuedToast = null;
        }

        // O(n²) pair scan over live robots — fine at sandbox scale (the spawn cap bounds n, and the scan runs
        // twice a second). At most one greeting fires per scan so the toast stream stays calm.
        private void Scan(float now)
        {
            registry.CollectRobots(scanBuffer);
            if (scanBuffer.Count < 2)
            {
                return;
            }

            for (var first = 0; first < scanBuffer.Count - 1; first++)
            {
                var a = scanBuffer[first].Robot;
                if (a == null || !Eligible(a))
                {
                    continue;
                }

                for (var second = first + 1; second < scanBuffer.Count; second++)
                {
                    var b = scanBuffer[second].Robot;
                    if (b == null || !Eligible(b))
                    {
                        continue;
                    }

                    if (PlanarDistance(a.Position, b.Position) > GreetDistanceMeters)
                    {
                        continue;
                    }

                    var key = PairKey(a, b);
                    if (pairCooldowns.TryGetValue(key, out var readyAt) && now < readyAt)
                    {
                        continue;
                    }

                    pairCooldowns[key] = now + PairCooldownSeconds;
                    PrunePairCooldowns(now);
                    Greet(scanBuffer[first], scanBuffer[second], now);
                    return;
                }
            }
        }

        // Only dormant, settled robots mingle: autonomous ones have their own social life, and a robot
        // mid-journey (couriering, seeking, fleeing) has somewhere to be.
        private bool Eligible(IRobotAgent robot)
        {
            if (!robot.IsAlive || robot.BrainMode != RobotBrainMode.Dormant)
            {
                return false;
            }

            var handle = objectives.GetObjective(robot);
            if (handle == null)
            {
                return true;
            }

            switch (handle.State)
            {
                case RobotObjectiveState.Idle:
                case RobotObjectiveState.Arrived:
                case RobotObjectiveState.Dwelling:
                case RobotObjectiveState.Delivered:
                    return true;
                default:
                    return false;
            }
        }

        private void Greet(SpawnRegistry.SpawnedEntry a, SpawnRegistry.SpawnedEntry b, float now)
        {
            Emote(a.Robot!, ":smile:", now);
            Emote(b.Robot!, ":wave:", now);

            if (config.AmbientBanter && brains != null && brains.IsAvailable
                && banterQuery == null && now >= banterAllowedAt)
            {
                BeginBanter(a, b, now);
                return;
            }

            ui.Toast(a.DisplayName + " beeps at " + b.DisplayName + ".", QwTone.Neutral);
        }

        // One structured brain query produces both lines of the exchange. The global cooldown is stamped up
        // front so failed/quiet attempts rate-limit exactly like successful ones — each attempt spends a token.
        private void BeginBanter(SpawnRegistry.SpawnedEntry a, SpawnRegistry.SpawnedEntry b, float now)
        {
            banterAllowedAt = now + Math.Max(30f, config.BanterCooldownSeconds);
            banterNameA = a.DisplayName;
            banterNameB = b.DisplayName;

            var prompt =
                "Two service robots in a creator sandbox pass each other and exchange a quick word. " +
                banterNameA + " is currently: " + CurrentProgram(a.Robot!) + ". " +
                banterNameB + " is currently: " + CurrentProgram(b.Robot!) + ". " +
                "Write one playful in-character spoken line for each — G-rated, max 10 words each, and never " +
                "mention being an AI, a model, code, or a game.";

            banterQuery = brains!.BeginQuery(new BrainQueryRequest(prompt, new[]
            {
                new BrainOutputField("line_a", "What " + banterNameA + " says to " + banterNameB + ". Max 10 words."),
                new BrainOutputField("line_b", banterNameB + "'s reply. Max 10 words."),
            })
            {
                Usage = "sandbox-banter",
                Temperature = 0.8f,
            });
            logger.Debug("Sandbox banter query started (" + banterNameA + " × " + banterNameB + ").");
        }

        private void DrainBanter(float now)
        {
            if (banterQuery == null || !banterQuery.IsComplete)
            {
                return;
            }

            var result = banterQuery.Result;
            banterQuery = null;
            if (!result.Succeeded)
            {
                return; // the emotes already played; a failed exchange just stays quiet
            }

            result.Values.TryGetValue("line_a", out var lineA);
            result.Values.TryGetValue("line_b", out var lineB);
            if (string.IsNullOrWhiteSpace(lineA))
            {
                return;
            }

            ui.Toast(banterNameA + ": " + Clamp(lineA!), QwTone.Neutral);
            if (!string.IsNullOrWhiteSpace(lineB))
            {
                queuedToast = banterNameB + ": " + Clamp(lineB!);
                queuedToastAt = now + BanterSecondLineDelay;
            }
        }

        // A courier finished its run — narrate the hand-over. Deliberately NOT gated by AmbientGreetings: this
        // is feedback for a player-initiated program, not ambience.
        private void OnProgramDelivered(RobotProgramDelivery delivery)
        {
            if (disposed)
            {
                return;
            }

            var now = Time.unscaledTime;
            var sender = registry.FindRobot(delivery.Sender)?.DisplayName ?? "A robot";
            var recipient = registry.FindRobot(delivery.Recipient)?.DisplayName ?? "another robot";
            ui.Toast(sender + " reprogrammed " + recipient + ": " + delivery.Payload.Describe(), QwTone.Success);
            Emote(delivery.Sender, ":thumbsup:", now);
            Emote(delivery.Recipient, ":thinking_face:", now);
        }

        private void Emote(IRobotAgent robot, string shortcode, float now)
        {
            if (!robot.IsAlive)
            {
                return;
            }

            robot.SetEmote(shortcode);
            emoteClears.Add((robot, now + EmoteClearSeconds));
        }

        private void DrainEmoteClears(float now)
        {
            for (var index = emoteClears.Count - 1; index >= 0; index--)
            {
                if (now < emoteClears[index].ClearAt)
                {
                    continue;
                }

                var robot = emoteClears[index].Robot;
                emoteClears.RemoveAt(index);
                if (robot.IsAlive)
                {
                    robot.SetEmote(string.Empty); // best-effort reset, like the chat's close
                }
            }
        }

        private void DrainQueuedToast(float now)
        {
            if (queuedToast == null || now < queuedToastAt)
            {
                return;
            }

            ui.Toast(queuedToast, QwTone.Neutral);
            queuedToast = null;
        }

        private string CurrentProgram(IRobotAgent robot)
        {
            var handle = objectives.GetObjective(robot);
            return handle == null ? "waiting for a task" : handle.Objective.Describe();
        }

        private static string Clamp(string line)
        {
            var trimmed = line.Trim();
            return trimmed.Length <= MaxBanterLineChars
                ? trimmed
                : trimmed.Substring(0, MaxBanterLineChars) + "…";
        }

        private static string PairKey(IRobotAgent a, IRobotAgent b)
        {
            // Order-independent key from the two agent ids, so (A,B) and (B,A) share one cooldown.
            return string.CompareOrdinal(a.Id, b.Id) <= 0 ? a.Id + "|" + b.Id : b.Id + "|" + a.Id;
        }

        // The cooldown map only grows while distinct pairs keep meeting; drop expired entries once it is
        // bigger than any plausible working set so a long session cannot accumulate garbage.
        private void PrunePairCooldowns(float now)
        {
            if (pairCooldowns.Count <= MaxTrackedPairs)
            {
                return;
            }

            expiredPairs.Clear();
            foreach (var pair in pairCooldowns)
            {
                if (now >= pair.Value)
                {
                    expiredPairs.Add(pair.Key);
                }
            }

            foreach (var key in expiredPairs)
            {
                pairCooldowns.Remove(key);
            }
        }

        private static float PlanarDistance(Vec3 a, Vec3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
