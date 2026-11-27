using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using TopiaForge.Mods;

namespace TopiaForge.MultiplayerAcceptance
{
    [DataContract]
    internal sealed class AcceptanceConfig
    {
        [DataMember(Name = "challenge")] public string Challenge { get; set; } = string.Empty;
        [DataMember(Name = "peer")] public string Peer { get; set; } = string.Empty;
        [DataMember(Name = "topology")] public string Topology { get; set; } = string.Empty;
        [DataMember(Name = "checks")] public AcceptanceCheck[] Checks { get; set; } = Array.Empty<AcceptanceCheck>();

        internal static OperationResult<bool> Validate(AcceptanceConfig value)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (value == null || !Regex.IsMatch(value.Challenge ?? "", "\\A[0-9a-f]{64}\\z") ||
                (value.Peer != "host" && value.Peer != "guest") || value.Topology != "listen-pair" ||
                value.Checks == null || value.Checks.Length > 128) return Invalid();
            foreach (var check in value.Checks)
                if (check == null || !Regex.IsMatch(check.Id ?? "", "\\Amultiplayer\\.[a-z][a-z0-9-]{0,80}\\z") ||
                    !ids.Add(check.Id!) || (check.Mode != "automatic" && check.Mode != "manual") ||
                    string.IsNullOrWhiteSpace(check.Steps) || check.Steps.Length > 2048) return Invalid();
            return OperationResult<bool>.Success(true);
        }
        private static OperationResult<bool> Invalid() => OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
            "Acceptance configuration is invalid. Restart the reviewed listen-pair runner.");
    }

    [DataContract]
    internal sealed class AcceptanceCheck
    {
        [DataMember(Name = "id")] public string Id { get; set; } = string.Empty;
        [DataMember(Name = "mode")] public string Mode { get; set; } = string.Empty;
        [DataMember(Name = "steps")] public string Steps { get; set; } = string.Empty;
    }
}
