using System;
using System.Collections.Generic;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// Renders prop thumbnails for the spawn menu with a hidden offscreen rig. HDRP does
    /// not support manual Camera.Render(), so the rig's camera is enabled for exactly one
    /// frame per capture (frame A: instantiate + sanitize + frame the clone; frame B: copy
    /// the render target GPU-side, tear the clone down) — one prop in flight at a time so
    /// menu-open hitches stay bounded. Results are cached per prop id for the menu's
    /// lifetime; failures cache a sentinel so they are never retried per frame.
    /// </summary>
    internal sealed class PropThumbnails : IDisposable
    {
        // High above the world: directional scene light still applies, HDRP height fog
        // (densest below its base height) does not, and no arena geometry lives there.
        private static readonly Vector3 RigPosition = new Vector3(0f, 4000f, 0f);
        private static readonly Color BackgroundColor = new Color(0.13f, 0.15f, 0.19f, 1f);

        private readonly PropCatalog catalog;
        private readonly IModLogger logger;
        private readonly int size;
        private readonly Dictionary<string, Texture2D?> cache = new Dictionary<string, Texture2D?>(StringComparer.Ordinal);
        private readonly Queue<SandboxPropDefinition> queue = new Queue<SandboxPropDefinition>();
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);

        private GameObject? rig;
        private Camera? camera;
        private Light? light;
        private RenderTexture? target;
        private GameObject? capture;         // the sanitized clone currently in front of the camera
        private string? captureId;
        private bool disposed;

        public PropThumbnails(PropCatalog catalog, IModLogger logger, int size = 128)
        {
            this.catalog = catalog;
            this.logger = logger;
            this.size = size;
        }

        /// <summary>Fired on the frame a requested thumbnail lands in the cache.</summary>
        public event Action<string, Texture2D>? Ready;

        /// <summary>The cached thumbnail, or null when never rendered (or failed).</summary>
        public Texture2D? TryGet(string id)
        {
            return cache.TryGetValue(id, out var texture) ? texture : null;
        }

        /// <summary>Enqueues a render unless the id is already cached, failed, or pending.</summary>
        public void Request(SandboxPropDefinition definition)
        {
            if (disposed || cache.ContainsKey(definition.Id) || !queued.Add(definition.Id))
            {
                return;
            }

            queue.Enqueue(definition);
        }

        /// <summary>Per-frame pump: finishes the in-flight capture, then starts the next one.</summary>
        public void Update()
        {
            if (disposed)
            {
                return;
            }

            if (captureId != null)
            {
                FinishCapture();
                return;
            }

            while (queue.Count > 0)
            {
                var definition = queue.Dequeue();
                queued.Remove(definition.Id);
                if (cache.ContainsKey(definition.Id))
                {
                    continue;
                }

                if (BeginCapture(definition))
                {
                    return; // one in flight; the rest of the queue waits its frame
                }

                cache[definition.Id] = null; // failed — never retried
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            queue.Clear();
            queued.Clear();
            if (capture != null)
            {
                UnityEngine.Object.Destroy(capture);
                capture = null;
            }

            foreach (var texture in cache.Values)
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }

            cache.Clear();
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                target = null;
            }

            if (rig != null)
            {
                UnityEngine.Object.Destroy(rig);
                rig = null;
            }
        }

        private bool BeginCapture(SandboxPropDefinition definition)
        {
            try
            {
                EnsureRig();
                if (camera == null || !catalog.TryInstantiate(definition, out var clone))
                {
                    return false;
                }

                Sanitize(clone);
                clone.transform.SetParent(rig!.transform, worldPositionStays: false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;

                if (!TryComputeBounds(clone, out var bounds))
                {
                    UnityEngine.Object.Destroy(clone);
                    return false;
                }

                var framing = QwPreviewMath.Frame(bounds.extents.x, bounds.extents.y, bounds.extents.z);
                var offset = new Vector3(framing.OffsetX, framing.OffsetY, framing.OffsetZ);
                camera.transform.position = bounds.center + (offset * framing.Distance);
                camera.transform.rotation = Quaternion.LookRotation(-offset, Vector3.up);
                camera.orthographicSize = framing.OrthoHalfSize;
                camera.nearClipPlane = framing.NearPlane;
                camera.farClipPlane = framing.FarPlane;

                camera.enabled = true;
                if (light != null)
                {
                    light.enabled = true;
                }

                capture = clone;
                captureId = definition.Id;
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Sandbox thumbnail capture failed for '" + definition.Id + "': " + ex.Message);
                if (capture != null)
                {
                    UnityEngine.Object.Destroy(capture);
                    capture = null;
                }

                captureId = null;
                return false;
            }
        }

        private void FinishCapture()
        {
            var id = captureId!;
            captureId = null;

            try
            {
                if (camera != null)
                {
                    camera.enabled = false;
                }

                if (light != null)
                {
                    light.enabled = false;
                }

                if (capture != null)
                {
                    UnityEngine.Object.Destroy(capture);
                    capture = null;
                }

                if (target == null)
                {
                    cache[id] = null;
                    return;
                }

                // GPU-side copy — no ReadPixels stall; the texture is display-only.
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
                {
                    name = "Sandbox Thumbnail " + id,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Graphics.CopyTexture(target, 0, 0, texture, 0, 0);
                cache[id] = texture;
                Ready?.Invoke(id, texture);
            }
            catch (Exception ex)
            {
                logger.Warn("Sandbox thumbnail copy failed for '" + id + "': " + ex.Message);
                cache[id] = null;
            }
        }

        private void EnsureRig()
        {
            if (rig != null)
            {
                return;
            }

            rig = new GameObject("SandboxThumbnailRig")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            rig.transform.position = RigPosition;

            target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32)
            {
                name = "Sandbox Thumbnail RT",
                hideFlags = HideFlags.HideAndDontSave,
            };
            target.Create();

            var cameraGo = new GameObject("Camera");
            cameraGo.transform.SetParent(rig.transform, false);
            camera = cameraGo.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.targetTexture = target;
            camera.cullingMask = ~0; // distance isolates the rig; no layer assumptions

            try
            {
                var cameraData = cameraGo.AddComponent<HDAdditionalCameraData>();
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = BackgroundColor;
            }
            catch (Exception ex)
            {
                logger.Warn("Sandbox thumbnail rig: HDRP camera setup unavailable (" + ex.Message + ").");
            }

            // Fill light angled with the default framing so captures never depend on the
            // scene having a sun. A touch of double-lighting when it does is acceptable.
            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(rig.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(40f, 225f, 0f);
            light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.enabled = false;
            try
            {
                lightGo.AddComponent<HDAdditionalLightData>();
                light.intensity = 20000f; // lux — sunlight order under HDRP physical exposure
            }
            catch (Exception ex)
            {
                light.intensity = 1.5f;
                logger.Warn("Sandbox thumbnail rig: HDRP light setup unavailable (" + ex.Message + ").");
            }
        }

        /// <summary>
        /// Strips behavior from a freshly instantiated clone so it only renders: no
        /// scripts thinking, no sounds, no physics falling out of frame.
        /// </summary>
        private static void Sanitize(GameObject clone)
        {
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (behaviour != null)
                {
                    behaviour.enabled = false;
                }
            }

            foreach (var audio in clone.GetComponentsInChildren<AudioSource>(includeInactive: true))
            {
                audio.enabled = false;
            }

            foreach (var collider in clone.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                collider.enabled = false;
            }

            foreach (var body in clone.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }
        }

        /// <summary>Union of mesh renderer bounds; false when there is nothing to picture.</summary>
        private static bool TryComputeBounds(GameObject clone, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(includeInactive: false))
            {
                // Particle/trail/VFX renderers report unstable bounds; meshes carry the shape.
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found && bounds.extents.sqrMagnitude > 0.000001f;
        }
    }
}
