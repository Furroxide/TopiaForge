using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>The session's world view, provided by Worlds. Single-player changes need session authority.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IWorldView : IViewControls
    {
        /// <summary>Gets whether the current world supports view changes.</summary>
        [RequiresTopiaForge("0.2.0")]
        bool IsAvailable { get; }
        /// <summary>Gets the bounded preset keys discovered in the running game; empty if unavailable.</summary>
        [RequiresTopiaForge("0.2.0")]
        IReadOnlyList<string> EnvironmentPresets { get; }
    }

    /// <summary>Resolves the world's view through the consuming session's own context.</summary>
    [RequiresTopiaForge("0.2.0")]
    public static class WorldViewExtensions
    {
        /// <summary>Gets the Worlds view provider bound to this session's context and lifetime.</summary>
        [RequiresTopiaForge("0.2.0")]
        public static IWorldView View(this IGamemodeSession session) =>
            (session ?? throw new ArgumentNullException(nameof(session))).Context.RequireExtension<IWorldView>();
        /// <summary>Hides the live object of an authored entity while the returned lease lasts.</summary>
        [RequiresTopiaForge("0.2.0")]
        public static OperationResult<IViewLease> Hide(this IViewControls view, AuthoredEntity entity)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            return entity == null ? OperationResult<IViewLease>.Failure(ModErrorCode.InvalidArgument, "An authored entity is required.")
                : view.Hide(entity.Entity);
        }
    }
}
