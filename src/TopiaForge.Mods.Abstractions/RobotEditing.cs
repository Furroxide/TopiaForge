using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Discovers robots that RobotKit can edit temporarily without expanding the ownership of
    /// <see cref="IRobotAgentService"/>.
    /// </summary>
    /// <remarks>
    /// This optional extension is published by RobotKit. Every edit is represented by a lease whose disposal
    /// restores the captured native state; the contract deliberately has no commit operation.
    /// </remarks>
    public interface IRobotSceneEditorService
    {
        /// <summary>Gets whether the verified native editing surface is available in the current scene.</summary>
        bool IsAvailable { get; }

        /// <summary>Gets the current bounded, deterministic set of editable robots.</summary>
        IReadOnlyList<IRobotEditTarget> Targets { get; }

        /// <summary>Resolves a RobotKit-managed agent to an editable scene target.</summary>
        bool TryResolve(IRobotAgent agent, out IRobotEditTarget? target);

        /// <summary>Begins an exclusive, lifetime-owned temporary edit.</summary>
        OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target);
    }

    /// <summary>An opaque robot selected for reversible scene editing.</summary>
    public interface IRobotEditTarget
    {
        /// <summary>Gets the process-local target id, valid only for the current scene.</summary>
        string Id { get; }

        /// <summary>Gets a user-facing robot name.</summary>
        string DisplayName { get; }

        /// <summary>Gets the scene name used by logical native-binding recipes.</summary>
        string SceneName { get; }

        /// <summary>Gets whether the native robot still exists.</summary>
        bool IsAlive { get; }

        /// <summary>Gets whether this robot existed before the current tool session.</summary>
        bool IsNativeSceneObject { get; }

        /// <summary>Tries to read the current complete transform.</summary>
        bool TryGetTransform(out TransformState transform);
    }

    /// <summary>A bounded personality draft used for both native autonomous behavior and creator conversations.</summary>
    public sealed class RobotPersonalityDraft
    {
        /// <summary>Creates a personality draft.</summary>
        public RobotPersonalityDraft(string displayName, string instructions, float temperature = 0.7f)
        {
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
            {
                throw new ArgumentException("A personality display name of at most 128 characters is required.", nameof(displayName));
            }

            if (string.IsNullOrWhiteSpace(instructions) || instructions.Length > 4096)
            {
                throw new ArgumentException("Personality instructions must contain 1-4096 characters.", nameof(instructions));
            }

            if (float.IsNaN(temperature) || float.IsInfinity(temperature) || temperature < 0f || temperature > 2f)
            {
                throw new ArgumentOutOfRangeException(nameof(temperature));
            }

            DisplayName = displayName;
            Instructions = instructions;
            Temperature = temperature;
        }

        /// <summary>Gets the editor-facing personality name.</summary>
        public string DisplayName { get; }

        /// <summary>Gets the bounded native/persona instruction text.</summary>
        public string Instructions { get; }

        /// <summary>Gets the requested sampling temperature.</summary>
        public float Temperature { get; }
    }

    /// <summary>An exclusive reversible edit over one live robot.</summary>
    public interface IRobotEditLease : IDisposable
    {
        /// <summary>Gets the edited target.</summary>
        IRobotEditTarget Target { get; }

        /// <summary>Gets whether the lease remains active.</summary>
        bool IsActive { get; }

        /// <summary>Previews a transform until the lease is restored or disposed.</summary>
        OperationResult<TransformState> PreviewTransform(TransformState transform);

        /// <summary>Previews a native brain mode until the lease is restored or disposed.</summary>
        OperationResult<bool> PreviewBrainMode(RobotBrainMode mode);

        /// <summary>Applies a temporary native autonomous personality.</summary>
        OperationResult<bool> PreviewPersonality(RobotPersonalityDraft personality);

        /// <summary>Restores every changed property. Repeated calls succeed with <c>false</c>.</summary>
        OperationResult<bool> Restore();
    }
}
