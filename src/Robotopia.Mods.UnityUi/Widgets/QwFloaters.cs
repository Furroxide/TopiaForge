using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Pooled world-anchored HUD labels: drifting damage floaters and speech bubbles
    /// projected from world space every frame (camera resolve with fallback scan,
    /// behind-camera culling, oldest-slot reuse — the proven Zombies pooling wholesale).
    /// </summary>
    public sealed class QwFloaterLayer : QwWidget
    {
        private struct Slot
        {
            public bool Active;
            public float SpawnTime;
            public float Ttl;
            public Vector3 World;
            public TextMeshProUGUI Label;
            public RectTransform Rect;
            public UImage? Backing;
            public float BackingBaseAlpha;
        }

        private readonly Slot[] slots;
        private readonly bool bubbles;
        private readonly float riseDistance;
        private RectTransform? canvasRect;
        private Camera? cachedCamera;
        private float nextCameraCheck;

        internal QwFloaterLayer(QwContainer parent, int poolSize, bool speechBubbles)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(speechBubbles ? "SpeechLayer" : "FloaterLayer"))
        {
            bubbles = speechBubbles;
            riseDistance = speechBubbles ? 0f : 48f;
            QwAnchors.Stretch(Rect);
            slots = new Slot[Mathf.Max(1, poolSize)];

            for (var index = 0; index < slots.Length; index++)
            {
                var go = new GameObject((bubbles ? "Bubble" : "Floater") + index, typeof(RectTransform));
                go.transform.SetParent(Go.transform, false);
                var rect = (RectTransform)go.transform;
                rect.sizeDelta = bubbles ? new Vector2(240f, 56f) : new Vector2(160f, 30f);

                UImage? backing = null;
                if (bubbles)
                {
                    var backingGo = new GameObject("Backing", typeof(RectTransform));
                    backingGo.transform.SetParent(go.transform, false);
                    backing = backingGo.AddComponent<UImage>();
                    backing.sprite = QwSprites.Fill(QwRadius.Tip);
                    backing.type = UImage.Type.Sliced;
                    backing.raycastTarget = false;
                    QwAnchors.Stretch((RectTransform)backingGo.transform);
                }

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(go.transform, false);
                var label = QwTmp.Create(labelGo);
                label.fontSize = bubbles ? QwTokens.LabelSize : 18f;
                label.alignment = TextAlignmentOptions.Center;
                label.textWrappingMode = bubbles ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                var font = QwFonts.For(QwTextStyle.Label);
                if (font != null)
                {
                    label.font = font;
                }

                if (QwFonts.UseFauxBold)
                {
                    label.fontStyle = FontStyles.Bold;
                }

                QwAnchors.Stretch((RectTransform)labelGo.transform, bubbles ? 10f : 0f, bubbles ? 6f : 0f, bubbles ? 10f : 0f, bubbles ? 6f : 0f);

                go.SetActive(false);
                slots[index] = new Slot
                {
                    Label = label,
                    Rect = rect,
                    Backing = backing,
                    BackingBaseAlpha = 0.62f,
                };
            }

            var driver = Go.AddComponent<QwFloaterDriver>();
            driver.Layer = this;
        }

        /// <summary>Spawns a label at a world position (reuses the oldest slot when full).</summary>
        public void Push(Vector3 world, string text, Color color, float ttlSeconds = 1.1f)
        {
            var slot = FindSlot();
            slots[slot].Active = true;
            slots[slot].SpawnTime = Time.unscaledTime;
            slots[slot].Ttl = Mathf.Max(0.1f, ttlSeconds);
            slots[slot].World = world;
            slots[slot].Label.text = text;
            slots[slot].Label.color = Theme.Emphasize(color);
            if (slots[slot].Backing != null)
            {
                var theme = Theme;
                var backingColor = theme.SurfaceAlt;
                slots[slot].Backing!.color = backingColor;
                slots[slot].BackingBaseAlpha = backingColor.a;
            }

            slots[slot].Rect.gameObject.SetActive(true);
        }

        /// <summary>Deactivates every slot (round reset / ClearTransient).</summary>
        public void Clear()
        {
            for (var index = 0; index < slots.Length; index++)
            {
                slots[index].Active = false;
                slots[index].Rect.gameObject.SetActive(false);
            }
        }

        internal void Tick()
        {
            var camera = ResolveCamera();
            if (camera == null || canvasRect == null && (canvasRect = FindCanvasRect()) == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            for (var index = 0; index < slots.Length; index++)
            {
                if (!slots[index].Active)
                {
                    continue;
                }

                var age = now - slots[index].SpawnTime;
                var life = age / slots[index].Ttl;
                if (life >= 1f)
                {
                    slots[index].Active = false;
                    slots[index].Rect.gameObject.SetActive(false);
                    continue;
                }

                var world = slots[index].World;
                if (bubbles)
                {
                    world.y += 0.4f;
                }

                var screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                {
                    slots[index].Rect.gameObject.SetActive(false);
                    continue;
                }

                slots[index].Rect.gameObject.SetActive(true);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, null, out var local);
                if (bubbles)
                {
                    local.y += 34f;
                }
                else
                {
                    local.y += riseDistance * life;
                }

                slots[index].Rect.anchoredPosition = local;

                // Fade after 70% of the lifetime (ported timing).
                var alpha = life < 0.7f ? 1f : 1f - ((life - 0.7f) / 0.3f);
                var color = slots[index].Label.color;
                if (!Mathf.Approximately(color.a, alpha))
                {
                    color.a = alpha;
                    slots[index].Label.color = color;
                }

                if (slots[index].Backing != null)
                {
                    var backing = slots[index].Backing!.color;
                    backing.a = slots[index].BackingBaseAlpha * alpha;
                    slots[index].Backing!.color = backing;
                }
            }
        }

        private int FindSlot()
        {
            var oldest = 0;
            var oldestTime = float.MaxValue;
            for (var index = 0; index < slots.Length; index++)
            {
                if (!slots[index].Active)
                {
                    return index;
                }

                if (slots[index].SpawnTime < oldestTime)
                {
                    oldestTime = slots[index].SpawnTime;
                    oldest = index;
                }
            }

            return oldest;
        }

        private RectTransform? FindCanvasRect()
        {
            var canvas = Go.GetComponentInParent<Canvas>();
            return canvas != null ? (RectTransform)canvas.transform : null;
        }

        private Camera? ResolveCamera()
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            {
                return cachedCamera;
            }

            if (Time.unscaledTime < nextCameraCheck)
            {
                return cachedCamera;
            }

            nextCameraCheck = Time.unscaledTime + 0.5f;
            cachedCamera = Camera.main;
            if (cachedCamera == null)
            {
                var all = Camera.allCameras;
                for (var index = 0; index < all.Length; index++)
                {
                    if (all[index] != null && all[index].isActiveAndEnabled)
                    {
                        cachedCamera = all[index];
                        break;
                    }
                }
            }

            return cachedCamera;
        }
    }

    /// <summary>Per-frame driver for a floater layer (only alive while the layer is).</summary>
    internal sealed class QwFloaterDriver : MonoBehaviour
    {
        public QwFloaterLayer? Layer;

        private void Update()
        {
            Layer?.Tick();
        }
    }
}
