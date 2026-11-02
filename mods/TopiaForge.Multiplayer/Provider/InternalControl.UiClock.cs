using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer
{
    internal sealed partial class InternalControl : IInternalUiClock
    {
        public void BeginUiWorld() => engine.BeginUiWorld();
        public string UiClockEpoch { get { engine.RequireOwnerThread(); return engine.UiClockEpoch; } }
        public double UiClockTime { get { engine.RequireOwnerThread(); return engine.UiClockTime; } }
    }
}
