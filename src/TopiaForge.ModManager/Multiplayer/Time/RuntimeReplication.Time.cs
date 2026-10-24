using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication
    {
        private static readonly OperationResult<bool> PublishStopping = OperationResult<bool>.Failure(ModErrorCode.Cancelled, Stopping);

        public IInternalGameClock? GameClock => services.Runtime.GameClock;

        public InternalSessionTime SessionTime => services.Runtime.SessionTime.Current;

        public OperationResult<bool> PublishSessionTime(InternalSessionTime time)
        {
            UnityMainThreadGuard.AssertCurrent();
            return lifetime.IsStopping ? PublishStopping : services.Runtime.SessionTime.Publish(time);
        }
    }
}
