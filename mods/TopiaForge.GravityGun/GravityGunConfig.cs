using System.Runtime.Serialization;

namespace TopiaForge.GravityGun
{
    [DataContract]
    public sealed class GravityGunConfig
    {
        public GravityGunConfig()
        {
            SeedDefaults();
        }

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
            MaxRange = Clamp(MaxRange, 1f, 100f, 20f);
            MinHoldDistance = Clamp(MinHoldDistance, 0.5f, 50f, 2f);
            MaxHoldDistance = Clamp(MaxHoldDistance, MinHoldDistance, 100f, MathMax(MinHoldDistance, 18f));
            DefaultHoldDistance = Clamp(DefaultHoldDistance, MinHoldDistance, MaxHoldDistance, 5f);
            ScrollStep = Clamp(ScrollStep, 0.1f, 10f, 1f);
            PullStrength = Clamp(PullStrength, 1f, 500f, 70f);
            Damping = Clamp(Damping, 0f, 100f, 12f);
            MaxVelocity = Clamp(MaxVelocity, 1f, 250f, 35f);
            ThrowVelocity = Clamp(ThrowVelocity, 1f, 250f, 22f);
            ParticleIntensity = Clamp(ParticleIntensity, 0f, 5f, 1f);
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            MaxRange = 20f;
            DefaultHoldDistance = 5f;
            MinHoldDistance = 2f;
            MaxHoldDistance = 18f;
            ScrollStep = 1f;
            PullStrength = 70f;
            Damping = 12f;
            MaxVelocity = 35f;
            ThrowVelocity = 22f;
            RequireCursorLocked = true;
            ParticleIntensity = 1f;
        }

        private static float Clamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = fallback;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static float MathMax(float first, float second)
        {
            return first > second ? first : second;
        }
    }
}
