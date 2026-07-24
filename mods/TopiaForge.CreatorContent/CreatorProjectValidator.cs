using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorProjectValidator
    {
        public const int CurrentSchemaVersion = 1;
        private static readonly HashSet<string> ConversationDecisions = new HashSet<string>(
            new[] { "CHAT", "IDLE", "GO_TO", "FOLLOW", "PATROL", "WANDER", "FLEE", "REPROGRAM", "AUTONOMOUS" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RobotObjectiveStates = new HashSet<string>(
            new[] { "Idle", "Seeking", "Arrived", "Dwelling", "TargetMissing", "Cancelled", "Delivered" },
            StringComparer.OrdinalIgnoreCase);
        private readonly Func<CreatorCatalogSnapshot> getCatalog;

        public CreatorProjectValidator(Func<CreatorCatalogSnapshot> getCatalog)
        {
            this.getCatalog = getCatalog ?? throw new ArgumentNullException(nameof(getCatalog));
        }

        public CreatorProjectValidationResult Validate(CreatorEventProject project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var issues = new List<CreatorProjectValidationIssue>();
            if (project.SchemaVersion != CurrentSchemaVersion)
            {
                Error(issues, "schema.unsupported", "Only event-project schema version 1 is supported.", project.Id);
            }
            ValidateId(issues, project.Id, "project.id", 64);
            Bounded(issues, project.DisplayName, 128, "project.name", project.Id);
            Bounded(issues, project.Description, 2048, "project.description", project.Id);
            if (project.Scope == CreatorProjectScope.Global && string.IsNullOrWhiteSpace(project.SceneName))
            {
                Error(issues, "scope.scene-required", "Global projects must name the scene they target.", project.Id);
            }
            if (project.Scope == CreatorProjectScope.Sandbox && string.IsNullOrWhiteSpace(project.WorldId))
            {
                Error(issues, "scope.world-required", "Sandbox projects must name the managed world they target.", project.Id);
            }
            Bounded(issues, project.WorldId, 128, "scope.world", project.Id);
            Bounded(issues, project.SceneName, 128, "scope.scene", project.Id);
            Limits(issues, project);

            Unique(issues, project.Entities.Select(entity => entity.Id), "entity.id");
            Unique(issues, project.NativeBindings.Select(binding => binding.Id), "native-binding.id");
            Unique(issues, project.Personas.Select(persona => persona.Id), "persona.id");
            Unique(issues, project.Nodes.Select(node => node.Id), "node.id");

            ValidateEntities(issues, project);
            ValidateNativeBindings(issues, project);
            ValidatePersonas(issues, project);
            ValidateNodes(issues, project);
            ValidateEdges(issues, project);
            ValidateAcyclic(issues, project);
            return new CreatorProjectValidationResult(issues
                .OrderBy(issue => issue.Severity)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.SubjectId, StringComparer.Ordinal)
                .ToArray());
        }

        private void ValidateEntities(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            var catalog = getCatalog().Entries.ToDictionary(entry => entry.ContentId, StringComparer.OrdinalIgnoreCase);
            foreach (var entity in project.Entities)
            {
                ValidateId(issues, entity.Id, "entity.id", 64);
                Bounded(issues, entity.DisplayName, 128, "entity.name", entity.Id);
                Bounded(issues, entity.ContentId, 256, "entity.content-id", entity.Id);
                if (!catalog.TryGetValue(entity.ContentId, out var descriptor))
                {
                    Warning(issues, "entity.content-unresolved", "The referenced creator content is not currently installed; loading is allowed but running must remain blocked.", entity.Id);
                }
                else if (!string.IsNullOrEmpty(entity.ExpectedSourceVersion)
                         && !string.Equals(entity.ExpectedSourceVersion, descriptor.SourceVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Warning(issues, "entity.source-version", "The installed content source version differs from the authored version; loading is allowed but running requires review.", entity.Id);
                }
            }
        }

        private static void ValidateNativeBindings(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            foreach (var binding in project.NativeBindings)
            {
                ValidateId(issues, binding.Id, "native-binding.id", 64);
                Bounded(issues, binding.DisplayName, 128, "native-binding.name", binding.Id);
                Bounded(issues, binding.SceneName, 128, "native-binding.scene", binding.Id);
                Bounded(issues, binding.NameContains, 128, "native-binding.name-fragment", binding.Id);
                if (!CreatorIds.IsLocalId(binding.AdapterId, 128))
                {
                    Error(issues, "native-binding.adapter", "Native bindings require a stable curated adapter id.", binding.Id);
                }
                if (binding.SearchRadius > 1000f)
                {
                    Error(issues, "native-binding.radius", "Native binding search radius must be no more than 1,000 metres.", binding.Id);
                }
                if (!string.Equals(binding.SceneName, project.SceneName, StringComparison.Ordinal))
                {
                    Error(issues, "native-binding.scene-mismatch", "Native bindings must name the project's exact scene.", binding.Id);
                }
            }
        }

        private static void ValidatePersonas(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            foreach (var persona in project.Personas)
            {
                ValidateId(issues, persona.Id, "persona.id", 64);
                Bounded(issues, persona.DisplayName, 128, "persona.name", persona.Id);
                Bounded(issues, persona.SystemFrame, 4000, "persona.system-frame", persona.Id);
                Bounded(issues, persona.ReplyGuidance, 2000, "persona.reply-guidance", persona.Id);
                if (persona.Facts.Count > 64)
                {
                    Error(issues, "persona.facts-limit", "A persona may contain at most 64 facts.", persona.Id);
                }
                foreach (var fact in persona.Facts)
                {
                    Bounded(issues, fact.Key, 64, "persona.fact-key", persona.Id);
                    Bounded(issues, fact.Value, 512, "persona.fact-value", persona.Id);
                }
            }
        }

        private static void ValidateNodes(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            var entityIds = new HashSet<string>(project.Entities.Select(entity => entity.Id), StringComparer.Ordinal);
            var bindingIds = new HashSet<string>(project.NativeBindings.Select(binding => binding.Id), StringComparer.Ordinal);
            var personaIds = new HashSet<string>(project.Personas.Select(persona => persona.Id), StringComparer.Ordinal);
            foreach (var node in project.Nodes)
            {
                ValidateId(issues, node.Id, "node.id", 64);
                if (node.Parameters.Count > 32)
                {
                    Error(issues, "node.parameters-limit", "A graph node may contain at most 32 parameters.", node.Id);
                }
                foreach (var parameter in node.Parameters)
                {
                    Bounded(issues, parameter.Key, 64, "node.parameter-key", node.Id);
                    Bounded(issues, parameter.Value, 1024, "node.parameter-value", node.Id);
                }

                if (node.Kind == CreatorGraphNodeKind.Delay)
                {
                    if (!TryPositiveNumber(node, "seconds", 3600f))
                    {
                        Error(issues, "node.seconds", "Delay nodes require finite 'seconds' greater than zero and no more than 3600.", node.Id);
                    }
                }
                if (node.Kind == CreatorGraphNodeKind.Repeat)
                {
                    if (!node.Parameters.TryGetValue("value", out var rawCount)
                        || !int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                        || count < 1 || count > 100)
                    {
                        Error(issues, "node.repeat", "Repeat nodes require integer 'value' between 1 and 100.", node.Id);
                    }
                }
                if (node.Kind == CreatorGraphNodeKind.StateCondition)
                {
                    if (!node.Parameters.TryGetValue("value", out var condition) || string.IsNullOrWhiteSpace(condition))
                    {
                        Error(issues, "node.condition", "State-condition nodes require non-empty 'value'.", node.Id);
                    }
                    else
                    {
                        ValidateStateCondition(issues, project, node, condition, entityIds, bindingIds);
                    }
                }

                if (node.Parameters.TryGetValue("maxActivations", out var rawMaximum)
                    && (!int.TryParse(rawMaximum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum)
                        || maximum < 1 || maximum > 1000))
                {
                    Error(issues, "node.max-activations", "'maxActivations' must be an integer between 1 and 1,000.", node.Id);
                }

                if (RequiresEntityOnly(node.Kind))
                {
                    RequireEntity(issues, node, entityIds);
                    if (HasValue(node, "nativeBindingId"))
                    {
                        Error(issues, "node.native-binding", "This node may target only a declared project entity.", node.Id);
                    }
                }
                else if (SupportsEntityOrNativeBinding(node.Kind))
                {
                    ValidateExclusiveTarget(issues, project, node, entityIds, bindingIds);
                }

                if (node.Kind == CreatorGraphNodeKind.PlayerEnteredRadius
                    && !TryPositiveNumber(node, "radius", 1000f))
                {
                    Error(issues, "node.radius", "Player-entered-radius triggers require finite 'radius' greater than zero and no more than 1,000.", node.Id);
                }
                if (node.Kind == CreatorGraphNodeKind.InteractionTrigger)
                {
                    if (!node.Parameters.TryGetValue("prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
                    {
                        Error(issues, "node.prompt", "Interaction triggers require a non-empty 'prompt'.", node.Id);
                    }
                    if (!TryPositiveNumber(node, "radius", 10f))
                    {
                        Error(issues, "node.radius", "Interaction triggers require finite 'radius' greater than zero and no more than 10.", node.Id);
                    }
                }
                if (node.Kind == CreatorGraphNodeKind.RobotObjectiveState)
                {
                    if (!node.Parameters.TryGetValue("value", out var state)
                        || !RobotObjectiveStates.Contains(state))
                    {
                        Error(issues, "node.objective-state", "Robot-objective-state triggers require a named RobotObjectiveState 'value'.", node.Id);
                    }
                }
                if (node.Kind == CreatorGraphNodeKind.ConversationDecision)
                {
                    // Empty is an intentional wildcard: it fires for any decision from the bounded built-in set.
                    if (node.Parameters.TryGetValue("value", out var decision)
                        && decision.Length > 0
                        && !ConversationDecisions.Contains(decision))
                    {
                        Error(issues, "node.conversation-decision", "Conversation decisions must be CHAT, IDLE, GO_TO, FOLLOW, PATROL, WANDER, FLEE, REPROGRAM, AUTONOMOUS, or empty for the documented wildcard.", node.Id);
                    }
                    ValidateOptionalTarget(issues, project, node, entityIds, bindingIds);
                }
                if (node.Kind == CreatorGraphNodeKind.ConfigureRobot)
                {
                    ValidateRobotConfiguration(issues, node);
                }
                if (node.Kind == CreatorGraphNodeKind.SetTransform && !ValidCompactTransform(node))
                {
                    Error(issues, "node.transform", "Transforms require project-relative 'value' with 3 or 10 finite comma-separated numbers and non-zero scale components.", node.Id);
                }
                if (node.Kind == CreatorGraphNodeKind.SetRobotObjective)
                {
                    ValidateObjective(issues, node);
                }
                if (node.Kind == CreatorGraphNodeKind.SetRobotPersonality
                    || node.Kind == CreatorGraphNodeKind.BeginConversation)
                {
                    if (!node.Parameters.TryGetValue("personaId", out var personaId) || !personaIds.Contains(personaId))
                    {
                        Error(issues, "node.persona", "This node requires an existing 'personaId'.", node.Id);
                    }
                    else if (node.Kind == CreatorGraphNodeKind.SetRobotPersonality)
                    {
                        var persona = project.Personas.First(item =>
                            string.Equals(item.Id, personaId, StringComparison.Ordinal));
                        if (string.IsNullOrWhiteSpace(persona.SystemFrame))
                        {
                            Error(
                                issues,
                                "node.persona-system-frame",
                                "Robot personality nodes require a persona with a non-empty system frame.",
                                node.Id);
                        }
                    }
                }
                if (node.Kind == CreatorGraphNodeKind.ShowToast
                    && (!node.Parameters.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text)))
                {
                    Error(issues, "node.text", "Toast nodes require non-empty 'text'.", node.Id);
                }
                if (node.Kind == CreatorGraphNodeKind.PlayAudio
                    && (!node.Parameters.TryGetValue("cueId", out var cue) || string.IsNullOrWhiteSpace(cue)))
                {
                    Error(issues, "node.cue", "Audio nodes require non-empty 'cueId'.", node.Id);
                }
            }
        }
    }
}
