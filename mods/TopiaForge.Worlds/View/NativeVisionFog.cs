using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    internal sealed class NativeVisionFog : IDisposable
    {
        private const float FadeSeconds = 0.5f;
        private readonly WorldSceneIdentity scene;
        private readonly IModEvents events;
        private GameObject? root;
        private Volume? volume;
        private VolumeProfile? profile;
        private Fog? component;
        private IDisposable? animation;
        private float elapsed;
        private float startWeight;
        private float targetWeight;
        private float startDistance;
        private float targetDistance;
        private Color startColor;
        private Color targetColor;
        private bool disposed;
        internal NativeVisionFog(WorldSceneIdentity scene, IModEvents events) { this.scene = scene; this.events = events; }
        internal static bool IsSupported => GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset;
        internal OperationResult<bool> Set(VisionFog? value)
        {
            if (disposed) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The fog renderer has stopped.");
            if (!value.HasValue && root == null) return OperationResult<bool>.Success(true);
            if (!IsSupported || !UnityWorldScene.TryGetLoaded(scene, out var loaded))
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The current renderer cannot draw vision fog.");
            try
            {
                if (root == null)
                {
                    root = new GameObject("Worlds Vision Fog") { hideFlags = HideFlags.DontSave };
                    SceneManager.MoveGameObjectToScene(root, loaded);
                    profile = ScriptableObject.CreateInstance<VolumeProfile>(); profile.hideFlags = HideFlags.DontSave;
                    component = profile.Add<Fog>(true);
                    component.enabled.Override(true);
                    component.colorMode.Override(FogColorMode.ConstantColor);
                    component.baseHeight.Override(-10000);
                    component.maximumHeight.Override(10000);
                    component.enableVolumetricFog.Override(false);
                    volume = root.AddComponent<Volume>(); volume.isGlobal = true;
                    volume.priority = 10000; volume.weight = 0; volume.sharedProfile = profile;
                    component.meanFreePath.Override(500);
                }
                animation?.Dispose(); animation = null;
                elapsed = 0; startWeight = volume!.weight; targetWeight = value.HasValue ? 1 : 0;
                startDistance = component!.meanFreePath.value;
                // Default optical depth: about 99% attenuation at the requested distance. Native tuning is isolated here.
                targetDistance = value.HasValue ? value.Value.VisibleDistance / 4.60517f : startDistance;
                startColor = component.color.value;
                targetColor = value.HasValue ? Tone(value.Value.Tint) : startColor;
                component.maxFogDistance.Override(value?.VisibleDistance ?? 500);
                if (TopiaForgeTheme.ReducedMotion) Apply(1);
                else animation = events.SubscribeUpdate(Update);
                return OperationResult<bool>.Success(true);
            }
            catch
            {
                ReleaseNative();
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The renderer could not create vision fog. Update TopiaForge.");
            }
        }
        private static Color Tone(FogTint tint) => tint == FogTint.Haze ? new Color(0.55f, 0.58f, 0.62f)
            : tint == FogTint.Smoke ? new Color(0.18f, 0.18f, 0.18f) : new Color(0.005f, 0.005f, 0.005f);
        private void Update(float delta)
        {
            if (volume == null || component == null) { animation?.Dispose(); animation = null; return; }
            // Use unscaled time so a paused world still completes its visual transition.
            elapsed += Time.unscaledDeltaTime;
            var amount = TopiaForgeTheme.ReducedMotion ? 1 : Mathf.Clamp01(elapsed / FadeSeconds);
            Apply(amount);
            if (amount >= 1) { animation?.Dispose(); animation = null; }
        }
        private void Apply(float amount)
        {
            if (volume == null || component == null) return;
            volume.weight = Mathf.Lerp(startWeight, targetWeight, amount);
            component.meanFreePath.Override(Mathf.Lerp(startDistance, targetDistance, amount));
            component.color.Override(Color.Lerp(startColor, targetColor, amount));
            if (amount >= 1 && targetWeight == 0) ReleaseNative();
        }
        private void ReleaseNative()
        {
            animation?.Dispose(); animation = null;
            if (volume != null) { volume.weight = 0; volume.sharedProfile = null; }
            if (component != null) UnityEngine.Object.Destroy(component);
            if (profile != null) UnityEngine.Object.Destroy(profile);
            if (root != null) UnityEngine.Object.Destroy(root);
            component = null; profile = null; volume = null; root = null;
        }
        public void Dispose() { if (disposed) return; disposed = true; ReleaseNative(); }
    }
}
