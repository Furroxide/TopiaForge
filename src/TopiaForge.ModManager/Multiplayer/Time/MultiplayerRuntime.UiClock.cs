using System;
using System.Diagnostics;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        private readonly Stopwatch uiClock = Stopwatch.StartNew();
        private string offlineUiEpoch = Guid.NewGuid().ToString("N");
        private bool offlineUiObserved, uiClockProviderSeen;

        private void AttachUiClock(IInternalMultiplayerControl candidate)
        {
            if (!(candidate is IInternalUiClock source)) return;
            if (!uiClockProviderSeen && offlineUiObserved && candidate.Role == InternalSessionRole.Solo &&
                source.TryAdoptUiClock(uiClock.Elapsed.TotalSeconds))
                UiClockBinding.AdoptInitialEpoch(offlineUiEpoch, source.UiClockEpoch);
            uiClockProviderSeen = true;
        }

        private void ResetOfflineUiClock()
        {
            offlineUiEpoch = Guid.NewGuid().ToString("N");
            uiClock.Restart();
            offlineUiObserved = false;
        }

        internal void BeginUiWorld()
        {
            // Listen hosts replace their authenticated session; joined peers follow its clock identity.
            if (role != InternalSessionRole.Solo) return;
            if (control is IInternalUiClock source) source.BeginUiWorld();
            else ResetOfflineUiClock();
        }

        internal string UiClockEpoch
        {
            get
            {
                if (control is IInternalUiClock source) return source.UiClockEpoch;
                offlineUiObserved = true;
                return offlineUiEpoch;
            }
        }

        internal double UiClockTime
        {
            get
            {
                if (control is IInternalUiClock source) return source.UiClockTime;
                offlineUiObserved = true;
                return uiClock.Elapsed.TotalSeconds;
            }
        }
    }
}
