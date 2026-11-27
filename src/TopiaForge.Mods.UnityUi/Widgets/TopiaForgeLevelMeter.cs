using System;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Microphone input meter: linear RMS and peak samples map onto a dBFS scale (unit-tested in
    /// <see cref="TopiaForgeLevelMath"/>), with instant attack and a smooth release, an optional
    /// threshold marker (voice activation), and a peak-hold marker. The fill tone follows the
    /// zone: muted below the threshold, success when active, warning when hot, danger when
    /// clipping. Cheap enough to feed every frame: positions are anchors written only when they
    /// move, and nothing allocates.
    /// </summary>
    public sealed class TopiaForgeLevelMeter : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float MarkerWidth = 2f;
        private const float Epsilon = 0.0005f;

        private readonly UImage track;
        private readonly UImage fill;
        private readonly RectTransform fillRect;
        private readonly UImage peak;
        private readonly RectTransform peakRect;
        private readonly UImage threshold;
        private readonly RectTransform thresholdRect;
        private TopiaForgePeakHold peakHold = new TopiaForgePeakHold(1f, 20f / 60f);
        private float floorDecibels = TopiaForgeLevelMath.DefaultFloorDecibels;
        private float ceilingDecibels = TopiaForgeLevelMath.DefaultCeilingDecibels;
        private float hotDecibels = TopiaForgeLevelMath.DefaultHotDecibels;
        private float clipDecibels = TopiaForgeLevelMath.DefaultClipDecibels;
        private float thresholdDecibels = float.NaN;
        private float releaseDecibelsPerSecond = 30f;
        private float peakFallDecibelsPerSecond = 20f;
        private float peakHoldSeconds = 1f;
        private bool peakEnabled = true;
        private float displayed;
        private float shownFill = -1f;
        private float shownPeak = -1f;
        private float lastUpdate = -1f;
        private TopiaForgeLevelZone zone = TopiaForgeLevelZone.Normal;

        internal TopiaForgeLevelMeter(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("LevelMeter"))
        {
            track = Go.AddComponent<UImage>();
            track.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Bar);
            track.type = UImage.Type.Sliced;
            track.raycastTarget = false;

            fill = CreatePart("Fill", sliced: true);
            fillRect = fill.rectTransform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            threshold = CreatePart("Threshold", sliced: false);
            thresholdRect = PlaceMarker(threshold);
            threshold.gameObject.SetActive(false);

            peak = CreatePart("Peak", sliced: false);
            peakRect = PlaceMarker(peak);
            peak.gameObject.SetActive(false);

            this.FixedHeight(12f);
            ApplyTheme(Theme);
        }

        /// <summary>Gets the displayed level in dBFS (after ballistics).</summary>
        public float LevelDecibels => floorDecibels + (displayed * (ceilingDecibels - floorDecibels));

        /// <summary>Gets the zone of the most recent sample.</summary>
        public TopiaForgeLevelZone Zone => zone;

        /// <summary>Sets the visible decibel range (default -60 to 0 dBFS).</summary>
        /// <exception cref="ArgumentOutOfRangeException">The range is empty or not finite.</exception>
        public TopiaForgeLevelMeter Range(float floor, float ceiling)
        {
            if (float.IsNaN(floor) || float.IsInfinity(floor) || float.IsNaN(ceiling) || float.IsInfinity(ceiling) || !(ceiling > floor))
            {
                throw new ArgumentOutOfRangeException(nameof(ceiling), "The meter range must be finite with the ceiling above the floor.");
            }

            floorDecibels = floor;
            ceilingDecibels = ceiling;
            RebuildPeakHold();
            PlaceThreshold();
            return this;
        }

        /// <summary>Sets where the hot and clipping zones start (defaults -6 and -1 dBFS); non-finite values are ignored.</summary>
        public TopiaForgeLevelMeter Zones(float hot, float clip)
        {
            if (!float.IsNaN(hot) && !float.IsInfinity(hot))
            {
                hotDecibels = hot;
            }

            if (!float.IsNaN(clip) && !float.IsInfinity(clip))
            {
                clipDecibels = clip;
            }

            return this;
        }

        /// <summary>Holds each new peak for <paramref name="holdSeconds"/>, then lets it fall.</summary>
        public TopiaForgeLevelMeter PeakHold(float holdSeconds = 1f, float fallDecibelsPerSecond = 20f)
        {
            peakHoldSeconds = holdSeconds;
            peakFallDecibelsPerSecond = fallDecibelsPerSecond;
            peakEnabled = true;
            RebuildPeakHold();
            return this;
        }

        /// <summary>Hides the peak-hold marker.</summary>
        public TopiaForgeLevelMeter NoPeakHold()
        {
            peakEnabled = false;
            peak.gameObject.SetActive(false);
            return this;
        }

        /// <summary>Sets how fast the bar falls back, in decibels per second (0 disables smoothing).</summary>
        public TopiaForgeLevelMeter Release(float decibelsPerSecond)
        {
            releaseDecibelsPerSecond = float.IsNaN(decibelsPerSecond) || decibelsPerSecond < 0f ? 0f : decibelsPerSecond;
            return this;
        }

        /// <summary>Dirty-checked threshold marker in dBFS; NaN removes it.</summary>
        public void SetThreshold(float decibels)
        {
            if (thresholdDecibels.Equals(decibels))
            {
                return;
            }

            thresholdDecibels = decibels;
            PlaceThreshold();
        }

        /// <summary>Feeds one linear sample used as both level and peak.</summary>
        public void SetLevel(float linear)
        {
            SetLevel(linear, linear);
        }

        /// <summary>
        /// Feeds linear RMS and peak samples (full scale 1). Elapsed time comes from the unscaled
        /// clock, so calling twice in a frame or skipping frames keeps the ballistics correct.
        /// </summary>
        public void SetLevel(float rms, float peakLinear)
        {
            var now = Time.unscaledTime;
            var elapsed = lastUpdate < 0f ? 0f : now - lastUpdate;
            lastUpdate = now;

            var range = ceilingDecibels - floorDecibels;
            var decibels = TopiaForgeLevelMath.ToDecibels(rms);
            var target = TopiaForgeLevelMath.ToFraction(decibels, floorDecibels, ceilingDecibels);
            displayed = TopiaForgeLevelMath.Release(displayed, target, elapsed, releaseDecibelsPerSecond / range);
            ApplyFill();

            var nextZone = TopiaForgeLevelMath.Zone(decibels, thresholdDecibels, hotDecibels, clipDecibels);
            if (nextZone != zone)
            {
                zone = nextZone;
                fill.color = ZoneColor(Theme);
            }

            if (peakEnabled)
            {
                var loudest = peakLinear > rms ? peakLinear : rms;
                peakHold.Update(TopiaForgeLevelMath.LinearToFraction(loudest, floorDecibels, ceilingDecibels), elapsed);
                ApplyPeak();
            }
        }

        /// <summary>Drops the bar and the peak marker to silence.</summary>
        public void Clear()
        {
            displayed = 0f;
            peakHold.Reset();
            lastUpdate = -1f;
            zone = TopiaForgeLevelMath.Zone(TopiaForgeLevelMath.SilenceDecibels, thresholdDecibels, hotDecibels, clipDecibels);
            fill.color = ZoneColor(Theme);
            ApplyFill();
            ApplyPeak();
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            track.color = theme.SurfaceSunken;
            fill.color = ZoneColor(theme);
            threshold.color = theme.Text;
            peak.color = theme.TextMuted;
        }

        private Color ZoneColor(TopiaForgeResolvedTheme theme)
        {
            switch (zone)
            {
                case TopiaForgeLevelZone.BelowThreshold:
                    return theme.TextMuted;
                case TopiaForgeLevelZone.Hot:
                    return theme.Warning;
                case TopiaForgeLevelZone.Clipping:
                    return theme.Danger;
                default:
                    return theme.Success;
            }
        }

        private void ApplyFill()
        {
            if (Mathf.Abs(shownFill - displayed) <= Epsilon)
            {
                return;
            }

            shownFill = displayed;
            var anchor = fillRect.anchorMax;
            anchor.x = displayed;
            fillRect.anchorMax = anchor;
        }

        private void ApplyPeak()
        {
            var value = peakEnabled ? peakHold.Value : 0f;
            if (Mathf.Abs(shownPeak - value) <= Epsilon)
            {
                return;
            }

            shownPeak = value;
            var visible = value > Epsilon;
            if (peak.gameObject.activeSelf != visible)
            {
                peak.gameObject.SetActive(visible);
            }

            SetMarkerPosition(peakRect, value);
        }

        private void PlaceThreshold()
        {
            var visible = !float.IsNaN(thresholdDecibels);
            if (threshold.gameObject.activeSelf != visible)
            {
                threshold.gameObject.SetActive(visible);
            }

            if (visible)
            {
                SetMarkerPosition(thresholdRect, TopiaForgeLevelMath.ToFraction(thresholdDecibels, floorDecibels, ceilingDecibels));
            }
        }

        private void RebuildPeakHold()
        {
            var range = ceilingDecibels - floorDecibels;
            var hold = float.IsNaN(peakHoldSeconds) || peakHoldSeconds < 0f || float.IsInfinity(peakHoldSeconds) ? 1f : peakHoldSeconds;
            var fall = float.IsNaN(peakFallDecibelsPerSecond) || peakFallDecibelsPerSecond < 0f || float.IsInfinity(peakFallDecibelsPerSecond)
                ? 20f
                : peakFallDecibelsPerSecond;
            peakHold = new TopiaForgePeakHold(hold, fall / range);
            shownPeak = -1f;
        }

        private static void SetMarkerPosition(RectTransform marker, float fraction)
        {
            var min = marker.anchorMin;
            var max = marker.anchorMax;
            min.x = fraction;
            max.x = fraction;
            marker.anchorMin = min;
            marker.anchorMax = max;
        }

        private static RectTransform PlaceMarker(UImage marker)
        {
            var rect = marker.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(MarkerWidth, 4f);
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private UImage CreatePart(string name, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sliced ? TopiaForgeSprites.Fill(TopiaForgeRadius.Bar) : TopiaForgeSprites.White;
            image.type = sliced ? UImage.Type.Sliced : UImage.Type.Simple;
            image.raycastTarget = false;
            return image;
        }
    }
}
