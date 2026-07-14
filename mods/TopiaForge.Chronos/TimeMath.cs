using System.Collections.Generic;

namespace TopiaForge.Chronos
{
    // The kinds of time effect a lease can express.
    internal enum LeaseKind
    {
        Freeze,        // forces world scale to 0 (any active freeze wins)
        Slow,          // multiplies the world scale by Scale (0..1)
        ExemptPlayer,  // keep the player full-speed while the world is slowed
        Driver         // a driver recomputes the base scale each tick
    }

    // One active time effect. Owner-tagged so a mod's leases can be released wholesale on teardown.
    internal sealed class LeaseRecord
    {
        public int Id;
        public LeaseKind Kind;
        public float Scale;
        public string Owner = string.Empty;
        public string Usage = string.Empty;
    }

    // Pure, Unity-free bookkeeping of the active leases and the DERIVED effective scale. Last-writer-wins is
    // structurally impossible: the scale is always derived from the whole set (any Freeze ⇒ 0, else the product of
    // Slow factors times the driver's base). Owner-scoped release makes leak-into-the-next-gamemode impossible.
    // Isolated here so the derivation unit-tests with no engine.
    internal sealed class LeaseLedger
    {
        private readonly List<LeaseRecord> leases = new List<LeaseRecord>();
        private int nextId = 1;

        public int Count => leases.Count;

        public bool HasActiveLeases => leases.Count > 0;

        public int Add(LeaseKind kind, string owner, string usage, float scale = 1f)
        {
            var id = nextId++;
            leases.Add(new LeaseRecord
            {
                Id = id,
                Kind = kind,
                Scale = scale,
                Owner = owner ?? string.Empty,
                Usage = usage ?? string.Empty,
            });
            return id;
        }

        public bool Remove(int id)
        {
            for (var index = 0; index < leases.Count; index++)
            {
                if (leases[index].Id == id)
                {
                    leases.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        // Release every lease owned by a mod (called on that mod's teardown). Returns how many were released.
        public int ReleaseOwner(string owner)
        {
            var removed = 0;
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                if (leases[index].Owner == owner)
                {
                    leases.RemoveAt(index);
                    removed++;
                }
            }

            return removed;
        }

        public void Clear()
        {
            leases.Clear();
        }

        public bool AnyFreeze
        {
            get
            {
                for (var index = 0; index < leases.Count; index++)
                {
                    if (leases[index].Kind == LeaseKind.Freeze)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public bool AnyExemptPlayer
        {
            get
            {
                for (var index = 0; index < leases.Count; index++)
                {
                    if (leases[index].Kind == LeaseKind.ExemptPlayer)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // The most-recently-added active driver lease id, or 0 when none (the latest driver wins).
        public int ActiveDriverId
        {
            get
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                {
                    if (leases[index].Kind == LeaseKind.Driver)
                    {
                        return leases[index].Id;
                    }
                }

                return 0;
            }
        }

        // The product of all active Slow factors (1 when there are none), each clamped to [0,1].
        public float SlowProduct
        {
            get
            {
                var product = 1f;
                for (var index = 0; index < leases.Count; index++)
                {
                    if (leases[index].Kind == LeaseKind.Slow)
                    {
                        product *= TimeMath.Clamp01(leases[index].Scale);
                    }
                }

                return product;
            }
        }

        // The effective world scale: any Freeze ⇒ 0; else the driver's base (or 1) times the Slow product, clamped.
        public float EffectiveScale(float driverBaseScale)
        {
            if (AnyFreeze)
            {
                return 0f;
            }

            return TimeMath.Clamp01(driverBaseScale * SlowProduct);
        }
    }

    internal static class TimeMath
    {
        // Co-scale the physics timestep with the world scale so native FixedUpdate-driven motion slows SMOOTHLY in
        // slow-mo (and stays real-time-stable) instead of stepping coarsely. Always derived from the captured
        // baseline — never the live value — so repeated gamemode loads can't drift the timestep. At/near 0 the
        // timestep is irrelevant (FixedUpdate halts), so a floor keeps it sane; at >=1 it is exactly the baseline.
        public static float FixedDelta(float baseFixedDelta, float scale, float floor)
        {
            // At/above 1 it's the baseline; at/below 0 the world is frozen (FixedUpdate halts) so leave the baseline
            // untouched — only co-scale in the (0,1) slow-mo band, clamped to a floor so the step never goes tiny.
            if (scale >= 1f || scale <= 0f)
            {
                return baseFixedDelta;
            }

            var s = scale < floor ? floor : scale;
            return baseFixedDelta * s;
        }

        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
