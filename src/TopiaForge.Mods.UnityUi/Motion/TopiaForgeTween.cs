using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Pooled channel-based tween runner ticked by TopiaForgeRuntime. Channels write directly
    /// to CanvasGroup alpha / localScale / anchoredPosition — no boxing, no closures in
    /// the hot loop (completion callbacks fire once at trigger time). Unscaled time so
    /// menus animate while the game is paused. Under ReducedMotion every tween applies
    /// its end state immediately.
    /// </summary>
    public static class TopiaForgeTween
    {
        private enum Channel
        {
            Alpha,
            ScaleUniform,
            OffsetX,
            OffsetY,
        }

        private struct Tween
        {
            public bool Active;
            public TopiaForgeWidget Target;
            public Channel Kind;
            public float From;
            public float To;
            public float Duration;
            public float Elapsed;
            public TopiaForgeEase Ease;
            public Action? OnDone;
        }

        private const int Capacity = 96;
        private static readonly Tween[] Pool = new Tween[Capacity];
        private static int highWater;
        private static bool overflowLogged;

        public static int ActiveCount { get; private set; }

        public static void FadeTo(TopiaForgeWidget target, float from, float to, float duration, TopiaForgeEase ease = TopiaForgeEase.OutQuad, Action? onDone = null)
        {
            target.EnsureCanvasGroup().alpha = from;
            Start(target, Channel.Alpha, from, to, duration, ease, onDone);
        }

        public static void ScaleTo(TopiaForgeWidget target, float from, float to, float duration, TopiaForgeEase ease = TopiaForgeEase.OutCubic, Action? onDone = null)
        {
            target.Rect.localScale = new Vector3(from, from, 1f);
            Start(target, Channel.ScaleUniform, from, to, duration, ease, onDone);
        }

        public static void MoveX(TopiaForgeWidget target, float from, float to, float duration, TopiaForgeEase ease = TopiaForgeEase.OutCubic, Action? onDone = null)
        {
            Start(target, Channel.OffsetX, from, to, duration, ease, onDone);
        }

        public static void MoveY(TopiaForgeWidget target, float from, float to, float duration, TopiaForgeEase ease = TopiaForgeEase.OutCubic, Action? onDone = null)
        {
            Start(target, Channel.OffsetY, from, to, duration, ease, onDone);
        }

        /// <summary>Cancels every tween on a widget (end values are NOT applied).</summary>
        public static void Cancel(TopiaForgeWidget target)
        {
            for (var index = 0; index < highWater; index++)
            {
                if (Pool[index].Active && ReferenceEquals(Pool[index].Target, target))
                {
                    Pool[index].Active = false;
                    Pool[index].OnDone = null;
                    ActiveCount--;
                }
            }
        }

        internal static void Tick(float unscaledDelta)
        {
            if (ActiveCount == 0)
            {
                return;
            }

            for (var index = 0; index < highWater; index++)
            {
                if (!Pool[index].Active)
                {
                    continue;
                }

                if (Pool[index].Target.Go == null)
                {
                    Pool[index].Active = false;
                    Pool[index].OnDone = null;
                    ActiveCount--;
                    continue;
                }

                if (Pool[index].Target.Host.EffectiveReducedMotion)
                {
                    var target = Pool[index].Target;
                    var kind = Pool[index].Kind;
                    var endValue = Pool[index].To;
                    var onDone = Pool[index].OnDone;
                    Pool[index].Active = false;
                    Pool[index].OnDone = null;
                    ActiveCount--;
                    Apply(target, kind, endValue);
                    TopiaForgeCallbacks.Invoke(onDone, "Tween completion");
                    continue;
                }

                Pool[index].Elapsed += unscaledDelta;
                var t = Pool[index].Duration <= 0f ? 1f : Mathf.Clamp01(Pool[index].Elapsed / Pool[index].Duration);
                var eased = TopiaForgeEasing.Evaluate(Pool[index].Ease, t);
                var value = Pool[index].From + ((Pool[index].To - Pool[index].From) * eased);
                Apply(Pool[index].Target, Pool[index].Kind, value);

                if (t >= 1f)
                {
                    var onDone = Pool[index].OnDone;
                    Pool[index].Active = false;
                    Pool[index].OnDone = null;
                    ActiveCount--;
                    TopiaForgeCallbacks.Invoke(onDone, "Tween completion");
                }
            }
        }

        internal static void Reset()
        {
            Array.Clear(Pool, 0, Pool.Length);
            highWater = 0;
            overflowLogged = false;
            ActiveCount = 0;
        }

        private static void Start(TopiaForgeWidget target, Channel kind, float from, float to, float duration, TopiaForgeEase ease, Action? onDone)
        {
            TopiaForgeRuntime.Ensure();

            if (target.Host.EffectiveReducedMotion || duration <= 0f)
            {
                Apply(target, kind, to);
                TopiaForgeCallbacks.Invoke(onDone, "Tween completion");
                return;
            }

            // Replace an existing tween on the same channel of the same target.
            for (var index = 0; index < highWater; index++)
            {
                if (Pool[index].Active && ReferenceEquals(Pool[index].Target, target) && Pool[index].Kind == kind)
                {
                    Pool[index].From = from;
                    Pool[index].To = to;
                    Pool[index].Duration = duration;
                    Pool[index].Elapsed = 0f;
                    Pool[index].Ease = ease;
                    Pool[index].OnDone = onDone;
                    return;
                }
            }

            for (var index = 0; index < Capacity; index++)
            {
                if (!Pool[index].Active)
                {
                    Pool[index] = new Tween
                    {
                        Active = true,
                        Target = target,
                        Kind = kind,
                        From = from,
                        To = to,
                        Duration = duration,
                        Elapsed = 0f,
                        Ease = ease,
                        OnDone = onDone,
                    };
                    highWater = Math.Max(highWater, index + 1);
                    ActiveCount++;
                    return;
                }
            }

            // Pool exhausted: apply the end state so nothing sticks mid-transition.
            if (!overflowLogged)
            {
                overflowLogged = true;
                TopiaForgeLog.Warn("Tween pool exhausted (" + Capacity + "); applying end states immediately. A mod is animating an unusual number of widgets.");
            }

            Apply(target, kind, to);
            TopiaForgeCallbacks.Invoke(onDone, "Tween completion");
        }

        private static void Apply(TopiaForgeWidget target, Channel kind, float value)
        {
            if (target.Go == null)
            {
                return; // widget destroyed mid-tween
            }

            switch (kind)
            {
                case Channel.Alpha:
                    target.EnsureCanvasGroup().alpha = value;
                    break;
                case Channel.ScaleUniform:
                    target.Rect.localScale = new Vector3(value, value, 1f);
                    break;
                case Channel.OffsetX:
                    {
                        var position = target.Rect.anchoredPosition;
                        position.x = value;
                        target.Rect.anchoredPosition = position;
                        break;
                    }

                case Channel.OffsetY:
                    {
                        var position = target.Rect.anchoredPosition;
                        position.y = value;
                        target.Rect.anchoredPosition = position;
                        break;
                    }
            }
        }
    }
}
