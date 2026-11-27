using System;
using TopiaForge.Mods;

namespace TopiaForge.SecondTestMod
{
    public sealed class RuntimeUiClockProviderMod : TopiaForgeMod
    {
        public static Action<IModContext>? OnLoadHook;
        protected override void OnLoad() => OnLoadHook?.Invoke(Context);
    }
}
