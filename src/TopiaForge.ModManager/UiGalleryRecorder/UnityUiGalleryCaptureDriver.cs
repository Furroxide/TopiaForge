using System;
using TopiaForge.Mods;
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
        private Action<UiWireEnvelope?>? committed;
        private object? hotkey;
        private int requestedFrame;
        private bool pending;
        private bool watchDevice;
        private UiInputDevice lastDevice;

        private void Awake() => enabled = false;

        internal void Initialize(Func<bool, bool> capture, Action<string> warn, Action<UiWireEnvelope?>? committed,
            bool watchDevice = false)
        {
            this.capture = capture;
            this.warn = warn;
            this.committed = committed;
            this.watchDevice = watchDevice;
            lastDevice = TopiaForgeInputOwnership.LastViewerDevice;
            hotkey = TopiaForgeHotkeys.Register(UiGalleryCaptureFixture.Owner, TopiaForgeKey.F11, Request);
            Request();
        }

        internal void ObserveCommit(UiWireEnvelope? envelope) => committed?.Invoke(envelope);

        internal void Request()
        {
            requestedFrame = Time.frameCount;
            pending = true;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (watchDevice && lastDevice != TopiaForgeInputOwnership.LastViewerDevice)
            {
                lastDevice = TopiaForgeInputOwnership.LastViewerDevice;
                Request();
            }
            var elapsed = unchecked(Time.frameCount - requestedFrame);
            if (!pending || elapsed < 2 || capture == null) return;
            var lastFrame = elapsed >= FrameLimit;
            try
            {
                if (capture(lastFrame)) CompleteRequest();
                else if (lastFrame)
                {
                    CompleteRequest();
                    warn?.Invoke("Gallery capture did not reach a visible, settled fixture within 120 frames.");
                }
            }
            catch (Exception error)
            {
                CompleteRequest();
                warn?.Invoke("Gallery capture failed (" + error.GetType().Name + ").");
            }
        }

        private void CompleteRequest()
        {
            pending = false;
            enabled = watchDevice;
        }

        private void OnDestroy()
        {
            if (hotkey != null) TopiaForgeHotkeys.Unregister(hotkey);
            hotkey = null;
            capture = null;
            warn = null;
            committed = null;
        }
    }
}
