using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// HUD chat log. It keeps a bounded history (the oldest line is evicted), and every line is
    /// sanitized plain text: rich-text tags, escape sequences, line breaks and bidirectional
    /// overrides in player messages cannot restyle or spoof the log. While closed, the newest
    /// lines show for a while and then fade (or vanish at once under reduced motion); while open,
    /// the full history shows on a backdrop with scrollback. Lines are pooled, a new message
    /// rebinds a single line, and per-frame work is bounded by the closed line count.
    /// </summary>
    public sealed partial class TopiaForgeChatLog : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        /// <summary>The longest author name kept in front of a message.</summary>
        public const int MaxAuthorChars = 32;

        private readonly TopiaForgeChatLogModel model;
        private readonly List<LineView> lines = new List<LineView>();
        private readonly Stack<LineView> spare = new Stack<LineView>();
        private readonly TopiaForgeChatLogDriver driver;
        private float visibleSeconds = TopiaForgeChatFade.DefaultVisibleSeconds;
        private float fadeSeconds = TopiaForgeChatFade.DefaultFadeSeconds;
        private int closedLines = 8;
        private bool open;

        internal TopiaForgeChatLog(TopiaForgeContainer parent, int capacity, int maxLineChars)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("ChatLog"))
        {
            model = new TopiaForgeChatLogModel(capacity, maxLineChars);
            BuildChrome();
            driver = Go.AddComponent<TopiaForgeChatLogDriver>();
            driver.Log = this;
            driver.enabled = false;
            ApplyOpenState();
            ApplyTheme(Theme);
        }

        /// <summary>Gets how many lines the log retains.</summary>
        public int Capacity => model.Capacity;

        /// <summary>Gets the number of retained lines.</summary>
        public int Count => model.Count;

        /// <summary>Gets whether the log is open (full history, scrollback).</summary>
        public bool IsOpen => open;

        /// <summary>Sets how long a closed line stays fully visible and how long it then fades.</summary>
        public TopiaForgeChatLog Timing(float visible, float fade)
        {
            visibleSeconds = Clamp(visible, 0.5f, 120f, TopiaForgeChatFade.DefaultVisibleSeconds);
            fadeSeconds = Clamp(fade, 0f, 10f, TopiaForgeChatFade.DefaultFadeSeconds);
            Refresh();
            return this;
        }

        /// <summary>Sets the most lines shown while closed (clamped to the capacity).</summary>
        public TopiaForgeChatLog ClosedLines(int maxLines)
        {
            closedLines = Math.Max(1, Math.Min(maxLines, model.Capacity));
            Refresh();
            return this;
        }

        /// <summary>Posts a line. Returns false when the text is empty after sanitizing.</summary>
        public bool Push(string text, TopiaForgeChatLineKind kind = TopiaForgeChatLineKind.Player)
        {
            var countBefore = model.Count;
            if (!model.TryAppend(text, kind, Time.unscaledTime, out var line))
            {
                return false;
            }

            LineView view;
            if (model.Count == countBefore)
            {
                // Full: the oldest line was evicted, so its view becomes the newest one.
                view = lines[0];
                lines.RemoveAt(0);
            }
            else
            {
                view = spare.Count > 0 ? spare.Pop() : CreateLine();
            }

            lines.Add(view);
            view.Root.transform.SetAsLastSibling();
            Bind(view, line);
            Refresh();
            if (open)
            {
                ScrollToEnd();
            }

            return true;
        }

        /// <summary>Posts "author: message"; the author is bounded to <see cref="MaxAuthorChars"/>.</summary>
        public bool Push(string author, string message, TopiaForgeChatLineKind kind = TopiaForgeChatLineKind.Player)
        {
            var name = TopiaForgePlainText.Sanitize(author, MaxAuthorChars);
            return name.Length == 0 ? Push(message, kind) : Push(name + ": " + message, kind);
        }

        /// <summary>Dirty-checked open state: open shows the full history with scrollback.</summary>
        public void SetOpen(bool value)
        {
            if (open == value)
            {
                return;
            }

            open = value;
            ApplyOpenState();
            ApplyTheme(Theme);
            if (open)
            {
                ScrollToEnd();
            }
        }

        /// <summary>Removes every line.</summary>
        public void Clear()
        {
            model.Clear();
            for (var index = 0; index < lines.Count; index++)
            {
                SetShown(lines[index], false);
                spare.Push(lines[index]);
            }

            lines.Clear();
            driver.enabled = false;
        }

        /// <summary>Scrolls the open log to its newest line.</summary>
        public void ScrollToEnd()
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        /// <summary>
        /// Applies the resolved theme, and re-evaluates which lines show: accessibility changes arrive
        /// here, so turning reduced motion off mid-fade brings a hidden line back and restarts the fade.
        /// </summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            backdrop.color = theme.Surface;
            scrollbarTrack.color = theme.SurfaceSunken;
            scrollbarHandle.color = theme.Tint;
            var backing = open ? Color.clear : theme.Surface;
            if (!open && !theme.HighContrast)
            {
                backing.a *= 0.7f;
            }

            for (var index = 0; index < lines.Count; index++)
            {
                Paint(lines[index], backing, theme);
            }

            lineBacking = backing;
            Refresh();
        }

        internal void Tick()
        {
            if (open)
            {
                driver.enabled = false;
                return;
            }

            var now = Time.unscaledTime;
            var reduced = Host.EffectiveReducedMotion;
            var anyVisible = false;
            var first = Math.Max(0, model.Count - closedLines);
            for (var index = first; index < model.Count; index++)
            {
                var alpha = TopiaForgeChatFade.Alpha(now - model[index].PostedAt, visibleSeconds, fadeSeconds, reduced);
                SetShown(lines[index], alpha > 0f);
                SetAlpha(lines[index], alpha);
                anyVisible |= alpha > 0f;
            }

            if (!anyVisible)
            {
                driver.enabled = false;
            }
        }

        private void Refresh()
        {
            if (open)
            {
                for (var index = 0; index < lines.Count; index++)
                {
                    SetShown(lines[index], true);
                    SetAlpha(lines[index], 1f);
                }

                driver.enabled = false;
                return;
            }

            var visible = model.CountVisibleWhenClosed(Time.unscaledTime, visibleSeconds, fadeSeconds, closedLines);
            var firstVisible = model.Count - visible;
            for (var index = 0; index < firstVisible; index++)
            {
                SetShown(lines[index], false);
            }

            driver.enabled = visible > 0;
            Tick();
        }

        private void Bind(LineView view, TopiaForgeChatLine line)
        {
            view.Kind = line.Kind;
            view.Text.text = line.Text;
            Paint(view, lineBacking, Theme);
        }

        private void Paint(LineView view, Color backing, TopiaForgeResolvedTheme theme)
        {
            view.Backing.color = backing;
            view.Text.color = ToneOf(view.Kind, theme);
        }

        private static Color ToneOf(TopiaForgeChatLineKind kind, TopiaForgeResolvedTheme theme)
        {
            switch (kind)
            {
                case TopiaForgeChatLineKind.System:
                    return theme.TextMuted;
                case TopiaForgeChatLineKind.Whisper:
                    return theme.Accent;
                case TopiaForgeChatLineKind.Error:
                    return theme.Danger;
                default:
                    return theme.Text;
            }
        }

        private static void SetShown(LineView view, bool shown)
        {
            if (view.Root.activeSelf != shown)
            {
                view.Root.SetActive(shown);
            }
        }

        private static void SetAlpha(LineView view, float alpha)
        {
            if (Mathf.Abs(view.Alpha - alpha) > 0.004f)
            {
                view.Alpha = alpha;
                view.Group.alpha = alpha;
            }
        }

        private static float Clamp(float value, float min, float max, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
        }
    }

    /// <summary>Per-frame fade driver, enabled only while a closed log still shows lines.</summary>
    internal sealed class TopiaForgeChatLogDriver : MonoBehaviour
    {
        public TopiaForgeChatLog? Log;

        private void Update()
        {
            Log?.Tick();
        }
    }
}
