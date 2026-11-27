using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private string uiClockEpoch = string.Empty;
        private MultiplayerSessionId uiClockSession;
        private ulong uiClockGeneration = ulong.MaxValue;
        private ulong uiWorldGeneration, seenUiWorldGeneration;
        private long? uiWorldOrigin;
        private bool uiClockAdopted;
        internal bool TryAdoptUiClock(double elapsedSeconds, long observedNowMicros)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host || uiClockAdopted || uiWorldGeneration != 0) return false;
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds < 0 || elapsedSeconds > long.MaxValue / 1_000_000d)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            _ = UiClockEpoch;
            // The provider may not have pumped since construction. Anchor at its current monotonic reading,
            // so the first pump does not count package-loading time for a second time.
            uiWorldOrigin = Math.Max(nowMicros, observedNowMicros) - (long)(elapsedSeconds * 1_000_000d);
            uiClockLast = elapsedSeconds;
            uiClockAdopted = true;
            return true;
        }
        internal ulong UiWorldGeneration => uiWorldGeneration;
        internal long UiTimelineMicros => (long)(UiClockTime * 1_000_000d);
        internal void BeginUiWorld()
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only the host starts a UI timeline.");
            uiWorldGeneration++;
            uiWorldOrigin = nowMicros;
        }
        private double uiClockSample, uiClockLast;
        private long uiClockSampleAt;
        private bool uiClockSampled;

        internal string UiClockEpoch
        {
            get
            {
                if (!uiClockSession.Equals(sessionId) || uiClockGeneration != epoch || seenUiWorldGeneration != uiWorldGeneration)
                {
                    uiClockSession = sessionId; uiClockGeneration = epoch; seenUiWorldGeneration = uiWorldGeneration;
                    uiClockEpoch = sessionId.Value + ":" + epoch.ToString(CultureInfo.InvariantCulture) + ":" + uiWorldGeneration.ToString(CultureInfo.InvariantCulture);
                    uiClockSampled = false; uiClockLast = 0;
                }
                return uiClockEpoch;
            }
        }

        internal double UiClockTime
        {
            get
            {
                _ = UiClockEpoch;
                var value = Role == EngineRole.Host ? Math.Max(0, nowMicros - (uiWorldOrigin ?? originMicros)) / 1_000_000d
                    : uiClockSampled ? uiClockSample + Math.Max(0, nowMicros - uiClockSampleAt) / 1_000_000d : 0;
                // A noisy later sample may slow convergence, but never moves an active deadline backwards.
                return uiClockLast = Math.Max(uiClockLast, value);
            }
        }

        private void ObserveUiClock(double hostSeconds, long roundTripMicros = 0)
        {
            _ = UiClockEpoch;
            uiClockSample = hostSeconds + Math.Max(0, roundTripMicros) / 2_000_000d;
            uiClockSampleAt = nowMicros;
            uiClockSampled = true;
        }
    }
}
