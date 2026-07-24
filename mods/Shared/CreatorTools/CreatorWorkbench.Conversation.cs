using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Sandbox;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private IRobotConversation? activeConversation;
        private CreatorRosterEntry? conversationRobot;
        private Task<OperationResult<RobotConversationTurnResult>>? conversationTask;
        private string submittedChatText = string.Empty;
        private bool graphConversationOwned;

        private OperationResult<string> BeginConversation(
            CreatorRosterEntry? entry = null,
            CreatorPersona? projectPersona = null)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            entry ??= SelectedRoster();
            if (!options.ConversationEnabled)
            {
                return OperationResult<string>.Failure(
                    ModErrorCode.Unavailable,
                    "Remote programming conversations are disabled in this mod's config.");
            }
            if (entry?.Robot == null || conversations == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "Choose a RobotKit-managed robot first.");
            }

            EndConversation();
            var knownTargets = objectives?.TargetNames
                .Where(name => !string.Equals(name, entry.TargetName, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? new[] { "PLAYER" };
            IRobotObjectiveHandle? objective = null;
            objectives?.TryGetObjective(entry.Robot, out objective);
            var baseRequest = RobotProgramDirector.BuildRequest(
                entry.DisplayName,
                RobotProgramDirector.DescribeActivity(entry.Robot.BrainMode, objective),
                knownTargets,
                entry.TargetName,
                () => DescribeTargets(entry, knownTargets),
                options.ChatMaxTurns,
                options.ChatTemperature);
            var personaTitle = projectPersona?.DisplayName
                ?? (string.IsNullOrWhiteSpace(personaName) ? "Creator persona" : personaName.Trim());
            var personaFrame = projectPersona?.SystemFrame ?? personaInstructions.Trim();
            var personaReply = projectPersona?.ReplyGuidance ?? string.Empty;
            var facts = baseRequest.GroundTruthFacts == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(baseRequest.GroundTruthFacts, StringComparer.Ordinal);
            if (projectPersona != null)
            {
                foreach (var fact in projectPersona.Facts) facts[fact.Key] = fact.Value;
            }
            facts["creator-persona"] = personaTitle;
            var request = new RobotConversationRequest(
                baseRequest.SystemFrame + (string.IsNullOrWhiteSpace(personaFrame)
                    ? string.Empty
                    : "\n\nCreator-authored persona for this scene: " + personaFrame),
                baseRequest.DecisionOptions,
                facts,
                baseRequest.LiveFacts,
                baseRequest.MaxTurns,
                baseRequest.Temperature,
                baseRequest.Usage,
                string.IsNullOrWhiteSpace(personaReply)
                    ? baseRequest.ReplyGuidance
                    : baseRequest.ReplyGuidance + " " + personaReply,
                baseRequest.DecisionGuidance,
                baseRequest.MaxReplyChars,
                baseRequest.ExtraOutputs);
            var begun = conversations.BeginConversation(request);
            if (!begun.TryGetValue(out activeConversation))
            {
                return OperationResult<string>.Failure(begun.ErrorCode, begun.ErrorMessage);
            }
            conversationRobot = entry;
            graphConversationOwned = projectPersona != null;
            chatStatus = "Programming " + entry.DisplayName + ". Type a request, then send.";
            RefreshUi();
            return OperationResult<string>.Success(chatStatus);
        }

        private OperationResult<string> SubmitConversation()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded)
            {
                return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            }
            if (conversationTask != null)
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "Wait for the current robot reply.");
            }
            if (activeConversation == null)
            {
                var begun = BeginConversation();
                if (!begun.Succeeded) return begun;
            }
            if (string.IsNullOrWhiteSpace(chatText))
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Type a programming request first.");
            }

            submittedChatText = chatText.Trim();
            chatText = string.Empty;
            conversationTask = activeConversation!.SubmitAsync(submittedChatText);
            chatStatus = "Waiting for " + conversationRobot!.DisplayName + "…";
            RefreshUi();
            return OperationResult<string>.Success(chatStatus);
        }

        private void PollConversation()
        {
            if (conversationTask?.IsCompleted != true) return;
            OperationResult<RobotConversationTurnResult> result;
            try
            {
                result = conversationTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                result = OperationResult<RobotConversationTurnResult>.Failure(ModErrorCode.External, exception.Message);
            }
            conversationTask = null;
            if (!result.TryGetValue(out var turn) || conversationRobot?.Robot == null)
            {
                chatStatus = result.ErrorMessage;
                RefreshUi();
                return;
            }

            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded)
            {
                chatStatus = "Robot reply received, but its changes were not applied: " + allowed.ErrorMessage;
                EndConversation(keepStatus: true);
                RefreshUi();
                return;
            }

            turn.Values.TryGetValue(RobotProgramDirector.TargetField, out var target);
            turn.Values.TryGetValue(RobotProgramDirector.ProgramField, out var program);
            turn.Values.TryGetValue(RobotProgramDirector.ProgramTargetField, out var programTarget);
            var known = objectives?.TargetNames ?? Array.Empty<string>();
            var robotTargets = roster
                .Where(entry => entry.Robot != null && !string.IsNullOrEmpty(entry.TargetName))
                .Select(entry => entry.TargetName)
                .ToArray();
            var parsed = RobotProgramDirector.Parse(
                turn.Decision,
                target,
                program,
                programTarget,
                known,
                robotTargets,
                conversationRobot.TargetName,
                submittedChatText);
            var emote = RobotProgramDirector.EmoteForDecision(turn.Decision);
            if (emote != null) conversationRobot.Robot.SetEmote(emote);
            var projectTargetId = ProjectTargetIdForRoster(conversationRobot.Id);
            runner?.Fire(
                CreatorGraphNodeKind.ConversationDecision,
                string.IsNullOrEmpty(projectTargetId) ? conversationRobot.Id : projectTargetId,
                turn.Decision);

            chatStatus = conversationRobot.DisplayName + ": " + turn.Reply;
            if (parsed.Objective != null && objectives != null)
            {
                var programmed = objectives.SetObjective(conversationRobot.Robot, parsed.Objective);
                if (!programmed.Succeeded) chatStatus += "\n" + programmed.ErrorMessage;
            }
            else if (parsed.GoAutonomous)
            {
                conversationRobot.Robot.SetBrainMode(RobotBrainMode.Autonomous);
            }
            else if (!string.IsNullOrEmpty(parsed.Problem))
            {
                chatStatus += "\n" + parsed.Problem;
            }

            if (!parsed.IsChat || activeConversation?.IsEnded == true) EndConversation(keepStatus: true);
            RefreshUi();
        }

        private IReadOnlyList<string> DescribeTargets(CreatorRosterEntry self, IReadOnlyList<string> names)
        {
            var described = new List<string>();
            foreach (var name in names)
            {
                if (objectives?.TryResolveTarget(name, out var snapshot) == true)
                {
                    var distance = (snapshot.Position - self.Robot!.Position).Length;
                    described.Add(name + ": " + distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m away");
                }
            }
            return described;
        }

        private void EndConversation(bool keepStatus = false)
        {
            activeConversation?.Dispose();
            activeConversation = null;
            conversationRobot = null;
            conversationTask = null;
            submittedChatText = string.Empty;
            graphConversationOwned = false;
            if (!keepStatus) chatStatus = string.Empty;
        }
    }
}
