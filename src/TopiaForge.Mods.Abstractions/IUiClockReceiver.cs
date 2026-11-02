using System;

namespace TopiaForge.Mods
{
    /// <summary>Connects a UI host to the runtime timeline without exposing synchronization plumbing to authors.</summary>
    internal interface IUiClockReceiver
    {
        void FollowClock(Func<double> clock, Func<string> epoch);
    }
}
