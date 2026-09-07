namespace TopiaForge.ModManager.Core
{
    /// <summary>The minimal validated identity needed to reject a malformed command without claiming success.</summary>
    public sealed class LaunchRequestCorrelation
    {
        internal LaunchRequestCorrelation(string requestId, string command)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Command = LaunchContractValues.Choice(command, nameof(command), LaunchContractValues.Commands);
        }
        public string RequestId { get; }
        public string Command { get; }
    }
    public static partial class LaunchTransportJson
    {
        internal static LaunchRequestCorrelation ReadCorrelation(string json)
        {
            var value = new Fields(Bounded(json, MaxDocumentBytes), allowed: null);
            value.Version(4);
            return new LaunchRequestCorrelation(value.String("requestId"), value.String("command"));
        }
    }
}
