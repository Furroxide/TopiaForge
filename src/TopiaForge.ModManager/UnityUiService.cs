using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerUiService : IUiService
    {
        private readonly IModLifetime lifetime;
        private readonly IModLogger logger;
        private readonly UiHost host;
        private readonly HashSet<string> surfaceIds = new HashSet<string>(StringComparer.Ordinal);
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;

        public OwnerUiService(
            string ownerModId,
            string dataPath,
            IModLifetime lifetime,
            IModLogger logger)
        {
            UnityMainThreadGuard.AssertCurrent();
            this.lifetime = lifetime;
            this.logger = logger;
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions
            {
                OwnerId = ownerModId,
                DataDirectory = dataPath,
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = logger.Error
            });
            lifetime.Track(host);
        }

        public UiAccessibilityPreferences Accessibility => accessibility;

        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            if (lifetime.IsStopping)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot change UI accessibility preferences.");
            }

            try
            {
                host.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                    preferences.HighContrast,
                    preferences.UiScale,
                    preferences.ReducedMotion,
                    preferences.MotionIntensity));
                accessibility = preferences;
                return OperationResult<UiAccessibilityPreferences>.Success(preferences);
            }
            catch (Exception exception)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not apply accessibility preferences: " + exception.Message);
            }
        }

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot create UI surfaces.");
            }

            if (!surfaceIds.Add(request.Id))
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Conflict,
                    "A UI surface already uses id '" + request.Id + "'.");
            }

            try
            {
                UnityUiSurface surface;
                if (request.Kind == UiSurfaceKind.Window)
                {
                    var window = host.Window(
                        request.Id,
                        request.Title,
                        request.Width,
                        request.Height,
                        TopiaForgeScheme.Paper);
                    var scroll = window.Content.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.Md);
                    var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                    surface = UnityUiSurface.ForWindow(
                        request.Id,
                        window,
                        body,
                        scroll.Content,
                        lifetime,
                        logger,
                        ReleaseSurfaceId);
                }
                else
                {
                    var layer = host.HudLayer(request.Id);
                    var panel = layer.Panel(TopiaForgePanelStyle.HudPanel)
                        .Dock(TopiaForgeCorner.TopLeft)
                        .Size(request.Width, request.Height);
                    var column = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
                    column.Label(request.Title, TopiaForgeTextStyle.Heading);
                    var scroll = column.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                    surface = UnityUiSurface.ForWidget(
                        request.Id,
                        panel,
                        body,
                        scroll.Content,
                        lifetime,
                        logger,
                        ReleaseSurfaceId);
                }

                if (request.Content != null)
                {
                    var contentResult = surface.SetContent(request.Content);
                    if (!contentResult.Succeeded)
                    {
                        surface.Dispose();
                        return OperationResult<IUiSurface>.Failure(
                            contentResult.ErrorCode,
                            contentResult.ErrorMessage);
                    }
                }

                lifetime.Track(surface);
                surface.Show();
                return OperationResult<IUiSurface>.Success(surface);
            }
            catch (Exception exception)
            {
                surfaceIds.Remove(request.Id);
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not create the surface: " + exception.Message);
            }
        }

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (completed == null)
            {
                throw new ArgumentNullException(nameof(completed));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot show a modal.");
            }

            try
            {
                var modal = host.Modal.Custom(request.Title, TopiaForgeScheme.Paper, 520f);
                var state = new UnityUiModal(modal, completed, logger);
                modal.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                var row = modal.Content.Row(TopiaForgeGap.Sm);
                row.Spacer();
                row.Button(request.CancelLabel, state.Cancel, TopiaForgeButtonStyle.Ghost);
                row.Button(
                    request.ConfirmLabel,
                    state.Confirm,
                    request.Destructive ? TopiaForgeButtonStyle.Danger : TopiaForgeButtonStyle.Filled);
                modal.Closed += state.HandleNativeClosed;
                lifetime.Track(state);
                modal.Show();
                return OperationResult<IUiModal>.Success(state);
            }
            catch (Exception exception)
            {
                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not show the modal: " + exception.Message);
            }
        }

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (string.IsNullOrWhiteSpace(message))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A toast message is required.");
            }

            if (!Enum.IsDefined(typeof(UiTone), tone))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The toast tone is not recognized.");
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot show a toast.");
            }

            try
            {
                host.Toast(message, ToNativeTone(tone));
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not show the toast: " + exception.Message);
            }
        }

        private static TopiaForgeTone ToNativeTone(UiTone tone)
        {
            switch (tone)
            {
                case UiTone.Success:
                    return TopiaForgeTone.Success;
                case UiTone.Warning:
                    return TopiaForgeTone.Warning;
                case UiTone.Danger:
                    return TopiaForgeTone.Danger;
                default:
                    return TopiaForgeTone.Neutral;
            }
        }

        private void ReleaseSurfaceId(string id) => surfaceIds.Remove(id);

        private sealed class UnityUiSurface : IUiSurface
        {
            private TopiaForgeWidget? widget;
            private TopiaForgeWindow? window;
            private TopiaForgeLabel? body;
            private TopiaForgeContainer? compositionParent;
            private TopiaForgeContainer? compositionRoot;
            private readonly IModLifetime lifetime;
            private readonly UiCallbackGate callbacks;
            private Action<string>? releaseId;
            private int visible;

            private UnityUiSurface(
                string id,
                TopiaForgeWidget widget,
                TopiaForgeWindow? window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                Id = id;
                this.widget = widget;
                this.window = window;
                this.body = body;
                this.compositionParent = compositionParent;
                this.lifetime = lifetime;
                this.releaseId = releaseId;
                callbacks = new UiCallbackGate(lifetime, logger);
            }

            public string Id { get; }
            public bool IsVisible => Volatile.Read(ref visible) != 0 && widget != null;

            public static UnityUiSurface ForWindow(
                string id,
                TopiaForgeWindow window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(id, window, window, body, compositionParent, lifetime, logger, releaseId);
            }

            public static UnityUiSurface ForWidget(
                string id,
                TopiaForgeWidget widget,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(id, widget, null, body, compositionParent, lifetime, logger, releaseId);
            }

            public void Show()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = widget ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (Interlocked.Exchange(ref visible, 1) != 0)
                {
                    return;
                }

                if (window != null)
                {
                    window.Show();
                }
                else
                {
                    current.SetVisible(true);
                }
            }

            public void Hide()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = widget ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (Interlocked.Exchange(ref visible, 0) == 0)
                {
                    return;
                }

                if (window != null)
                {
                    window.Close();
                }
                else
                {
                    current.SetVisible(false);
                }
            }

            public void SetBody(string value)
            {
                UnityMainThreadGuard.AssertCurrent();
                var label = body ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                label.SetText(value ?? string.Empty);
            }

            public OperationResult<bool> SetContent(UiNode content)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (content == null) throw new ArgumentNullException(nameof(content));
                var parent = compositionParent;
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod UI surface is stopping.");
                }

                if (parent == null || widget == null)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "The mod UI surface is disposed.");
                }

                TopiaForgeContainer? next = null;
                try
                {
                    UiComposition.Validate(content);
                    next = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    RenderNode(content, next, callbacks);
                    var previous = compositionRoot;
                    compositionRoot = next;
                    previous?.Destroy();
                    return OperationResult<bool>.Success(true);
                }
                catch (ArgumentException exception)
                {
                    next?.Destroy();
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
                }
                catch (Exception exception)
                {
                    next?.Destroy();
                    return OperationResult<bool>.Failure(
                        ModErrorCode.External,
                        "TopiaForgeUi could not render the composition: " + exception.Message);
                }
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Interlocked.Exchange(ref visible, 0);
                callbacks.Close();
                compositionRoot = null;
                compositionParent = null;
                body = null;
                window = null;
                Interlocked.Exchange(ref widget, null)?.Destroy();
                Interlocked.Exchange(ref releaseId, null)?.Invoke(Id);
            }
        }

        private static void RenderNode(UiNode node, TopiaForgeContainer parent, UiCallbackGate callbacks)
        {
            if (node is UiText text)
            {
                parent.Label(text.Text, ToNativeTextStyle(text.Style)).Tone(ToNativeTone(text.Tone));
                return;
            }

            if (node is UiColumn column)
            {
                var container = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in column.Children) RenderNode(child, container, callbacks);
                return;
            }

            if (node is UiRow row)
            {
                var container = parent.Row(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in row.Children) RenderNode(child, container, callbacks);
                return;
            }

            if (node is UiScroll scrollNode)
            {
                var scroll = parent.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None)
                    .FixedHeight(scrollNode.Height);
                RenderNode(scrollNode.Content, scroll.Content, callbacks);
                return;
            }

            if (node is UiButton button)
            {
                var native = parent.Button(
                    button.Label,
                    callbacks.Wrap(button.Activated, "button '" + button.Id + "'"),
                    ToNativeButtonStyle(button.Style));
                native.SetEnabled(button.Enabled);
                return;
            }

            if (node is UiToggle toggle)
            {
                var native = parent.Toggle(
                    toggle.Label,
                    toggle.Value,
                    callbacks.Wrap(toggle.Changed, "toggle '" + toggle.Id + "'"));
                native.SetEnabled(toggle.Enabled);
                return;
            }

            if (node is UiSlider slider)
            {
                var native = parent.Slider(
                    slider.Label,
                    slider.Minimum,
                    slider.Maximum,
                    slider.Value,
                    callbacks.Wrap(slider.Changed, "slider '" + slider.Id + "'"));
                native.SetEnabled(slider.Enabled);
                return;
            }

            if (node is UiTextInput input)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                container.Label(input.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                var native = container.Input(
                    input.Placeholder,
                    input.Value,
                    callbacks.Wrap(input.Changed, "text input '" + input.Id + "'"));
                native.SetCharacterLimit(input.MaximumLength);
                native.SetEnabled(input.Enabled);
                return;
            }

            if (node is UiDropdown dropdown)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                container.Label(dropdown.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                var labels = dropdown.Choices.Select(choice => choice.Label).ToArray();
                var selectedIndex = 0;
                for (var index = 0; index < dropdown.Choices.Count; index++)
                {
                    if (string.Equals(dropdown.Choices[index].Value, dropdown.SelectedValue, StringComparison.Ordinal))
                    {
                        selectedIndex = index;
                        break;
                    }
                }

                var native = container.Dropdown(labels, selectedIndex, index =>
                {
                    if (index >= 0 && index < dropdown.Choices.Count)
                    {
                        callbacks.Invoke(
                            dropdown.Changed,
                            dropdown.Choices[index].Value,
                            "dropdown '" + dropdown.Id + "'");
                    }
                });
                native.SetEnabled(dropdown.Enabled);
                return;
            }

            if (node is UiVirtualList list)
            {
                var native = parent.ListView<UiListItem>()
                    .FixedHeight(list.VisibleRows * (TopiaForgeTokens.ListRowHeight + 4f));
                native.Bind((row, item, _) =>
                {
                    row.Title.SetText(item.Title);
                    row.Subtitle.SetText(item.Subtitle);
                    row.Badge.Set(item.Badge, TopiaForgeTone.Neutral);
                });
                native.OnSelected(index =>
                {
                    if (list.Enabled && index >= 0 && index < list.Items.Count)
                    {
                        callbacks.Invoke(list.Selected, list.Items[index].Id, "virtual list '" + list.Id + "'");
                    }
                });
                native.SetItems(list.Items);
                native.SetEnabled(list.Enabled);
                if (list.SelectedItemId != null)
                {
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        if (string.Equals(list.Items[index].Id, list.SelectedItemId, StringComparison.Ordinal))
                        {
                            native.SetSelectedIndex(index);
                            break;
                        }
                    }
                }

                return;
            }

            throw new NotSupportedException("Unsupported safe UI node type: " + node.GetType().FullName + ".");
        }

        private static TopiaForgeTextStyle ToNativeTextStyle(UiTextStyle style)
        {
            switch (style)
            {
                case UiTextStyle.Heading:
                    return TopiaForgeTextStyle.Heading;
                case UiTextStyle.Caption:
                    return TopiaForgeTextStyle.Caption;
                default:
                    return TopiaForgeTextStyle.Body;
            }
        }

        private static TopiaForgeButtonStyle ToNativeButtonStyle(UiButtonStyle style)
        {
            switch (style)
            {
                case UiButtonStyle.Secondary:
                    return TopiaForgeButtonStyle.Outline;
                case UiButtonStyle.Ghost:
                    return TopiaForgeButtonStyle.Ghost;
                case UiButtonStyle.Danger:
                    return TopiaForgeButtonStyle.Danger;
                default:
                    return TopiaForgeButtonStyle.Filled;
            }
        }

        private sealed class UiCallbackGate
        {
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;
            private int active = 1;

            public UiCallbackGate(IModLifetime lifetime, IModLogger logger)
            {
                this.lifetime = lifetime;
                this.logger = logger;
            }

            public Action Wrap(Action callback, string description) => () => Invoke(callback, description);
            public Action<T> Wrap<T>(Action<T> callback, string description) => value => Invoke(callback, value, description);

            public void Invoke(Action callback, string description)
            {
                if (!CanInvoke()) return;
                foreach (var subscriber in callback.GetInvocationList())
                {
                    try { ((Action)subscriber)(); }
                    catch (Exception exception) { Report(description, exception); }
                }
            }

            public void Invoke<T>(Action<T> callback, T value, string description)
            {
                if (!CanInvoke()) return;
                foreach (var subscriber in callback.GetInvocationList())
                {
                    try { ((Action<T>)subscriber)(value); }
                    catch (Exception exception) { Report(description, exception); }
                }
            }

            public void Close() => Interlocked.Exchange(ref active, 0);

            private bool CanInvoke() => Volatile.Read(ref active) != 0 && !lifetime.IsStopping;

            private void Report(string description, Exception exception)
            {
                try { logger.Error(exception, "A mod UI " + description + " callback failed."); }
                catch { }
            }
        }

        private sealed class UnityUiModal : IUiModal
        {
            private TopiaForgeModalInstance? modal;
            private Action<bool>? completed;
            private readonly IModLogger logger;
            private int finished;

            public UnityUiModal(
                TopiaForgeModalInstance modal,
                Action<bool> completed,
                IModLogger logger)
            {
                this.modal = modal;
                this.completed = completed;
                this.logger = logger;
            }

            public bool IsOpen => Volatile.Read(ref finished) == 0 && modal != null;

            public void Confirm()
            {
                Complete(true);
            }

            public void Cancel()
            {
                Complete(false);
            }

            public void HandleNativeClosed()
            {
                Complete(false, closeNative: false);
            }

            public void Close()
            {
                UnityMainThreadGuard.AssertCurrent();
                Cancel();
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Complete(false);
            }

            private void Complete(bool confirmed, bool closeNative = true)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0)
                {
                    return;
                }

                var currentModal = Interlocked.Exchange(ref modal, null);
                if (currentModal != null)
                {
                    currentModal.Closed -= HandleNativeClosed;
                    if (closeNative)
                    {
                        currentModal.Close();
                    }
                }

                var callback = Interlocked.Exchange(ref completed, null);
                if (callback == null)
                {
                    return;
                }

                foreach (var subscriber in callback.GetInvocationList())
                {
                    try
                    {
                        ((Action<bool>)subscriber)(confirmed);
                    }
                    catch (Exception exception)
                    {
                        try { logger.Error(exception, "A mod UI modal completion callback failed."); }
                        catch { }
                    }
                }
            }
        }
    }
}
