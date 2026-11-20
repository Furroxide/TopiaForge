using System.Collections;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Paints a stationary indication when a selectable's state tints draw the same fill.</summary>
    internal sealed class TopiaForgeControlCues : MonoBehaviour, ISelectHandler, IDeselectHandler,
        IPointerDownHandler, IPointerUpHandler, ISubmitHandler
    {
        private Selectable selectable = null!;
        private RectTransform area = null!;
        private bool compact;
        private bool held;
        private bool authoredSelectionCue;
        private Coroutine? submit;
        private bool submitted;
        private UiButtonCue available;
        private Image? selection;
        private TopiaForgeRoundedImage? press;
        private Color ink;
        private float corner;
        private float stroke;

        internal bool Compact => compact;
        internal bool IsSelected { get; private set; }
        internal UiButtonCue ActiveCues { get; private set; }

        internal static TopiaForgeControlCues Install(Selectable control, RectTransform area, bool compact = false)
        {
            var driver = control.gameObject.AddComponent<TopiaForgeControlCues>();
            driver.selectable = control;
            driver.area = area;
            driver.compact = compact;
            return driver;
        }

        internal void Apply(Color stateFill, Color markInk, float radius, bool highContrast, bool authoredSelectionCue = false)
        {
            this.authoredSelectionCue = authoredSelectionCue;
            Color32 fill = stateFill;
            var colors = selectable.colors;
            var tints = new UiThemeStateTints(colors.highlightedColor.r, colors.selectedColor.r, colors.pressedColor.r);
            available = UiControlPaint.ButtonCues(new UiThemeColor(fill.r, fill.g, fill.b), tints);
            ink = markInk;
            corner = compact ? UiControlPaint.CompactCueRadius : Mathf.Max(0f, radius - UiControlPaint.ButtonPressInset);
            stroke = UiControlPaint.ButtonPressStroke(highContrast);
            if (selection != null) selection.color = ink;
            if (press != null) PaintPress(press);
            Refresh();
        }

        public void OnSelect(BaseEventData eventData)
        {
            IsSelected = true;
            Refresh();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            IsSelected = false;
            Refresh();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !selectable.IsInteractable()) return;
            held = true;
            Refresh();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            held = false;
            Refresh();
        }

        public void OnSubmit(BaseEventData eventData)
        {
            if (!selectable.IsInteractable() || (available & UiButtonCue.Press) == 0) return;
            if (submit != null) StopCoroutine(submit);
            submit = StartCoroutine(ShowSubmit());
        }

        private IEnumerator ShowSubmit()
        {
            submitted = true;
            Refresh();
            yield return new WaitForSecondsRealtime(0.1f);
            submit = null;
            submitted = false;
            Refresh();
        }

        private void OnDisable()
        {
            if (submit != null) StopCoroutine(submit);
            submit = null;
            held = false;
            submitted = false;
            authoredSelectionCue = false;
            IsSelected = false;
            Refresh();
        }

        private void Refresh()
        {
            var cues = UiButtonCue.None;
            if (selectable != null && selectable.IsInteractable())
            {
                if (IsSelected) cues |= UiButtonCue.Selection;
                if (held || submitted) cues |= UiButtonCue.Press;
                cues &= available;
                if (authoredSelectionCue) cues |= UiButtonCue.Selection;
            }

            if (cues == ActiveCues) return;
            ActiveCues = cues;
            if ((cues & UiButtonCue.Selection) != 0 && selection == null) selection = CreateSelection();
            if ((cues & UiButtonCue.Press) != 0 && press == null) press = CreatePress();
            if (selection != null) selection.gameObject.SetActive((cues & UiButtonCue.Selection) != 0);
            if (press != null) press.gameObject.SetActive((cues & UiButtonCue.Press) != 0);
        }

        private Image CreateSelection()
        {
            var mark = Create<Image>(TopiaForgeCueButton.SelectionName);
            mark.color = ink;
            var rect = mark.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(0f, 1f);
            var leading = compact ? UiControlPaint.CompactCueLeading : UiControlPaint.ButtonSelectionLeft;
            rect.offsetMin = new Vector2(leading, UiControlPaint.ButtonSelectionTop);
            rect.offsetMax = new Vector2(leading + UiControlPaint.ButtonSelectionWidth, -UiControlPaint.ButtonSelectionTop);
            return mark;
        }

        private TopiaForgeRoundedImage CreatePress()
        {
            var mark = Create<TopiaForgeRoundedImage>(TopiaForgeCueButton.PressName);
            var inset = UiControlPaint.ButtonPressInset;
            var rect = mark.rectTransform;
            if (compact)
            {
                var side = UiControlPaint.CompactCueSideInset;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = new Vector2(0f, 1f);
                rect.offsetMin = new Vector2(side, inset);
                rect.offsetMax = new Vector2(UiControlPaint.CompactCueGutter - side, -inset);
            }
            else TopiaForgeAnchors.Stretch(rect, inset, inset, inset, inset);
            PaintPress(mark);
            return mark;
        }

        private T Create<T>(string name) where T : Graphic
        {
            var mark = new GameObject(name, typeof(RectTransform));
            mark.transform.SetParent(area, false);
            mark.AddComponent<LayoutElement>().ignoreLayout = true;
            var graphic = mark.AddComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private void PaintPress(TopiaForgeRoundedImage mark)
        {
            mark.color = ink;
            mark.SetShape(corner, stroke);
        }
    }
}
