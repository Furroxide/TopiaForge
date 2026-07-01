using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    public sealed class NeonPulse : MonoBehaviour
    {
        public float Frequency = 2f;
        public float AlphaAmplitude = 0.12f;
        public float ScaleAmplitude = 0.02f;
        public bool UseUnscaledTime = true;

        private Graphic? graphic;
        private Color baseColor;
        private Vector3 baseScale;

        private void Awake()
        {
            graphic = GetComponent<Graphic>();
            if (graphic != null)
            {
                baseColor = graphic.color;
            }

            baseScale = transform.localScale;
        }

        private void Update()
        {
            var time = UseUnscaledTime ? Time.unscaledTime : Time.time;
            var pulse = 0.5f + (0.5f * Mathf.Sin(time * Mathf.PI * 2f * Mathf.Max(0.01f, Frequency)));
            transform.localScale = baseScale * (1f + (ScaleAmplitude * pulse));
            if (graphic != null)
            {
                var color = baseColor;
                color.a = Mathf.Clamp01(baseColor.a - AlphaAmplitude + (AlphaAmplitude * 2f * pulse));
                graphic.color = color;
            }
        }
    }
}
