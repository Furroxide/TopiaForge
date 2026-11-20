using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface : IUiSurface, IUiSurfaceDismissalSource, IPresentationSurfaceState
        {
            private static readonly OperationResult<bool> ContentApplied = OperationResult<bool>.Success(true);

            private TopiaForgeWidget? widget;
            private TopiaForgeWindow? window;
            private TopiaForgeFullscreenTool? fullscreen;
            private TopiaForgeLabel? body;
            private TopiaForgeContainer? compositionParent;
            private readonly ScrollRect? outerScroll;
            private TopiaForgeContainer? compositionRoot;
            private UnityUiLayoutRenderer? layoutRenderer;
            private TopiaForgeContainer? layoutFallback;
            private UiThemeDescriptor? declaredTheme;
            private bool remoteComposition;
            private int bodyVersion;
            private string? sentBody;
            private IReadOnlyDictionary<string, TopiaForgeGraphCanvas> retainedGraphs =
                new Dictionary<string, TopiaForgeGraphCanvas>(StringComparer.Ordinal);
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;
            private readonly UiCallbackGate callbacks;
            private readonly UiSurfacePresence presence;
            private Action<UnityUiSurface>? release;
            private Action? nativeClosed;
            private IDisposable? lifetimeLease;
            private UiNode? admitted;
            private ContentStamp committedStamp;
            private UnityUiHudPlacement? placement;
            private int dockHandle;
            private int disposed;

            private UnityUiSurface(
                string id,
                GameObject canvasRoot,
                TopiaForgeWidget widget,
                TopiaForgeWindow? window,
                TopiaForgeFullscreenTool? fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                UiSurfacePresence presence,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                Id = id;
                CanvasRoot = canvasRoot;
                this.widget = widget;
                this.window = window;
                this.fullscreen = fullscreen;
                this.body = body;
                this.compositionParent = compositionParent;
                outerScroll = window != null || fullscreen != null
                    ? compositionParent.Go.GetComponentInParent<ScrollRect>(includeInactive: true) : null;
                this.presence = presence;
                this.lifetime = lifetime;
                this.logger = logger;
                this.release = release;
                callbacks = new UiCallbackGate(lifetime, logger);
                if (window != null)
                {
                    nativeClosed = HandleNativeClosed;
                    window.Closed += nativeClosed;
                }
                else if (fullscreen != null)
                {
                    nativeClosed = HandleNativeClosed;
                    fullscreen.Closed += nativeClosed;
                }
            }

            public string Id { get; }

            /// <summary>Gets the kit canvas the surface draws on, by which a canvas destroyed outside TopiaForge is matched.</summary>
            internal GameObject CanvasRoot { get; }

            /// <summary>Gets whether the surface is a HUD that hides outside gameplay.</summary>
            internal bool FollowsGameplay => presence.FollowsGameplay;

            public bool IsVisible => presence.IsVisible;

            bool IPresentationSurfaceState.IsAlive => presence.IsLive;

            public event Action? Dismissed;

            public static UnityUiSurface ForWindow(
                string id,
                TopiaForgeWindow window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, window.CanvasRoot, window, window, null, body, compositionParent,
                    new UiSurfacePresence(followsGameplay: false, gameplay: true), lifetime, logger, release);
            }

            public static UnityUiSurface ForFullscreen(
                string id,
                TopiaForgeFullscreenTool fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, fullscreen.CanvasRoot, fullscreen, null, fullscreen, body, compositionParent,
                    new UiSurfacePresence(followsGameplay: false, gameplay: true), lifetime, logger, release);
            }

            public static UnityUiSurface ForHud(
                string id,
                TopiaForgeHudLayer layer,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                bool followsGameplay,
                bool gameplay,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, layer.Go, layer, null, null, body, compositionParent,
                    new UiSurfacePresence(followsGameplay, gameplay), lifetime, logger, release);
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            /// <summary>Docks the HUD's panel through the process-wide placement, which stacks HUDs sharing a corner.</summary>
            public void AttachDock(UnityUiHudPlacement hudPlacement, int handle, TopiaForgeLabel title, string titleText)
            {
                placement = hudPlacement;
                dockHandle = handle;
                hudTitle = title;
                hudTitleText = titleText;
                ObserveLayout();
            }

            public void SetBody(string value)
            {
                UnityMainThreadGuard.AssertCurrent();
                var label = body ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (presence.IsLost)
                {
                    return;
                }

                var next = value ?? string.Empty;
                if (string.Equals(sentBody, next, StringComparison.Ordinal)) return;
                label.SetText(next);
                if (placement != null) label.SetShown(next.Length != 0 && layoutFallback == null);
                sentBody = next;
                bodyVersion++;
            }

            public OperationResult<bool> SetContent(UiNode content)
                => SetComposition(content, declaredTheme, remoteComposition, changingTheme: false);

            private OperationResult<bool> SetComposition(UiNode content, UiThemeDescriptor? descriptor, bool remote, bool changingTheme)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (content == null) throw new ArgumentNullException(nameof(content));
                var parent = compositionParent;
                var view = widget;
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod UI surface is stopping.");
                }

                if (presence.IsLost)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, LostMessage);
                }

                if (parent == null || view == null)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "The mod UI surface is disposed.");
                }

                TopiaForgeContainer? next = null;
                UiGraphRetentionTransaction? graphTransaction = null;
                UnityUiLayoutRenderer? nextLayout = null;
                ContentStamp stamp;
                try
                {
                    stamp = new ContentStamp(view, callbacks.ClockEpoch(), bodyVersion, placement?.DockVersion ?? 0, OuterScrollViewport);
                    var retained = ReferenceEquals(content, admitted);
                    if (retained && !changingTheme)
                    {
                        if (!committedStamp.Matches(stamp))
                        {
                            RefreshRetainedComposition(compositionRoot!, stamp.ClockEpoch);
                            committedStamp = stamp;
                        }
                        return ContentApplied;
                    }

                    UiComposition.Validate(content);
                    if (window == null && fullscreen == null && UiComposition.ContainsInteractive(content))
                        return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "HUD surfaces are presentation-only.");
                    if (!retained) UiClockBinding.Check(content, stamp.ClockEpoch);
                    next = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    next.SetShown(false);
                    if (changingTheme) PrepareStagedTheme(next, descriptor);
                    graphTransaction = new UiGraphRetentionTransaction(retainedGraphs);
                    nextLayout = UnityUiLayoutRenderer.Stage(content, next, MeasureContentArea(parent, view),
                        descriptor ?? (remote ? null : UiThemePackParser.Default), callbacks, graphTransaction);
                    graphTransaction.Transfer(next);
                    if (!retained) UiClockBinding.Commit(content, stamp.ClockEpoch);
                }
                catch (ArgumentException exception)
                {
                    var cleanup = RollbackFailedRender(graphTransaction, parent, next);
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        exception.Message + cleanup);
                }
                catch (Exception exception)
                {
                    var cleanup = RollbackFailedRender(graphTransaction, parent, next);
                    return OperationResult<bool>.Failure(
                        ModErrorCode.External,
                        "TopiaForgeUi could not render the composition: " + exception.Message + cleanup);
                }

                var previous = compositionRoot;
                compositionRoot = next;
                layoutRenderer = nextLayout;
                admitted = content;
                declaredTheme = descriptor;
                remoteComposition = remote;
                committedStamp = stamp;
                retainedGraphs = graphTransaction!.Commit();
                RemoveLayoutFallback();
                next!.SetShown(true);
                ObserveLayout();
                try { SyncGraphConnections(); }
                catch (Exception exception)
                {
                    try { logger.Error(exception, "TopiaForgeUi could not refresh the graph Connections controls."); }
                    catch { }
                }
                try
                {
                    previous?.Destroy();
                }
                catch (Exception exception)
                {
                    // Admission has committed. Retire the stale view without reporting a failed update.
                    try { previous?.SetShown(false); }
                    catch { }
                    try { logger.Error(exception, "TopiaForgeUi could not destroy a replaced composition."); }
                    catch { }
                }

                RestoreOuterScrollOffset();
                return ContentApplied;
            }

            partial void SyncGraphConnections();
            partial void ResetGraphConnections(bool retire);

            private void RefreshRetainedComposition(TopiaForgeContainer root, string clockEpoch)
            {
                UpdateRetainedLayout(root);
                LayoutRebuilder.MarkLayoutForRebuild(root.Rect);
                if (!string.Equals(committedStamp.ClockEpoch, clockEpoch, StringComparison.Ordinal))
                {
                    foreach (var countdown in root.Go.GetComponentsInChildren<UiCountdownDriver>(includeInactive: true))
                    {
                        countdown.Refresh();
                    }
                }
            }

            private readonly struct ContentStamp
            {
                private readonly TopiaForgeResolvedTheme theme;
                private readonly int screenWidth;
                private readonly int screenHeight;
                private readonly float width;
                private readonly float height;
                private readonly int bodyVersion;
                private readonly int dockVersion;
                private readonly Rect outerScrollViewport;

                internal ContentStamp(TopiaForgeWidget view, string clockEpoch, int bodyVersion, int dockVersion, Rect outerScrollViewport)
                {
                    ClockEpoch = clockEpoch;
                    this.bodyVersion = bodyVersion;
                    this.dockVersion = dockVersion;
                    this.outerScrollViewport = outerScrollViewport;
                    theme = view.ResolvedTheme;
                    screenWidth = Screen.width;
                    screenHeight = Screen.height;
                    var rect = view.Rect.rect;
                    width = rect.width;
                    height = rect.height;
                }

                internal string ClockEpoch { get; }

                internal bool Matches(in ContentStamp other) =>
                    string.Equals(ClockEpoch, other.ClockEpoch, StringComparison.Ordinal)
                    && ReferenceEquals(theme, other.theme)
                    && screenWidth == other.screenWidth && screenHeight == other.screenHeight
                    && width.Equals(other.width) && height.Equals(other.height) && bodyVersion == other.bodyVersion
                    && dockVersion == other.dockVersion
                    && outerScrollViewport.width.Equals(other.outerScrollViewport.width)
                    && outerScrollViewport.height.Equals(other.outerScrollViewport.height);
            }

            private static string RollbackFailedRender(
                UiGraphRetentionTransaction? transaction,
                TopiaForgeContainer fallbackParent,
                TopiaForgeContainer? failedRoot)
            {
                Exception? cleanupFailure = null;
                try
                {
                    cleanupFailure = transaction?.Rollback(fallbackParent);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                try
                {
                    failedRoot?.Destroy();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                return cleanupFailure == null
                    ? string.Empty
                    : " UI rollback also failed: " + cleanupFailure.Message;
            }
        }
    }
}
