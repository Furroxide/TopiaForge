using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // Publishes IRobotObjectiveService: persistent robot programs (go-to / follow / patrol / idle) executed
    // frame-to-frame on top of IRobotAgent movement intents, plus the session-scoped named-target registry that both
    // the runners and LLM payloads draw from. Unity-free — it only touches the SDK contracts and ObjectiveRunner —
    // so the whole flow unit-tests on net8.0 with a fake agent. Ticked after the agent service (agents step first,
    // then objectives react to fresh reached/moving state). Never throws.
    internal sealed class RobotObjectiveService : IRobotObjectiveService, IDisposable
    {
        private readonly IModLogger logger;
        private readonly Func<float> now;
        private readonly Dictionary<string, ObjectiveRunner> runners = new Dictionary<string, ObjectiveRunner>();
        private readonly Dictionary<string, (RobotTargetInfo Info, Func<RobotTargetSnapshot?> Resolve)> targets =
            new Dictionary<string, (RobotTargetInfo, Func<RobotTargetSnapshot?>)>(StringComparer.OrdinalIgnoreCase);

        private float clock;
        private bool disposed;

        public RobotObjectiveService(IModLogger logger, Func<float>? now = null)
        {
            this.logger = logger;
            this.now = now ?? (() => clock);
        }

        public bool IsAvailable => !disposed;

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
                now);

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
            List<string>? dead = null;
            foreach (var pair in runners)
            {
                var runner = pair.Value;
                if (runner.IsCancelled || !runner.AgentAlive)
                {
                    (dead ??= new List<string>()).Add(pair.Key);
                    continue;
                }

                runner.Step();
            }

            if (dead != null)
            {
                foreach (var id in dead)
                {
                    runners.Remove(id);
                }
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
