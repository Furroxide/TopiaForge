using System;

namespace Robotopia.UgcLiveSync
{
    /// <summary>Small Unity-free cadence gate so the command channel never stats the filesystem every frame.</summary>
    internal sealed class UgcCommandPollGate
    {
        private readonly float intervalSeconds;
        private float remainingSeconds;

        public UgcCommandPollGate(float intervalSeconds)
        {
            if (intervalSeconds <= 0f || float.IsNaN(intervalSeconds) || float.IsInfinity(intervalSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            }

            this.intervalSeconds = intervalSeconds;
        }

        /// <summary>Returns true immediately on first use, then at most once per configured interval.</summary>
        public bool Tick(float unscaledDeltaTime)
        {
            if (remainingSeconds <= 0f)
            {
                remainingSeconds = intervalSeconds;
                return true;
            }

            remainingSeconds -= Math.Max(0f, unscaledDeltaTime);
            return false;
        }

        public void Reset()
        {
            remainingSeconds = 0f;
        }
    }
}
