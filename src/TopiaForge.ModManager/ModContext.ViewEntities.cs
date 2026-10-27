using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalViewEntityContext
    {
        public object? UnwrapViewEntity(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            return !Lifetime.IsStopping && authoredMapInterop != null && authoredMapInterop.TryGetGameObject(entity, out var target) ? target : null;
        }
    }
}
