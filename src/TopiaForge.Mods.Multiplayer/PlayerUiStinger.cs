namespace TopiaForge.Mods
{
    public partial interface IPlayerUi
    {
        /// <summary>Shows a centre announcement to this player only.</summary>
        /// <param name="request">The immutable announcement request.</param>
        /// <returns>Success when admitted or coalesced, or an error describing why it could not be shown.</returns>
        [RequiresTopiaForge("0.2.0")]
        OperationResult<bool> ShowStinger(UiStingerRequest request) => UnavailablePlayerUiStinger.Result;
    }

    internal static class UnavailablePlayerUiStinger
    {
        internal static readonly OperationResult<bool> Result = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
            "This player UI does not provide stingers. Update its implementation.");
    }
}
