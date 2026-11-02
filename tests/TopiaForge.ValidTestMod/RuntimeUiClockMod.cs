using System;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    public sealed class RuntimeUiClockMod : TopiaForgeMod
    {
        public static Action<IModContext>? OnLoadHook;
        protected override void OnLoad() => OnLoadHook?.Invoke(Context);
    }
}
