using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeSliderInput : Slider
    {
        internal bool UsePercentSteps { get; set; }

        public override void OnMove(AxisEventData eventData)
        {
            if (UsePercentSteps && IsActive() && IsInteractable())
            {
                var step = (maxValue - minValue) * 0.01f;
                if (eventData.moveDir == MoveDirection.Left && FindSelectableOnLeft() == null)
                {
                    value -= step;
                    return;
                }

                if (eventData.moveDir == MoveDirection.Right && FindSelectableOnRight() == null)
                {
                    value += step;
                    return;
                }
            }

            base.OnMove(eventData);
        }
    }
}
