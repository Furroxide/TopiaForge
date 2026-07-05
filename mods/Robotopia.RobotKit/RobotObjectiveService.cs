using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // Publishes IRobotObjectiveService: persistent robot programs (go-to / follow / patrol / wander / flee /
    // reprogram-courier / idle) executed frame-to-frame on top of IRobotAgent movement intents, plus the
    // session-scoped named-target registry that both the runners and LLM payloads draw from. Unity-free — it only
    // touches the SDK contracts and ObjectiveRunner — so the whole flow unit-tests on net8.0 with a fake agent.
    // Ticked after the agent service (agents step first, then objectives react to fresh reached/moving state).
    // Never throws.
    internal sealed class RobotObjectiveService : IRobotObjectiveService, IDisposable
    {
        private readonly IModLogger logger;
        private readonly Func<float> now;
        private readonly Func<float> random01;
        private readonly Func<object, IRobotAgent?>? resolveAgent; // live object -> agent, for Reprogram couriers
        private readonly Dictionary<string, ObjectiveRunner> runners = new Dictionary<string, ObjectiveRunner>();
        private readonly List<ObjectiveRunner> stepBuffer = new List<ObjectiveRunner>();
        private readonly Dictionary<string, (RobotTargetInfo Info, Func<RobotTargetSnapshot?> Resolve)> targets =
            new Dictionary<string, (RobotTargetInfo, Func<RobotTargetSnapshot?>)>(StringComparer.OrdinalIgnoreCase);

        private float clock;
        private bool disposed;

        public RobotObjectiveService(
            IModLogger logger,
            Func<float>? now = null,
            Func<object, IRobotAgent?>? resolveAgent = null,
            Func<float>? random01 = null)
        {
            this.logger = logger;
            this.now = now ?? (() => clock);
            this.resolveAgent = resolveAgent;
            if (random01 != null)
            {
                this.random01 = random01;
            }
            else
            {
                var rng = new Random();
                this.random01 = () => (float)rng.NextDouble();
            }
        }

        public bool IsAvailable => !disposed;

        public event Action<RobotProgramDelivery>? ProgramDelivered;

        public IReadOnlyList<string> TargetNames
        {
            get
            {
                var names = new List<string>(targets.Count);
                foreach (var name in targets.Keys)
                {
                    names.Add(name);
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        public IReadOnlyList<RobotTargetInfo> Targets
        {
            get
            {
                var infos = new List<RobotTargetInfo>(targets.Count);
                foreach (var pair in targets)
                {
                    infos.Add(pair.Value.Info);
                }

                infos.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                return infos;
            }
        }

        public bool TryGetTargetInfo(string name, out RobotTargetInfo info)
        {
            info = null!;
            if (string.IsNullOrWhiteSpace(name) || !targets.TryGetValue(Normalize(name), out var entry))
            {
                return false;
            }

            info = entry.Info;
            return true;
        }

        public void RegisterTarget(string name, Func<RobotTargetSnapshot?> resolve)
        {
            RegisterTarget(name, RobotTargetKind.Custom, resolve);
        }

        public void RegisterTarget(string name, RobotTargetKind kind, Func<RobotTargetSnapshot?> resolve)
        {
            if (disposed || string.IsNullOrWhiteSpace(name) || resolve == null)
            {
                return;
            }

            var normalized = Normalize(name);
            targets[normalized] = (new RobotTargetInfo(normalized, kind), resolve);
        }

        public void UnregisterTarget(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            targets.Remove(Normalize(name));
        }

        public bool TryResolveTarget(string name, out RobotTargetSnapshot snapshot)
        {
            snapshot = default;
            if (string.IsNullOrWhiteSpace(name) || !targets.TryGetValue(Normalize(name), out var entry))
            {
                return false;
            }

            RobotTargetSnapshot? resolved;
            try
            {
                resolved = entry.Resolve();
            }
            catch (Exception exception)
            {
                logger.Warn("Objective target '" + name + "' resolver threw: " + exception.Message);
                return false;
            }

            if (resolved == null)
            {
                return false;
            }

            snapshot = resolved.Value;
            return true;
        }

        public IRobotObjectiveHandle SetObjective(IRobotAgent agent, RobotObjective objective)
        {
            var program = objective ?? RobotObjective.Idle();
            var runner = new ObjectiveRunner(
                agent,
                program,
                name => TryResolveTarget(name, out var snapshot) ? snapshot : (RobotTargetSnapshot?)null,
                now,
                random01,
                resolveAgent,
                (recipient, payload) => DeliverProgram(agent, recipient, payload));

            if (agent == null)
            {
                runner.Cancel();
                return runner;
            }

            if (runners.TryGetValue(agent.Id, out var previous))
            {
                previous.Cancel();
            }

            if (disposed || !agent.IsAlive)
            {
                runner.Cancel();
                return runner;
            }

            runners[agent.Id] = runner;
            return runner;
        }

        public IRobotObjectiveHandle? GetObjective(IRobotAgent agent)
        {
            if (agent == null || !runners.TryGetValue(agent.Id, out var runner))
            {
                return null;
            }

            return runner;
        }

        public void ClearObjective(IRobotAgent agent)
        {
            if (agent == null || !runners.TryGetValue(agent.Id, out var runner))
            {
                return;
            }

            runner.Cancel();
            runners.Remove(agent.Id);
        }

        // Advance every live objective and drop those whose robot is gone or that were cancelled elsewhere. Call
        // after the agent service's Tick so runners see this frame's reached/moving state.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            clock += deltaTime;

            // Step from a copied buffer: a Reprogram delivery calls SetObjective(recipient, payload) mid-step,
            // which mutates the runner map. A runner replaced mid-tick may still sit in the buffer — harmless,
            // cancelled runners return from Step immediately.
            stepBuffer.Clear();
            foreach (var runner in runners.Values)
            {
                stepBuffer.Add(runner);
            }

            foreach (var runner in stepBuffer)
            {
                if (!runner.IsCancelled && runner.AgentAlive)
                {
                    runner.Step();
                }
            }

            // Prune in a second pass over the live map (cancelled/replaced runners and dead robots).
            List<string>? dead = null;
            foreach (var pair in runners)
            {
                if (pair.Value.IsCancelled || !pair.Value.AgentAlive)
                {
                    (dead ??= new List<string>()).Add(pair.Key);
                }
            }

            if (dead != null)
            {
                foreach (var id in dead)
                {
                    runners.Remove(id);
                }
            }
        }

        // Applies a courier's payload to the recipient and announces the hand-over. Runs inside the courier
        // runner's Step (via its delivery callback) — hence Tick stepping from a buffer, not the live map.
        private void DeliverProgram(IRobotAgent sender, IRobotAgent recipient, RobotObjective payload)
        {
            SetObjective(recipient, payload);
            var handler = ProgramDelivered;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(new RobotProgramDelivery(sender, recipient, payload));
            }
            catch (Exception exception)
            {
                logger.Warn("A ProgramDelivered subscriber threw: " + exception.Message);
            }
        }

        public void OnSceneChanged()
        {
            // The robots and the things targets pointed at are gone; objectives are session-only by design.
            foreach (var runner in runners.Values)
            {
                runner.Cancel();
            }

            runners.Clear();
            targets.Clear();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var runner in runners.Values)
            {
                runner.Cancel();
            }

            runners.Clear();
            targets.Clear();
        }

        private static string Normalize(string name)
        {
            return name.Trim().ToUpperInvariant();
        }
    }
}
