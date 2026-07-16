using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Overflow-safe, engine-free rules shared by the Zombies runtime and its tests.</summary>
    internal static class ZombiesRuntimeMath
    {
        public static int WaveSize(int baseCount, int increment, int wave)
        {
            if (wave <= 0)
            {
                return 0;
            }

            var total = (long)Math.Max(0, baseCount)
                + ((long)Math.Max(0, increment) * (wave - 1L));
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public static int ComboMultiplier(int combo, int killsPerTier, int maximum)
        {
            if (combo <= 0 || killsPerTier <= 0)
            {
                return 1;
            }

            var multiplier = 1L + ((long)combo / killsPerTier);
            return (int)Math.Min(Math.Max(1, maximum), multiplier);
        }

        public static int SaturatingAdd(int left, int right)
        {
            var sum = (long)left + right;
            if (sum > int.MaxValue)
            {
                return int.MaxValue;
            }

            return sum < int.MinValue ? int.MinValue : (int)sum;
        }

        public static int SaturatingMultiply(int left, int right)
        {
            var product = (long)left * right;
            if (product > int.MaxValue)
            {
                return int.MaxValue;
            }

            return product < int.MinValue ? int.MinValue : (int)product;
        }

        public static int ScoreCredits(int awardedScore, float creditsPerScore)
        {
            if (awardedScore <= 0 || creditsPerScore <= 0f
                || float.IsNaN(creditsPerScore))
            {
                return 0;
            }

            var credits = awardedScore * (double)creditsPerScore;
            return credits >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Round(credits, MidpointRounding.AwayFromZero);
        }

        public static bool IsHeadshot(IRobotAgent agent, ZombieArchetype archetype, Vec3 hitPoint)
        {
            var baseY = agent.Position.Y;
            var height = agent.HeadPosition.Y - baseY;
            if (height <= 0.001f || float.IsNaN(height) || float.IsInfinity(height))
            {
                return false;
            }

            var fraction = (hitPoint.Y - baseY) / height;
            return fraction >= archetype.HeadFraction;
        }

        public static bool IsNearRay(Ray ray, Vec3 point, float maximumDistance, float radius)
        {
            var offset = point - ray.Origin;
            var along = Vec3.Dot(offset, ray.Direction);
            if (along < 0f || along > maximumDistance)
            {
                return false;
            }

            var closest = ray.Origin + (ray.Direction * along);
            return (point - closest).LengthSquared <= radius * radius;
        }
    }
}
