using System;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>Samples only after a Gallery request and releases the hotkey with its canvas.</summary>
    internal sealed class UnityUiGalleryCaptureDriver : MonoBehaviour
    {
        private const int FrameLimit = 120;
        private Func<bool, bool>? capture;
        private Action<string>? warn;
        private object? hotkey;
        private int requestedFrame;

        private void Awake() => enabled = false;

        internal void Initialize(Func<bool, bool> capture, Action<string> warn)
        {
            this.capture = capture;
            this.warn = warn;
            hotkey = TopiaForgeHotkeys.Register(UiGalleryCaptureFixture.Owner, TopiaForgeKey.F10, Request);
            Request();
        }

        private void Request()
        {
            requestedFrame = Time.frameCount;
            enabled = true;
        }

        private void LateUpdate()
        {
            var elapsed = unchecked(Time.frameCount - requestedFrame);
            if (elapsed < 2 || capture == null) return;
            var lastFrame = elapsed >= FrameLimit;
            try
            {
                if (capture(lastFrame)) enabled = false;
                else if (lastFrame)
                {
                    enabled = false;
                    warn?.Invoke("Gallery capture did not reach a visible, settled fixture within 120 frames.");
                }
            }
            catch (Exception error)
            {
                enabled = false;
                warn?.Invoke("Gallery capture failed (" + error.GetType().Name + ").");
            }
        }

        private void OnDestroy()
        {
            if (hotkey != null) TopiaForgeHotkeys.Unregister(hotkey);
            hotkey = null;
            capture = null;
            warn = null;
        }
    }
}
