using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable fake UI surface.</summary>
    public sealed class FakeUiSurface : IUiSurface
    {
        private Action<FakeUiSurface>? release;
        private IDisposable? lifetimeLease;
        private readonly Dictionary<string, bool> toggleValues = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> sliderValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> dropdownValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> listSelections = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly List<string> callbackErrors = new List<string>();
        private ModErrorCode nextContentErrorCode;
        private string nextContentErrorMessage = string.Empty;

        internal FakeUiSurface(UiSurfaceRequest request, Action<FakeUiSurface> release)
        {
            Request = request;
            Body = request.Body;
            Content = request.Content;
            CaptureState(Content);
            this.release = release;
            IsVisible = true;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        internal void FailNextContentUpdate(ModErrorCode errorCode, string message)
        {
            nextContentErrorCode = errorCode;
            nextContentErrorMessage = message ?? string.Empty;
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

            if (nextContentErrorCode != ModErrorCode.None)
            {
                var errorCode = nextContentErrorCode;
                var message = nextContentErrorMessage;
                nextContentErrorCode = ModErrorCode.None;
                nextContentErrorMessage = string.Empty;
                return OperationResult<bool>.Failure(errorCode, message);
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
            System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
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
}
