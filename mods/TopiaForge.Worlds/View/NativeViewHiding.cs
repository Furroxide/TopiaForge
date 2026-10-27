using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds
{
    internal sealed class NativeViewHiding : IDisposable
    {
        private readonly OwnedRendererVisibility<Renderer> visibility = new OwnedRendererVisibility<Renderer>(r => r != null, r => r.enabled, (r, value) => r.enabled = value);
        private readonly Type? marker = Type.GetType("UgcImportedEntity, GameCode", false);
        private readonly Type? visualRoot = Type.GetType("UgcImportedVisualRoot, GameCode", false);
        internal OperationResult<IDisposable> Hide(GameObject target)
        {
            if (marker == null || visualRoot == null) return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "The game's authored entity boundaries are unavailable.");
            try
            {
                var own = new List<Renderer>();
                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                {
                    if (ViewRendererSelection.BelongsTo(target.transform, renderer.transform,
                        node => node.parent, node => node.GetComponent(marker) != null)) own.Add(renderer);
                }
                return OperationResult<IDisposable>.Success(visibility.Hide(own));
            }
            catch { return OperationResult<IDisposable>.Failure(ModErrorCode.External, "The object's renderers could not be hidden."); }
        }
        public void Dispose() => visibility.Dispose();
    }
}
