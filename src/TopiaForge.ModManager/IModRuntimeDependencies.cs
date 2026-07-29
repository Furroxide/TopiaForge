using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Internal seam that keeps runtime lifecycle tests independent of a live Unity process.</summary>
    internal interface IRuntimeGameplayHost : IGameplayContextFactory, IDisposable
    {
        event Action<GameTimeSample>? FixedUpdate;
        event Action<GameTimeSample>? LateUpdate;

        GameTimeSample BeginFrame(float deltaTime);
    }

    /// <summary>Internal attributed logging seam used by the runtime and its synthetic-assembly tests.</summary>
    internal interface IModRuntimeLogger
    {
        IModLogger ForMod(string modId);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(Exception exception, string message);
    }
}
