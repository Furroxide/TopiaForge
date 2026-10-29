using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    internal interface IInternalWorldViewBinding
    {
        string WorldId { get; }
        IInternalWorldViewRenderer Renderer { get; }
        bool TryNameAuthored(IEntity entity, out string scene, out string id);
        IEntity? ResolveAuthored(string scene, string id);
    }

    // Native drawing stays in Worlds. The runtime owns aggregation and caller authority.
    internal interface IInternalWorldViewRenderer : IDisposable
    {
        bool IsAlive { get; }
        bool FogAvailable { get; }
        bool EnvironmentAvailable { get; }
        IReadOnlyList<string> Presets { get; }
        event Action Stopped;
        bool HasPreset(string preset);
        OperationResult<bool> SetEnvironment(string? preset);
        OperationResult<bool> SetFog(VisionFog? fog);
        OperationResult<IDisposable> Hide(IEntity entity);
    }
}
