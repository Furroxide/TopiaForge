using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication
    {
        public IViewControls WorldView(IInternalWorldViewRenderer renderer)
        {
            UnityMainThreadGuard.AssertCurrent();
            return services.Views.For(renderer).Bind(lifetime, () => ExecutionContext == InternalExecutionContext.Participant
                ? ModErrorCode.NotAuthoritative : Realm == InternalNetRealm.Offline ? ModErrorCode.None : ModErrorCode.Unavailable,
                () => UnityMainThreadGuard.AssertCurrent());
        }
    }
}
