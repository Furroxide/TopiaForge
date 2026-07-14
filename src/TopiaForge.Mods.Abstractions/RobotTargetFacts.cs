using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Pure helpers that turn a registered target into a short "who/what/where" line an LLM brain can ground on —
    /// e.g. <c>"another robot, 8 m north-east of you"</c> or <c>"a prop, currently missing"</c>. Use them to build
    /// per-turn ground-truth facts (<see cref="RobotConversationRequest.LiveFacts"/>) so a robot can find any
    /// registered entity instead of guessing from a bare name. Unity-free and allocation-light.
    /// </summary>
    public static class RobotTargetFacts
    {
        /// <summary>
        /// Describes one target relative to an observer: its kind phrase plus direction and distance, or
        /// "currently missing" when the target does not resolve right now.
        /// </summary>
        public static string Describe(RobotTargetInfo info, RobotTargetSnapshot? snapshot, Vec3 observerPosition)
        {
            if (info == null)
            {
                return string.Empty;
            }

            var what = KindPhrase(info);
            return snapshot == null
                ? what + ", currently missing"
                : what + ", " + DirectionAndDistance(observerPosition, snapshot.Value.Position);
        }

        /// <summary>
        /// Same as <see cref="Describe(RobotTargetInfo, RobotTargetSnapshot?, Vec3)"/> with an optional activity
        /// suffix — what the target is doing right now (e.g. <c>"currently: FOLLOW PLAYER (moving)"</c>) — so a
        /// robot can reason about the rest of the fleet, not just find it. Blank activity yields the plain line.
        /// </summary>
        public static string Describe(RobotTargetInfo info, RobotTargetSnapshot? snapshot, Vec3 observerPosition, string? activity)
        {
            var described = Describe(info, snapshot, observerPosition);
            return described.Length == 0 || string.IsNullOrWhiteSpace(activity)
                ? described
                : described + "; " + activity;
        }

        /// <summary>
        /// A compact "8 m north-east of you" phrase (8-way world compass, north = +Z, east = +X, whole metres).
        /// Targets closer than a metre are "right next to you".
        /// </summary>
        public static string DirectionAndDistance(Vec3 from, Vec3 to)
        {
            var dx = to.X - from.X;
            var dz = to.Z - from.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance < 1.0)
            {
                return "right next to you";
            }

            return Math.Round(distance) + " m " + Compass(dx, dz) + " of you";
        }

        private static string KindPhrase(RobotTargetInfo info)
        {
            switch (info.Kind)
            {
                case RobotTargetKind.Player:
                    return "the human operator";
                case RobotTargetKind.Robot:
                    return "another robot";
                case RobotTargetKind.Prop:
                    return "a prop";
                case RobotTargetKind.Marker:
                    return "a marker pad";
                default:
                    return string.IsNullOrWhiteSpace(info.Description) ? "a target" : info.Description!;
            }
        }

        private static string Compass(float dx, float dz)
        {
            // 8 sectors of 45°, centred on the cardinals (north = +Z, east = +X).
            var degrees = Math.Atan2(dx, dz) * (180.0 / Math.PI);
            if (degrees < 0)
            {
                degrees += 360.0;
            }

            var sector = (int)Math.Round(degrees / 45.0) % 8;
            switch (sector)
            {
                case 0: return "north";
                case 1: return "north-east";
                case 2: return "east";
                case 3: return "south-east";
                case 4: return "south";
                case 5: return "south-west";
                case 6: return "west";
                default: return "north-west";
            }
        }
    }
}
