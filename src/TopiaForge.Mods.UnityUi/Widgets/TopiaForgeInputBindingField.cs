using System;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeInputBindingField : TopiaForgeContainer, ITopiaForgeThemeAware
    {
        private const float LabelHeight = 22f;
        private const float StatusHeight = 46f;
        internal const float MinimumHeight = LabelHeight + TopiaForgeTokens.ControlHeight + StatusHeight + 2f * (int)TopiaForgeGap.Xs;

        private readonly TopiaForgeInputBindingState binding;
        private readonly TopiaForgeKeyCapture escape;
        private readonly string ownerPackageId;
        private readonly TopiaForgeLabel name;
        private readonly TopiaForgeContainer statusRow;
        private readonly TopiaForgeLabel status;
        private readonly TopiaForgeImage warning;
        private readonly TopiaForgeRoundedImage capFill;
        private readonly TopiaForgeRoundedImage capBorder;
        private UiPaintRole backdrop;
        private bool capturePushed;

        internal TopiaForgeInputBindingField(TopiaForgeContainer parent, UiKeyBindingField field, string ownerPackageId,
            Func<(IInputAction Action, string DisplayName)?> resolveLocal,
            Func<IInputAction, InputBinding, string?> findConflict, bool enabled = true, UiPaintRole backdrop = UiPaintRole.Surface)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Input Binding Field"))
        {
            this.ownerPackageId = ownerPackageId;
            this.backdrop = backdrop;
            binding = new TopiaForgeInputBindingState(field, resolveLocal, findConflict, enabled);
            Device = field.Device;
            escape = new TopiaForgeKeyCapture(CancelOnBack);
            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            this.FixedHeight(MinimumHeight);

            name = Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText().Ellipsis()
                .Tone(TopiaForgeTone.Muted).FixedHeight(LabelHeight);

            var row = Row().FixedHeight(TopiaForgeTokens.ControlHeight);
            var cap = row.CreateChild("Key cap").FillWidth().FixedHeight(TopiaForgeTokens.ControlHeight);
            capFill = cap.Go.AddComponent<TopiaForgeRoundedImage>();
            capFill.raycastTarget = false;
            capFill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            capFill.type = UImage.Type.Sliced;
            var border = cap.CreateChildGameObject("Key cap border");
            capBorder = border.AddComponent<TopiaForgeRoundedImage>();
            capBorder.raycastTarget = false;
            capBorder.type = UImage.Type.Sliced;
            TopiaForgeAnchors.Stretch(capBorder.rectTransform);
            KeyCap = cap.Label(string.Empty, TopiaForgeTextStyle.Label).PlainText().Ellipsis().AlignCenter()
                .Stretch(left: 6f, right: 6f);
            KeyCap.SetBackdrop(UiPaintRole.Sunken);
            RebindButton = row.Button("Rebind", () => BeginCapture(), TopiaForgeButtonStyle.Outline)
                .FixedHeight(TopiaForgeTokens.ControlHeight);

            statusRow = Row(TopiaForgeGap.Xs).FixedHeight(StatusHeight);
            warning = statusRow.Image("Binding warning").Icon(TopiaForgeIcon.Warning).Fixed(18f, 18f);
            status = statusRow.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText().AlignTopLeft()
                .FillWidth().FixedHeight(StatusHeight);
            status.Go.GetComponent<TextMeshProUGUI>().overflowMode = TextOverflowModes.Ellipsis;

            Go.AddComponent<TopiaForgeInputBindingFieldDriver>().Field = this;
            ApplyTheme(Theme);
            Paint();
        }

        internal string DisplayLabel => binding.DisplayLabel;
        internal string BindingText => binding.BindingText;
        internal string StatusText => binding.StatusText;
        internal TopiaForgeInputBindingFieldState State => binding.State;
        internal UiInputDevice Device { get; }
        internal TopiaForgeLabel NameLabel => name;
        internal TopiaForgeLabel StatusLabel => status;
        internal TopiaForgeLabel KeyCap { get; }
        internal TopiaForgeButton RebindButton { get; }
        internal UImage KeyCapFill => capFill;
        internal UImage KeyCapBorder => capBorder;
        internal TopiaForgeImage WarningIcon => warning;

        // An owning capture suspends the overlay's CanvasGroup. The listener keeps its own enabled state.
        internal bool CanReceiveCapture => capturePushed && State == TopiaForgeInputBindingFieldState.Listening
            && Visible && Go != null && Go.activeInHierarchy && RebindButton.Button.interactable
            && ReferenceEquals(TopiaForgeDismissStack.Top(), escape);

        internal void SetBackdrop(UiPaintRole value)
        {
            if (backdrop == value) return;
            backdrop = value;
            ApplyTheme(Theme);
        }

        internal void SetEnabled(bool enabled)
        {
            binding.SetEnabled(enabled);
            if (State != TopiaForgeInputBindingFieldState.Listening) EndCapture();
            Paint();
        }

        internal bool BeginCapture()
        {
            binding.Refresh();
            Paint();
            if (!Visible || !Go.activeInHierarchy || !RebindButton.Button.IsInteractable() || !binding.Begin()) return false;

            // Back uses the same dismiss ordering and held-press rules as the original key field.
            capturePushed = true;
            TopiaForgeDismissStack.Push(escape, TopiaForgeInputClaim.Keyboard, Go);
            if (!TopiaForgeInputOwnership.ReadyForCapture)
            {
                CaptureBackendStopped();
                return false;
            }
            if (!CanReceiveCapture)
            {
                CancelCapture();
                return false;
            }

            TopiaForgeBindingCaptureInput.Begin(this);
            Paint();
            return true;
        }

        internal void CancelCapture()
        {
            if (!capturePushed) return;
            binding.Cancel();
            EndCapture();
            Paint();
        }

        private void CancelOnBack()
        {
            binding.CancelOnBack();
            EndCapture();
            Paint();
        }

        internal bool AcceptCaptured(InputBinding next)
        {
            if (!CanReceiveCapture) return false;
            bool consumed = true;
            try
            {
                using (TopiaForgeIntent.PlayerAction(ownerPackageId)) consumed = binding.Accept(next);
            }
            catch (Exception exception)
            {
                binding.Fail("Could not rebind this action.");
                TopiaForgeLog.Error(exception, "A native key binding field could not apply the captured control.");
            }

            if (State != TopiaForgeInputBindingFieldState.Listening) EndCapture();
            Paint();
            return consumed;
        }

        internal void CaptureBackendStopped()
        {
            if (!capturePushed) return;
            binding.Fail("Input capture is currently unavailable.");
            EndCapture();
            Paint();
        }

        internal void Tick()
        {
            binding.Refresh();
            if (capturePushed && !CanReceiveCapture)
            {
                CancelCapture();
                return;
            }

            // The runtime has already offered this frame's Back to the dismiss stack.
            if (capturePushed && TopiaForgeBindingCaptureInput.TryTake(this, out var next)) AcceptCaptured(next);
            else Paint();
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            name.SetBackdrop(backdrop);
            status.SetBackdrop(backdrop);
            RebindButton.SetBackdrop(backdrop);
            warning.SetColor(theme.ForegroundOn(backdrop, UiTone.Warning));
            capFill.color = theme.SurfaceSunken;
            capBorder.color = theme.BoundaryOn(backdrop);
            capBorder.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip,
                theme.HighContrast ? TopiaForgeTokens.BorderStrong : TopiaForgeTokens.BorderStandard);
            var radius = theme.Descriptor?.Shape.Small;
            capFill.SetShape(radius);
            capBorder.SetShape(radius, theme.HighContrast ? TopiaForgeTokens.BorderStrong : TopiaForgeTokens.BorderStandard);
        }

        private void EndCapture()
        {
            if (!capturePushed) return;
            capturePushed = false;
            TopiaForgeBindingCaptureInput.End(this);
            TopiaForgeDismissStack.Remove(escape);
        }

        private void Paint()
        {
            var warn = State is TopiaForgeInputBindingFieldState.Conflict or TopiaForgeInputBindingFieldState.Reserved;
            name.SetText(DisplayLabel);
            KeyCap.SetText(BindingText);
            KeyCap.SetTone(binding.CanEdit ? TopiaForgeTone.Neutral : TopiaForgeTone.Muted);
            RebindButton.SetEnabled(binding.CanEdit);
            warning.SetShown(warn);
            status.SetText(StatusText);
            status.SetTone(warn ? TopiaForgeTone.Warning : TopiaForgeTone.Muted);
            statusRow.SetVisible(StatusText.Length != 0);
        }
    }

    internal sealed class TopiaForgeInputBindingFieldDriver : MonoBehaviour
    {
        internal TopiaForgeInputBindingField? Field;

        private void LateUpdate() => Field?.Tick();

        private void OnDisable() => Field?.CancelCapture();
    }
}
