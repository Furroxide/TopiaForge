using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A horizontal or vertical step rail for multi-phase flows such as joining a session
    /// (Resolving, Connecting, Authenticating, Verifying mods, Loading world, Synchronizing,
    /// Spawning). Each step is pending, active, done, failed or skipped, with an optional detail
    /// line and progress ("3/7"). Status reads without color: pending and active steps show their
    /// number, done a check, failed a cross, skipped a chevron. The active node breathes subtly
    /// unless motion is reduced. Transitions run through the unit-tested
    /// <see cref="TopiaForgePhaseStepperState"/> and repaint only when it changes.
    /// </summary>
    public sealed partial class TopiaForgePhaseStepper : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly TopiaForgePhaseStepperState state;
        private readonly StepView[] steps;
        private readonly bool vertical;
        private readonly TopiaForgePhaseStepperDriver driver;
        private int pulsingStep = -1;
        private float shownScale = -1f;
        private float shownAlpha = -1f;

        internal TopiaForgePhaseStepper(TopiaForgeContainer parent, IReadOnlyList<string> labels, bool verticalRail)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(verticalRail ? "PhaseRail" : "PhaseStepper"))
        {
            if (labels == null)
            {
                throw new ArgumentNullException(nameof(labels));
            }

            state = new TopiaForgePhaseStepperState(labels.Count);
            vertical = verticalRail;
            steps = new StepView[labels.Count];
            if (vertical)
            {
                BuildVertical(labels);
            }
            else
            {
                BuildHorizontal(labels);
            }

            driver = Go.AddComponent<TopiaForgePhaseStepperDriver>();
            driver.Stepper = this;
            driver.enabled = false;
            Repaint(Theme);
        }

        /// <summary>Gets the number of steps.</summary>
        public int Count => state.Count;

        /// <summary>Gets the active step, or -1.</summary>
        public int ActiveIndex => state.ActiveIndex;

        /// <summary>Gets the failed step, or -1.</summary>
        public int FailedIndex => state.FailedIndex;

        /// <summary>Gets whether every step is done or skipped.</summary>
        public bool IsComplete => state.IsComplete;

        /// <summary>Gets the status of a step.</summary>
        public TopiaForgeStepStatus StatusOf(int index)
        {
            return state.StatusOf(index);
        }

        /// <summary>Activates the first pending step.</summary>
        public bool Start()
        {
            return Commit(state.Start());
        }

        /// <summary>Finishes the active step and activates the next pending one.</summary>
        public bool Advance()
        {
            return Commit(state.Advance());
        }

        /// <summary>Jumps to a step: earlier ones finish, later ones return to pending.</summary>
        public bool MoveTo(int index)
        {
            return Commit(state.MoveTo(index));
        }

        /// <summary>Marks the active step failed.</summary>
        public bool Fail()
        {
            return Commit(state.Fail());
        }

        /// <summary>Marks a pending step skipped.</summary>
        public bool Skip(int index)
        {
            return Commit(state.Skip(index));
        }

        /// <summary>Finishes every remaining step.</summary>
        public bool Complete()
        {
            return Commit(state.Complete());
        }

        /// <summary>Returns every step to pending and clears details and progress.</summary>
        public bool Reset()
        {
            var changed = state.Reset();
            for (var index = 0; index < steps.Length; index++)
            {
                if (steps[index].Detail.Length > 0)
                {
                    steps[index].Detail = string.Empty;
                    changed = true;
                }

                WriteDetail(index);
            }

            return Commit(changed);
        }

        /// <summary>Dirty-checked detail line under a step ("Relay", "Retrying").</summary>
        public void SetDetail(int index, string? detail)
        {
            state.StatusOf(index);
            var value = detail ?? string.Empty;
            if (string.Equals(steps[index].Detail, value, StringComparison.Ordinal))
            {
                return;
            }

            steps[index].Detail = value;
            WriteDetail(index);
        }

        /// <summary>Dirty-checked progress shown in a step's detail line ("3/7"); a non-positive total clears it.</summary>
        public void SetProgress(int index, int current, int total)
        {
            if (state.SetProgress(index, current, total))
            {
                WriteDetail(index);
            }
        }

        /// <summary>Dirty-checked step name.</summary>
        public void SetLabel(int index, string label)
        {
            state.StatusOf(index);
            steps[index].Label.SetText(label ?? string.Empty);
        }

        /// <summary>Applies the resolved theme; accessibility changes also re-evaluate the animation.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            Repaint(theme);
        }

        internal void Tick()
        {
            if (pulsingStep < 0)
            {
                return;
            }

            TopiaForgeBreath.Evaluate(Time.unscaledTime, Host.EffectiveMotion, out var scale, out var alpha);
            var view = steps[pulsingStep];
            if (Mathf.Abs(scale - shownScale) > 0.0005f)
            {
                shownScale = scale;
                view.RingRect.localScale = new Vector3(scale, scale, 1f);
            }

            if (Mathf.Abs(alpha - shownAlpha) > 0.002f)
            {
                shownAlpha = alpha;
                var color = view.Ring.color;
                color.a = alpha;
                view.Ring.color = color;
            }
        }

        private bool Commit(bool changed)
        {
            if (changed)
            {
                Repaint(Theme);
            }

            return changed;
        }

        private void Repaint(TopiaForgeResolvedTheme theme)
        {
            for (var index = 0; index < steps.Length; index++)
            {
                PaintStep(index, state.StatusOf(index), theme);
                WriteDetail(index);
            }

            pulsingStep = state.ActiveIndex;
            shownScale = 1f;
            shownAlpha = TopiaForgeBreath.RestAlpha;
            var animate = pulsingStep >= 0 && Host.EffectiveMotion > 0f;
            if (driver.enabled != animate)
            {
                driver.enabled = animate;
            }
        }

        private void WriteDetail(int index)
        {
            var view = steps[index];
            var hasProgress = state.TryGetProgress(index, out var current, out var total);
            if (view.ShownCurrent == current && view.ShownTotal == total && ReferenceEquals(view.ShownDetail, view.Detail))
            {
                return;
            }

            view.ShownCurrent = current;
            view.ShownTotal = total;
            view.ShownDetail = view.Detail;
            string text;
            if (hasProgress)
            {
                text = view.Detail.Length > 0
                    ? view.Detail + "  " + current + "/" + total
                    : current + "/" + total;
            }
            else
            {
                text = view.Detail;
            }

            view.DetailLabel.SetText(text);
            var show = text.Length > 0 || !vertical;
            if (view.DetailLabel.Go.activeSelf != show)
            {
                view.DetailLabel.Go.SetActive(show);
            }
        }
    }

    /// <summary>Per-frame driver for the active step's breathing ring, enabled only while it breathes.</summary>
    internal sealed class TopiaForgePhaseStepperDriver : MonoBehaviour
    {
        public TopiaForgePhaseStepper? Stepper;

        private void Update()
        {
            Stepper?.Tick();
        }
    }
}
