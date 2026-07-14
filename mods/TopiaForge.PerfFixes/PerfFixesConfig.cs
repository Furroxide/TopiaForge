using System.Runtime.Serialization;

namespace TopiaForge.PerfFixes
{
    /// <summary>
    /// Config for the behavior-identical performance-fix mod. Every fix is a pure win (no visual or
    /// gameplay change) and defaults ON; each can be toggled off independently if a future game update
    /// makes one misbehave. Same defaults-seeding pattern as the rest of the codebase
    /// (<c>DataContractJsonSerializer</c> bypasses the constructor and initializers).
    /// </summary>
    [DataContract]
    public sealed class PerfFixesConfig
    {
        public PerfFixesConfig()
        {
            SeedDefaults();
        }

        /// <summary>Master switch. When false the mod loads but applies nothing.</summary>
        [DataMember(Name = "enabled")] public bool Enabled { get; set; } = true;

        /// <summary>Set <c>Physics.reuseCollisionCallbacks = true</c> to stop a per-collision allocation.</summary>
        [DataMember(Name = "reuse_collision_callbacks")] public bool ReuseCollisionCallbacks { get; set; } = true;

        /// <summary>Cache <c>Camera.main</c> per frame so the native tag-scan runs once, not per call site.</summary>
        [DataMember(Name = "camera_main_cache")] public bool CameraMainCache { get; set; } = true;

        /// <summary>Dispatch <c>CollisionEventProxy</c> callbacks from a pooled list (no array+iterator alloc).</summary>
        [DataMember(Name = "collision_proxy_pooled")] public bool CollisionProxyPooled { get; set; } = true;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            Enabled = true;
            ReuseCollisionCallbacks = true;
            CameraMainCache = true;
            CollisionProxyPooled = true;
        }
    }
}
