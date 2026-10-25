using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the first TopiaForge release that supplies an SDK type or member.</summary>
    /// <remarks>The analyzer checks this metadata against the mod manifest's loader floor.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
        AttributeTargets.Enum | AttributeTargets.Delegate | AttributeTargets.Constructor |
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Field,
        AllowMultiple = false, Inherited = false)]
    public sealed class RequiresTopiaForgeAttribute : Attribute
    {
        /// <summary>Marks an API with the first release that supplies it, expressed as SemVer 2.</summary>
        public RequiresTopiaForgeAttribute(string version)
        {
            Version = version;
        }

        /// <summary>Gets the first TopiaForge release that supplies the marked API.</summary>
        public string Version { get; }
    }
}
