using System;

namespace TopiaForge.Mods
{
    /// <summary>Host-only, scope-owned presentation of a participant, automatically replicated to everyone including late joiners.
    /// Changes do not rename the participant or affect health, collision, input or movement. Main thread only.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IPlayerAppearance
    {
        /// <summary>Reads the current effective appearance; false when the participant or presentation is unavailable.</summary>
        bool TryGet(out AvatarAppearance? appearance);
        /// <summary>Overrides plastic, metal and the selected accessory slot until the returned lease or owning scope ends.</summary>
        OperationResult<IPlayerAppearanceLease> Set(AvatarAppearance appearance);
        /// <summary>Snapshots another current participant's effective appearance in this directory. Does not copy identity,
        /// nameplate or pose. Disposing the lease restores the target's underlying appearance.</summary>
        OperationResult<IPlayerAppearanceLease> CopyFrom(IPlayer source);
        /// <summary>Sets literal nameplate text and visibility. Null text uses the participant's current display name.
        /// Text is at most 32 characters and single-line; the runtime redacts address-shaped text before publication. Dispose to restore the underlying nameplate.</summary>
        OperationResult<IPlayerAppearanceLease> SetNameplate(string? text = null, bool visible = true);
        /// <summary>Poses the cosmetic body powered-down until disposal; false overrides an underlying powered-down lease.</summary>
        OperationResult<IPlayerAppearanceLease> SetPoweredDown(bool poweredDown = true);
    }

    /// <summary>A reversible presentation change. Latest live lease wins independently for appearance, nameplate and pose.
    /// Out-of-order disposal cannot resurrect an ended change. The owning context tracks this lease automatically.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IPlayerAppearanceLease : IDisposable
    {
        /// <summary>Gets whether the change remains owned by a live participant and context.</summary>
        bool IsActive { get; }
    }
}
