using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private sealed class LoadedMod
        {
            public LoadedMod(ModManifest manifest, IModEntrypoint instance, ModContext context)
            {
                Manifest = manifest;
                Instance = instance;
                Context = context;
            }

            public ModManifest Manifest { get; }
            public IModEntrypoint Instance { get; }
            public ModContext Context { get; }
        }

        private interface IModEntrypoint
        {
            void OnLoad(IModContext context);
            void OnUnload();
        }

        private sealed class V1ModEntrypoint : IModEntrypoint
        {
            private readonly TopiaForgeMod mod;

            public V1ModEntrypoint(TopiaForgeMod mod)
            {
                this.mod = mod;
            }

            public void OnLoad(IModContext context)
            {
                mod.Load(context);
            }

            public void OnUnload()
            {
                mod.Unload();
            }
        }
    }
}
