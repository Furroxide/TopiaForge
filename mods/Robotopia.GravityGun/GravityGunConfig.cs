using System.Runtime.Serialization;

namespace Robotopia.GravityGun
{
    [DataContract]
    public sealed class GravityGunConfig
    {
        [DataMember(Name = "maxRange")]
        public float MaxRange { get; set; } = 20f;

        [DataMember(Name = "defaultHoldDistance")]
        public float DefaultHoldDistance { get; set; } = 5f;

        [DataMember(Name = "minHoldDistance")]
        public float MinHoldDistance { get; set; } = 2f;

        [DataMember(Name = "maxHoldDistance")]
        public float MaxHoldDistance { get; set; } = 18f;

        [DataMember(Name = "scrollStep")]
        public float ScrollStep { get; set; } = 1f;

        [DataMember(Name = "pullStrength")]
        public float PullStrength { get; set; } = 70f;

        [DataMember(Name = "damping")]
        public float Damping { get; set; } = 12f;

        [DataMember(Name = "maxVelocity")]
        public float MaxVelocity { get; set; } = 35f;

        [DataMember(Name = "throwVelocity")]
        public float ThrowVelocity { get; set; } = 22f;

        [DataMember(Name = "requireCursorLocked")]
        public bool RequireCursorLocked { get; set; } = true;

        [DataMember(Name = "particleIntensity")]
        public float ParticleIntensity { get; set; } = 1f;

        public void Normalize()
        {
            MaxRange = Clamp(MaxRange, 1f, 100f);
            MinHoldDistance = Clamp(MinHoldDistance, 0.5f, 50f);
            MaxHoldDistance = Clamp(MaxHoldDistance, MinHoldDistance, 100f);
            DefaultHoldDistance = Clamp(DefaultHoldDistance, MinHoldDistance, MaxHoldDistance);
            ScrollStep = Clamp(ScrollStep, 0.1f, 10f);
            PullStrength = Clamp(PullStrength, 1f, 500f);
            Damping = Clamp(Damping, 0f, 100f);
            MaxVelocity = Clamp(MaxVelocity, 1f, 250f);
            ThrowVelocity = Clamp(ThrowVelocity, 1f, 250f);
            ParticleIntensity = Clamp(ParticleIntensity, 0f, 5f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
