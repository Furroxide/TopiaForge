using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.Sandbox
{
    /// <summary>Live target facts and displayed-program resolution for <see cref="RobotChat"/>.</summary>
    internal sealed partial class RobotChat
    {
        // Per-turn "who/what/where/doing-what" lines for every offered target, from the robot's current position —
        // the ground truth that lets it follow another robot, answer "where is X?", or reason about what the rest
        // of the fleet is up to instead of guessing at names.
        private IReadOnlyList<string> DescribeOfferedTargets()
        {
            var described = new List<string>(offeredTargets.Count);
            var observer = agent;
            if (observer == null || !observer.IsAlive)
            {
                return described;
            }

            var from = observer.Position;
            foreach (var name in offeredTargets)
            {
                if (!objectives.TryGetTargetInfo(name, out var info))
                {
                    continue;
                }

                var snapshot = objectives.TryResolveTarget(name, out var resolved)
                    ? resolved
                    : (RobotTargetSnapshot?)null;

                // Robot targets also carry their current activity ("currently: FOLLOW PLAYER (moving)").
                string? activity = null;
                if (info.Kind == RobotTargetKind.Robot)
                {
                    var other = findRobotByTargetName?.Invoke(name);
                    if (other != null && other.IsAlive)
                    {
                        activity = RobotProgramDirector.DescribeActivity(other.BrainMode, objectives.GetObjective(other));
                    }
                }

                described.Add(name + ": " + RobotTargetFacts.Describe(info, snapshot, from, activity));
            }

            return described;
        }

        private RobotObjective? ResolveDisplayedProgram()
        {
            return acceptedProgram
                ?? (agent != null ? objectives.GetObjective(agent)?.Objective : null)
                ?? previousProgram;
        }
    }
}
