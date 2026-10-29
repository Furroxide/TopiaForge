using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalViewEntityContext
    {
        public bool IsCurrentViewAvatar(IEntity entity, IInternalWorldViewRenderer renderer)
        {
            UnityMainThreadGuard.AssertCurrent();
            return !Lifetime.IsStopping && replicationServices.Views.IsCurrentAvatar(entity, renderer);
        }
        public object? UnwrapViewEntity(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            return !Lifetime.IsStopping && authoredMapInterop != null && authoredMapInterop.TryGetGameObject(entity, out var target) ? target : null;
        }
    }
}
