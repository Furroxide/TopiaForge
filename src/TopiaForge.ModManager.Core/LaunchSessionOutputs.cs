using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Progress V2's <c>session</c> block: the session a V5 launch hosts or joins. It names no address and no port.</summary>
    public sealed class LaunchProgressSession
    {
        public LaunchProgressSession(string role, string intentId, string state, string? serverId = null, int? players = null,
            int? maxPlayers = null, string? inviteCode = null)
        {
            Role = LaunchSessionValues.Choice(role, nameof(role), LaunchContractValues.SessionRoles);
            IntentId = LaunchContractValues.Token(intentId, nameof(intentId));
            State = LaunchSessionValues.Choice(state, nameof(state), Role == "host" ? LaunchContractValues.HostStates : LaunchContractValues.JoinStates);
            ServerId = serverId == null ? null : LaunchSessionValues.Sha256(serverId, nameof(serverId));
            MaxPlayers = maxPlayers == null ? (int?)null
                : LaunchSessionValues.Ranged(maxPlayers.Value, nameof(maxPlayers), LaunchSessionValues.MinimumPlayers, LaunchSessionValues.MaximumPlayers);
            Players = players == null ? (int?)null : LaunchSessionValues.Ranged(players.Value, nameof(players), 0, LaunchSessionValues.MaximumPlayers);
            InviteCode = inviteCode == null ? null : LaunchSessionValues.InviteCode(inviteCode, nameof(inviteCode));
            if (Players.HasValue != MaxPlayers.HasValue || Players > MaxPlayers || (InviteCode != null && Role != "host"))
                throw new ArgumentException("Inconsistent session progress.");
        }

        public string Role { get; }
        public string IntentId { get; }
        public string State { get; }
        public string? ServerId { get; }
        public int? Players { get; }
        public int? MaxPlayers { get; }

        /// <summary>Gets a host's invite code, once the control plane issued one.</summary>
        public string? InviteCode { get; }
    }

    /// <summary>The host's admission report for a refused join. It never names an address.</summary>
    public sealed class LaunchAdmissionReport
    {
        public LaunchAdmissionReport(string code, IEnumerable<string> problems)
        {
            Code = LaunchSessionValues.Choice(code, nameof(code), LaunchContractValues.AdmissionCodes);
            var copy = (problems ?? throw new ArgumentNullException(nameof(problems))).Take(LaunchSessionValues.MaximumProblems + 1).ToArray();
            if (copy.Length > LaunchSessionValues.MaximumProblems) throw new ArgumentException("An admission report has 64 problems.", nameof(problems));
            Problems = Array.AsReadOnly(copy.Select(problem =>
                LaunchSessionValues.DisplayText(problem, nameof(problems), 1, LaunchSessionValues.MaximumProblemLength)).ToArray());
        }

        public string Code { get; }
        public IReadOnlyList<string> Problems { get; }
    }

    /// <summary>
    /// Outcome V2's <c>multiplayer</c> block: how a session launch went, or how its session ended. A join's failures are the
    /// launcher's join failures of the same name; a host has its own. Only a join gets the host's admission report. It
    /// names no address and no port.
    /// </summary>
    public sealed class LaunchOutcomeMultiplayer
    {
        public LaunchOutcomeMultiplayer(string role, string intentId, string? serverId = null, string? failure = null, string? detail = null,
            LaunchAdmissionReport? admission = null, string? endReason = null)
        {
            Role = LaunchSessionValues.Choice(role, nameof(role), LaunchContractValues.SessionRoles);
            IntentId = LaunchContractValues.Token(intentId, nameof(intentId));
            ServerId = serverId == null ? null : LaunchSessionValues.Sha256(serverId, nameof(serverId));
            Failure = failure == null ? null : LaunchSessionValues.Choice(failure, nameof(failure),
                Role == "host" ? LaunchContractValues.HostFailures : LaunchContractValues.JoinFailures);
            Detail = detail == null ? null : LaunchSessionValues.DisplayText(detail, nameof(detail), 1, LaunchSessionValues.MaximumDetailLength);
            Admission = admission;
            EndReason = endReason == null ? null : LaunchSessionValues.Choice(endReason, nameof(endReason), LaunchContractValues.EndReasons);
            if (admission != null && Role != "join") throw new ArgumentException("Inconsistent session outcome.");
        }

        public string Role { get; }
        public string IntentId { get; }
        public string? ServerId { get; }
        public string? Failure { get; }

        /// <summary>Gets a sentence for the player, never naming an address.</summary>
        public string? Detail { get; }
        public LaunchAdmissionReport? Admission { get; }
        public string? EndReason { get; }
    }
}
