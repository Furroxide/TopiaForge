using System;
using System.Diagnostics;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        private readonly Stopwatch uiClock = Stopwatch.StartNew();
        private string offlineUiEpoch = Guid.NewGuid().ToString("N");
        internal void BeginUiWorld()
        {
            // Listen hosts replace their authenticated session; joined peers follow its clock identity.
            if (role != InternalSessionRole.Solo) return;
            if (control is IInternalUiClock source) source.BeginUiWorld();
            else { offlineUiEpoch = Guid.NewGuid().ToString("N"); uiClock.Restart(); }
        }
        internal string UiClockEpoch => (control as IInternalUiClock)?.UiClockEpoch ?? offlineUiEpoch;
        internal double UiClockTime => (control as IInternalUiClock)?.UiClockTime ?? uiClock.Elapsed.TotalSeconds;
    }
}
