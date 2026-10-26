using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Observes the resolved viewport after layout, with no work at a steady size.</summary>
    internal sealed class TopiaForgeListViewRuntime : MonoBehaviour
    {
        private RectTransform? viewport;
        private Action? refresh;
        private float lastHeight = float.NaN;

        internal void Initialize(RectTransform rect, Action onResize)
        {
            viewport = rect;
            refresh = onResize;
        }

        private void OnEnable() => lastHeight = float.NaN;

        private void LateUpdate()
        {
            if (viewport == null) return;
            var height = viewport.rect.height;
            if (height <= 0 || height == lastHeight) return;
            lastHeight = height;
            refresh?.Invoke();
        }

        private void OnDestroy()
        {
            refresh = null;
            viewport = null;
        }
    }
}
