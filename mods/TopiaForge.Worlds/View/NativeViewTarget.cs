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
            if (!entity.IsAlive || !renderer.IsAlive || !(context is IInternalViewEntityContext bridge)) return null;
            if (bridge.UnwrapViewEntity(entity) is T target
                && (inWorld(target) || bridge.IsCurrentViewAvatar(entity, renderer))) return target;
            return bridge.UnwrapViewProviderEntity(entity, renderer) as T;
        }
    }
}
