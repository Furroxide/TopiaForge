using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public static partial class TopiaForgeToasts
    {
        private static bool RefreshMeasurements()
        {
            var themeChanged = appliedThemeVersion != TopiaForgeTheme.Version;
            var changed = themeChanged || appliedHudTop != bottomRightHudTop || appliedSubtitleReserve != subtitleReserve ||
                appliedScreenWidth != Screen.width || appliedScreenHeight != Screen.height;
            appliedThemeVersion = TopiaForgeTheme.Version;
            appliedHudTop = bottomRightHudTop;
            appliedSubtitleReserve = subtitleReserve;
            appliedScreenWidth = Screen.width;
            appliedScreenHeight = Screen.height;
            for (var index = 0; index < Views.Count; index++)
            {
                if (!Stack.IsOpen(index)) continue;
                var view = Views[index];
                var settings = ViewerSettings(view);
                if (!themeChanged && MatchesMeasurements(view, settings)) continue;
                ApplyAppearance(view, settings);
                Stack.SetHeight(index, MeasuredHeight(view), StackGap * view.Scale);
                changed = true;
            }

            // A waiting viewer can change scale without changing the shared canvas or any placed toast.
            return changed || blockedOffer != null && !MatchesMeasurements(blockedOffer, ViewerSettings(blockedOffer));
        }

        private static bool MatchesMeasurements(ToastView view, TopiaForgeEffectiveAccessibility settings) =>
            view.UiScale == settings.UiScale && view.HighContrast == settings.HighContrast;

        private static float MeasuredHeight(ToastView view) => view.Root!.Rect.sizeDelta.y * view.Scale;

        private static ToastViewport CurrentViewport()
        {
            if (appliedScreenWidth <= 0 || appliedScreenHeight <= 0) return default;
            var screenScale = UiHudViewport.ScreenScale(appliedScreenWidth, appliedScreenHeight);
            var globalScale = layer!.Host.EffectiveUiScale;
            var pixelsPerUnit = screenScale * globalScale;
            var clearance = bottomRightHudTop > 0f
                ? (bottomRightHudTop + appliedScreenHeight * subtitleReserve / screenScale) / globalScale : 0f;
            return new ToastViewport(appliedScreenWidth / pixelsPerUnit, appliedScreenHeight / pixelsPerUnit, clearance);
        }

        private static bool FitHeldViews(ToastViewport viewport, bool resume)
        {
            var column = new ToastColumn();
            var fits = true;
            for (var index = 0; index < Views.Count; index++)
            {
                if (!Stack.IsOpen(index)) continue;
                var view = Views[index];
                column.Append(view);
                fits = fits && viewport.Fits(column);
                if (fits)
                {
                    if (resume) Stack.SetParked(index, false);
                }
                else if (Stack.SetParked(index, true))
                {
                    ReleaseCapture(view);
                    TopiaForgeTween.Cancel(view.Root!);
                    view.Root!.Go.SetActive(false);
                }
            }
            return fits;
        }

        private static bool FitsPending(ToastViewport viewport, int candidate)
        {
            var column = new ToastColumn();
            for (var index = 0; index < Views.Count; index++)
                if (index == candidate || Stack.IsPlaced(index)) column.Append(Views[index]);
            return viewport.Fits(column);
        }

        private static void Restack(ToastViewport viewport)
        {
            var scale = 0f;
            for (var index = 0; index < Views.Count; index++)
                if (Stack.IsPlaced(index)) scale = Math.Max(scale, Views[index].Scale);

            var bottom = viewport.Bottom(scale);
            for (var index = 0; index < Views.Count; index++)
            {
                if (!Stack.TryPlace(index, out var top, out var fresh)) continue;
                var view = Views[index];
                var root = view.Root!;
                var targetY = bottom + top;
                var restingX = -TopiaForgeTokens.SafeMargin * view.Scale;
                if (fresh)
                {
                    root.Rect.anchoredPosition = new Vector2(restingX, targetY);
                    view.RestingX = restingX;
                    view.RestingY = targetY;
                    root.Go.SetActive(true);
                    TopiaForgeMotion.ToastIn(root, restingX, view.Content.Viewer, view.Scale);
                    ObserveCapture(view, view.Content.Observe);
                }
                else
                {
                    var position = root.Rect.anchoredPosition;
                    if (Math.Min(position.y, view.RestingY) < bottom ||
                        Math.Max(position.y, view.RestingY) + MeasuredHeight(view) > viewport.Height)
                    {
                        // A previous position or reflow target can now be behind the HUD or above the screen.
                        TopiaForgeTween.Cancel(root);
                        root.Rect.anchoredPosition = new Vector2(restingX, targetY);
                        TopiaForgeTween.FadeTo(root, 1f, 1f, 0f);
                    }
                    else
                    {
                        if (!Mathf.Approximately(position.y, targetY))
                            TopiaForgeTween.MoveY(root, position.y, targetY, TopiaForgeTokens.DurationFast,
                                TopiaForgeEase.OutCubic, null, skipAtZeroIntensity: true, view.Content.Viewer);
                        if (restingX != view.RestingX)
                            TopiaForgeTween.MoveX(root, position.x, restingX, TopiaForgeTokens.DurationFast,
                                TopiaForgeEase.OutCubic, view.Content.Viewer);
                    }
                    view.RestingX = restingX;
                    view.RestingY = targetY;
                }
            }
        }

        private struct ToastColumn
        {
            private float precedingGap;
            internal float Height { get; private set; }
            internal float Scale { get; private set; }

            internal void Append(ToastView view)
            {
                Height += precedingGap + MeasuredHeight(view);
                Scale = Math.Max(Scale, view.Scale);
                precedingGap = StackGap * view.Scale;
            }
        }

        private readonly struct ToastViewport
        {
            private readonly float width;
            private readonly float dockClearance;

            internal ToastViewport(float width, float height, float dockClearance)
            {
                this.width = width;
                Height = height;
                this.dockClearance = dockClearance;
            }

            internal float Height { get; }
            internal float Bottom(float scale) => Math.Max(TopiaForgeTokens.SafeMargin * scale,
                dockClearance > 0f ? dockClearance + StackGap * scale : 0f);
            internal bool Fits(ToastColumn column) =>
                (ToastWidth + TopiaForgeTokens.SafeMargin) * column.Scale <= width &&
                Bottom(column.Scale) + column.Height <= Height;
        }
    }
}
