using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Captures audio playback requests and owns their fake handles.</summary>
    public sealed class FakeAudioService : IAudioService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeAudioPlayback> playbacks = new List<FakeAudioPlayback>();

        /// <summary>Creates a fake audio service.</summary>
        public FakeAudioService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets a stable error used to reject playback.</summary>
        public ModErrorCode PlayErrorCode { get; set; }

        /// <summary>Gets currently active playback handles.</summary>
        public IReadOnlyList<FakeAudioPlayback> ActivePlaybacks => playbacks.AsReadOnly();

        /// <inheritdoc/>
        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (PlayErrorCode != ModErrorCode.None)
            {
                return OperationResult<IAudioPlayback>.Failure(
                    PlayErrorCode,
                    "Audio playback was rejected by the fake service.");
            }

            var playback = new FakeAudioPlayback(request, value => playbacks.Remove(value));
            playbacks.Add(playback);
            return lifetime.TrackResult<IAudioPlayback>(
                playback,
                "The fake mod stopped before audio playback could start.");
        }
    }

    /// <summary>Inspectable fake audio playback.</summary>
    public sealed class FakeAudioPlayback : IAudioPlayback
    {
        private Action<FakeAudioPlayback>? release;

        internal FakeAudioPlayback(AudioPlayRequest request, Action<FakeAudioPlayback> release)
        {
            Request = request;
            this.release = release;
        }

        /// <summary>Gets the captured playback request.</summary>
        public AudioPlayRequest Request { get; }

        /// <inheritdoc/>
        public bool IsPlaying => release != null;

        /// <inheritdoc/>
        public void Stop() => Dispose();

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>Captures UI surfaces, modals, and toasts without rendering.</summary>
    public sealed class FakeUiService : IUiService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeUiSurface> surfaces = new List<FakeUiSurface>();
        private readonly List<FakeUiModal> modals = new List<FakeUiModal>();
        private readonly List<FakeToast> toasts = new List<FakeToast>();
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;

        /// <summary>Creates a fake UI service.</summary>
        public FakeUiService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets active fake UI surfaces.</summary>
        public IReadOnlyList<FakeUiSurface> Surfaces => surfaces.AsReadOnly();

        /// <summary>Gets open fake UI modals.</summary>
        public IReadOnlyList<FakeUiModal> Modals => modals.AsReadOnly();

        /// <summary>Gets every captured toast in display order.</summary>
        public IReadOnlyList<FakeToast> Toasts => toasts.AsReadOnly();

        /// <inheritdoc/>
        public UiAccessibilityPreferences Accessibility => accessibility;

        /// <inheritdoc/>
        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences)
        {
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            if (lifetime.IsStopping)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot change UI accessibility preferences.");
            }

            accessibility = preferences;
            return OperationResult<UiAccessibilityPreferences>.Success(accessibility);
        }

        /// <inheritdoc/>
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot create UI surfaces.");
            }

            foreach (var existing in surfaces)
            {
                if (string.Equals(existing.Id, request.Id, StringComparison.Ordinal))
                {
                    return OperationResult<IUiSurface>.Failure(
                        ModErrorCode.Conflict,
                        "A UI surface already uses id '" + request.Id + "'.");
                }
            }

            var surface = new FakeUiSurface(request, value => surfaces.Remove(value));
            surfaces.Add(surface);
            return lifetime.TrackResult<IUiSurface>(
                surface,
                "The fake mod stopped before the UI surface could be created.");
        }

        /// <inheritdoc/>
        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
        {
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
                    "The fake mod is stopping and cannot show a modal.");
            }

            var modal = new FakeUiModal(request, completed, value => modals.Remove(value));
            modals.Add(modal);
            return lifetime.TrackResult<IUiModal>(
                modal,
                "The fake mod stopped before the modal could be shown.");
        }

        /// <inheritdoc/>
        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A toast message is required.", nameof(message));
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
                    "The fake mod is stopping and cannot show a toast.");
            }

            toasts.Add(new FakeToast(message, tone));
            return OperationResult<bool>.Success(true);
        }
    }

    /// <summary>Inspectable fake UI surface.</summary>
    public sealed class FakeUiSurface : IUiSurface
    {
        private Action<FakeUiSurface>? release;
        private readonly Dictionary<string, bool> toggleValues = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> sliderValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> dropdownValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> listSelections = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly List<string> callbackErrors = new List<string>();

        internal FakeUiSurface(UiSurfaceRequest request, Action<FakeUiSurface> release)
        {
            Request = request;
            Body = request.Body;
            Content = request.Content;
            CaptureState(Content);
            this.release = release;
            IsVisible = true;
        }

        /// <summary>Gets the captured surface request.</summary>
        public UiSurfaceRequest Request { get; }

        /// <inheritdoc/>
        public string Id => Request.Id;

        /// <inheritdoc/>
        public bool IsVisible { get; private set; }

        /// <summary>Gets the current body text.</summary>
        public string Body { get; private set; }

        /// <summary>Gets the currently captured immutable composition tree.</summary>
        public UiNode? Content { get; private set; }

        /// <summary>Gets callback failures captured without interrupting later callback subscribers.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrors.AsReadOnly();

        /// <inheritdoc/>
        public void Show()
        {
            EnsureActive();
            IsVisible = true;
        }

        /// <inheritdoc/>
        public void Hide()
        {
            EnsureActive();
            IsVisible = false;
        }

        /// <inheritdoc/>
        public void SetBody(string body)
        {
            EnsureActive();
            Body = body ?? string.Empty;
        }

        /// <inheritdoc/>
        public OperationResult<bool> SetContent(UiNode content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (release == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake UI surface is disposed.");
            }

            try
            {
                UiComposition.Validate(content);
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            Content = content;
            toggleValues.Clear();
            sliderValues.Clear();
            textValues.Clear();
            dropdownValues.Clear();
            listSelections.Clear();
            CaptureState(content);
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Finds a captured node by stable control id.</summary>
        public bool TryFindNode(string id, out UiNode? node)
        {
            node = null;
            return release != null && TryFind(Content, id, out node);
        }

        /// <summary>Invokes one enabled button by id while isolating all callback subscribers.</summary>
        public OperationResult<bool> ActivateButton(string id)
        {
            if (!TryFindNode(id, out var node) || !(node is UiButton button)) return NotFound(id, "button");
            if (!button.Enabled) return Disabled(id);
            return Invoke(button.Activated, "button '" + id + "'");
        }

        /// <summary>Changes one enabled toggle and invokes its callback.</summary>
        public OperationResult<bool> ChangeToggle(string id, bool value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiToggle toggle)) return NotFound(id, "toggle");
            if (!toggle.Enabled) return Disabled(id);
            toggleValues[id] = value;
            return Invoke(toggle.Changed, value, "toggle '" + id + "'");
        }

        /// <summary>Changes one enabled slider to an in-range finite value and invokes its callback.</summary>
        public OperationResult<bool> ChangeSlider(string id, float value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiSlider slider)) return NotFound(id, "slider");
            if (!slider.Enabled) return Disabled(id);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < slider.Minimum || value > slider.Maximum)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake slider value is outside its range.");
            }

            sliderValues[id] = value;
            return Invoke(slider.Changed, value, "slider '" + id + "'");
        }

        /// <summary>Changes one enabled text input, applying its maximum length before callback delivery.</summary>
        public OperationResult<bool> ChangeText(string id, string value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiTextInput input)) return NotFound(id, "text input");
            if (!input.Enabled) return Disabled(id);
            var bounded = UiTextInput.Truncate(value ?? string.Empty, input.MaximumLength);
            textValues[id] = bounded;
            return Invoke(input.Changed, bounded, "text input '" + id + "'");
        }

        /// <summary>Selects one enabled dropdown choice by stable value and invokes its callback.</summary>
        public OperationResult<bool> ChangeDropdown(string id, string value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiDropdown dropdown)) return NotFound(id, "dropdown");
            if (!dropdown.Enabled) return Disabled(id);
            var found = false;
            foreach (var choice in dropdown.Choices)
            {
                if (string.Equals(choice.Value, value, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake dropdown value is not a choice.");
            dropdownValues[id] = value;
            return Invoke(dropdown.Changed, value, "dropdown '" + id + "'");
        }

        /// <summary>Selects one enabled virtual-list item by stable id and invokes its callback.</summary>
        public OperationResult<bool> SelectListItem(string id, string itemId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiVirtualList list)) return NotFound(id, "virtual list");
            if (!list.Enabled) return Disabled(id);
            var found = false;
            foreach (var item in list.Items)
            {
                if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake list item was not found.");
            listSelections[id] = itemId;
            return Invoke(list.Selected, itemId, "virtual list '" + id + "'");
        }

        /// <summary>Tries to read the fake's current toggle value.</summary>
        public bool TryGetToggleValue(string id, out bool value) => toggleValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current slider value.</summary>
        public bool TryGetSliderValue(string id, out float value) => sliderValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current text-input value.</summary>
        public bool TryGetTextValue(string id, out string? value) => textValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current dropdown value.</summary>
        public bool TryGetDropdownValue(string id, out string? value) => dropdownValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current virtual-list selection.</summary>
        public bool TryGetSelectedListItem(string id, out string? itemId) => listSelections.TryGetValue(id, out itemId);

        /// <inheritdoc/>
        public void Dispose()
        {
            IsVisible = false;
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }

        private void CaptureState(UiNode? node)
        {
            if (node == null) return;
            if (node is UiToggle toggle) toggleValues[toggle.Id!] = toggle.Value;
            else if (node is UiSlider slider) sliderValues[slider.Id!] = slider.Value;
            else if (node is UiTextInput input) textValues[input.Id!] = input.Value;
            else if (node is UiDropdown dropdown) dropdownValues[dropdown.Id!] = dropdown.SelectedValue;
            else if (node is UiVirtualList list) listSelections[list.Id!] = list.SelectedItemId;

            if (node is UiLayoutNode layout)
            {
                foreach (var child in layout.Children) CaptureState(child);
            }
            else if (node is UiScroll scroll)
            {
                CaptureState(scroll.Content);
            }
        }

        private static bool TryFind(UiNode? current, string id, out UiNode? node)
        {
            node = null;
            if (current == null || string.IsNullOrWhiteSpace(id)) return false;
            if (string.Equals(current.Id, id, StringComparison.Ordinal))
            {
                node = current;
                return true;
            }

            if (current is UiLayoutNode layout)
            {
                foreach (var child in layout.Children)
                {
                    if (TryFind(child, id, out node)) return true;
                }
            }
            else if (current is UiScroll scroll && TryFind(scroll.Content, id, out node))
            {
                return true;
            }

            return false;
        }

        private OperationResult<bool> Invoke(Action callback, string description)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private OperationResult<bool> Invoke<T>(Action<T> callback, T value, string description)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action<T>)subscriber)(value); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private static OperationResult<bool> CallbackResult(int failures) => failures == 0
            ? OperationResult<bool>.Success(true)
            : OperationResult<bool>.Failure(ModErrorCode.External, failures + " fake UI callback subscriber(s) failed.");

        private static OperationResult<bool> NotFound(string id, string kind) =>
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "No " + kind + " uses id '" + id + "'.");

        private static OperationResult<bool> Disabled(string id) =>
            OperationResult<bool>.Failure(ModErrorCode.InvalidState, "UI control '" + id + "' is disabled.");

        private void EnsureActive()
        {
            if (release == null)
            {
                throw new ObjectDisposedException(nameof(FakeUiSurface));
            }
        }
    }

    /// <summary>Inspectable modal whose result is completed explicitly by a test.</summary>
    public sealed class FakeUiModal : IUiModal
    {
        private Action<bool>? completed;
        private Action<FakeUiModal>? release;
        private readonly List<string> callbackErrors = new List<string>();

        internal FakeUiModal(
            UiModalRequest request,
            Action<bool> completed,
            Action<FakeUiModal> release)
        {
            Request = request;
            this.completed = completed;
            this.release = release;
        }

        /// <summary>Gets the captured modal request.</summary>
        public UiModalRequest Request { get; }

        /// <inheritdoc/>
        public bool IsOpen => release != null;

        /// <summary>Gets isolated completion-callback failures in invocation order.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrors.AsReadOnly();

        /// <summary>Closes the modal and reports confirmation.</summary>
        public void Confirm() => Complete(true);

        /// <inheritdoc/>
        public void Close() => Complete(false);

        /// <inheritdoc/>
        public void Dispose() => Complete(false);

        private void Complete(bool confirmed)
        {
            var callback = completed;
            completed = null;
            var releaseCallback = release;
            release = null;
            releaseCallback?.Invoke(this);
            if (callback == null) return;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action<bool>)subscriber)(confirmed); }
                catch (Exception exception)
                {
                    callbackErrors.Add("modal completion callback failed: " + exception.Message);
                }
            }
        }
    }

    /// <summary>Represents a captured toast.</summary>
    public sealed class FakeToast
    {
        /// <summary>Creates captured toast data.</summary>
        public FakeToast(string message, UiTone tone)
        {
            Message = message;
            Tone = tone;
        }

        /// <summary>Gets the display message.</summary>
        public string Message { get; }

        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
    }
}
