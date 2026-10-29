using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal static class NativeViewProviderTarget
    {
        internal static object? Resolve(IInternalNativeEntityReadContext context, IEntity? provider)
        {
            try
            {
                return provider?.IsAlive == true && provider is IInternalOwnedNativeEntity owned
                    && owned.NativeReadScope?.IsCurrentWorld(context) == true ? owned.NativeObject : null;
            }
            catch (System.Exception) { return null; }
        }
    }
}
