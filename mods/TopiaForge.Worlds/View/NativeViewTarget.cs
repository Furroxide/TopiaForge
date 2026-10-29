using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    // The native renderer and its offline regression exercise the same admission boundary.
    internal static class NativeViewTarget
    {
        internal static T? Resolve<T>(IModContext context, IInternalWorldViewRenderer renderer, IEntity entity,
            System.Func<T, bool> inWorld) where T : class
        {
            if (!entity.IsAlive || !renderer.IsAlive || !(context is IInternalViewEntityContext bridge)
                || !(bridge.UnwrapViewEntity(entity) is T target)) return null;
            return inWorld(target) || bridge.IsCurrentViewAvatar(entity, renderer) ? target : null;
        }
    }
}
