namespace Robotopia.Mods
{
    /// <summary>
    /// The "time moves only when you move" driver: ramps the world scale toward <c>1</c> while the player is
    /// moving/aiming/acting and eases it back toward a near-frozen floor when they hold still — the Superhot feel.
    /// Pure and Unity-free (the service samples the player and feeds the <see cref="TimeSignal"/>, and pairs this with
    /// <see cref="ITimeControlService.ExemptPlayer"/> so the player stays full-speed). Frame-rate independent: lerps
    /// use rate × unscaled delta. Pair the floor with <c>fixedDeltaTime</c> co-scaling for smooth native slow-mo.
    /// </summary>
    public sealed class SuperhotTimeDriver : ITimeDriver
    {
        private readonly float idleScale;
        private readonly float moveThreshold;
        private readonly float actionRate;
        private readonly float moveRate;
        private readonly float easeDownRate;

        /// <summary>Creates a Superhot driver.</summary>
        /// <param name="idleScale">World scale while perfectly still (a small floor, not true 0, so the sim reads as "almost frozen" and physics stays sane). Default 0.03.</param>
        /// <param name="moveThreshold">Player input magnitude (0..1) above which the world ramps to full speed. Default 0.05.</param>
        /// <param name="actionRate">Ramp-up rate (per second) when the player takes a discrete action (fires) — snap up fast. Default 14.</param>
        /// <param name="moveRate">Ramp-up rate when the player is merely moving/aiming. Default 9.</param>
        /// <param name="easeDownRate">Ramp-down rate when the player holds still — ease back slowly. Default 4.</param>
        public SuperhotTimeDriver(
            float idleScale = 0.03f,
            float moveThreshold = 0.05f,
            float actionRate = 14f,
            float moveRate = 9f,
            float easeDownRate = 4f)
        {
            this.idleScale = Clamp(idleScale, 0.001f, 1f);
            this.moveThreshold = moveThreshold;
            this.actionRate = actionRate;
            this.moveRate = moveRate;
            this.easeDownRate = easeDownRate;
        }

        /// <summary>The near-frozen world scale this driver eases toward when the player is still.</summary>
        public float IdleScale => idleScale;

        /// <inheritdoc/>
        public float ComputeScale(in TimeSignal signal)
        {
            var moving = signal.PlayerInputMagnitude > moveThreshold;
            var target = (signal.PlayerActing || moving) ? 1f : idleScale;

            // Asymmetric: snap up fast on action/move, ease down slowly when still.
            float rate;
            if (target > signal.CurrentScale)
            {
                rate = signal.PlayerActing ? actionRate : moveRate;
            }
            else
            {
                rate = easeDownRate;
            }

            var t = Clamp(rate * signal.ControlDeltaTime, 0f, 1f);
            var next = signal.CurrentScale + ((target - signal.CurrentScale) * t);
            return Clamp(next, idleScale, 1f);
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
