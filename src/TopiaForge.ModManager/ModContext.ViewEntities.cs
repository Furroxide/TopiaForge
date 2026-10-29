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
        public object? UnwrapViewProviderEntity(IEntity entity, IInternalWorldViewRenderer renderer)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (Lifetime.IsStopping) return null;
            try
            {
                var provider = replicationServices.Views.ResolveProviderEntity(entity, renderer);
                return NativeViewProviderTarget.Resolve(this, provider);
            }
            catch (System.Exception) { return null; }
        }

        public object? UnwrapViewEntity(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            return !Lifetime.IsStopping && authoredMapInterop != null && authoredMapInterop.TryGetGameObject(entity, out var target) ? target : null;
        }
    }
}
