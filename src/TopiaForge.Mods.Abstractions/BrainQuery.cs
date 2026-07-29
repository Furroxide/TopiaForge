using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Asks a robot's <b>LLM brain</b> a structured question and gets a machine-readable answer back — the shared
    /// "talk to the brain" primitive. It proxies the game's own backend (the same inference the native robots think
    /// with) behind a Unity-free, network-free contract, so a mod can let a robot <i>decide</i> something in its own
    /// words (comply or refuse, pick a tactic, answer a riddle) without re-deriving any backend plumbing.
    /// </summary>
    /// <remarks>
    /// Published by the <c>TopiaForge.RobotKit</c> framework mod and resolved with
    /// <c>context.RequireExtension&lt;IRobotBrainQueryService&gt;()</c>, exactly like
    /// <see cref="IRobotAgentService"/>.
    /// <para>
    /// The call is <b>asynchronous and must never block a frame</b>: a brain round-trip is a network request to a
    /// metered backend (typically a few hundred milliseconds, occasionally up to a second). Await
    /// <see cref="IRobotBrainQueryService.QueryAsync"/> with the current mod lifetime token; it never blocks a game
    /// frame.
    /// </para>
    /// <para>
    /// Everything degrades gracefully. When the backend is unreachable, the per-user token is missing/expired, or the
    /// game build does not expose what the service needs, <see cref="IsAvailable"/> is <c>false</c> and a query
    /// returns a stable failed <see cref="OperationResult{T}"/> rather than throwing. A brain query
    /// is a pure <i>enrichment</i> layer — gameplay should resolve its own deterministic outcome first and let the
    /// brain answer (or not) a beat later.
    /// </para>
    /// <para>
    /// <b>Cost note:</b> each query spends a call against the player's own backend token. Keep queries deliberate
    /// (gated by a cooldown / resource), not per-frame.
    /// </para>
    /// </remarks>
    public interface IRobotBrainQueryService
    {
        /// <summary>
        /// <c>true</c> when a brain query can currently be served — the backend token was resolved and the service is
        /// live. Cheap to poll.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Runs a structured brain query asynchronously with stable failure codes and cancellation.
        /// </summary>
        Task<OperationResult<BrainQueryResult>> QueryAsync(
            BrainQueryRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A structured brain question. Mirrors the backend's structured-output shape without exposing any transport or
    /// Unity types: a free-text <see cref="Prompt"/> plus the typed <see cref="Outputs"/> fields the brain must fill,
    /// each optionally constrained to an enum via <see cref="BrainOutputField.AllowedStrings"/>. Build the prompt from
    /// your own live state (who the robot is, what the player said, the situation) so the answer is grounded.
    /// </summary>
    public sealed class BrainQueryRequest
    {
        /// <summary>Creates a query with a prompt and the output fields the brain must return.</summary>
        /// <param name="prompt">The full natural-language question/context for the brain.</param>
        /// <param name="outputs">The typed fields the brain must produce (at least one).</param>
        /// <param name="usage">A stable diagnostic label for this query.</param>
        /// <param name="successDescription">Optional guidance describing a successful answer.</param>
        /// <param name="temperature">Sampling temperature; zero is the most deterministic.</param>
        /// <param name="useReasoning">Whether the provider should request an additional reasoning value.</param>
        public BrainQueryRequest(
            string prompt,
            IReadOnlyList<BrainOutputField> outputs,
            string usage = "robot-brain-query",
            string? successDescription = null,
            float temperature = 0.7f,
            bool useReasoning = false)
        {
            Prompt = prompt ?? string.Empty;
            Outputs = outputs == null
                ? System.Array.Empty<BrainOutputField>()
                : new ReadOnlyCollection<BrainOutputField>(new List<BrainOutputField>(outputs));
            Usage = usage ?? string.Empty;
            SuccessDescription = successDescription;
            Temperature = temperature;
            UseReasoning = useReasoning;
        }

        /// <summary>The full natural-language prompt the brain reasons over.</summary>
        public string Prompt { get; }

        /// <summary>The structured fields the brain must fill in (its machine-readable answer).</summary>
        public IReadOnlyList<BrainOutputField> Outputs { get; }

        /// <summary>
        /// Short label describing what this query is for (telemetry/debugging on the backend). Optional; defaults to a
        /// generic label when empty.
        /// </summary>
        public string Usage { get; }

        /// <summary>Optional description of what a successful answer looks like, to steer the brain. May be empty.</summary>
        public string? SuccessDescription { get; }

        /// <summary>Sampling temperature (0 = most deterministic). Clamped by the service to the backend's valid range.</summary>
        public float Temperature { get; }

        /// <summary>
        /// When <c>true</c>, asks the brain to also produce its reasoning (a <c>reasoning</c> field in the result
        /// values). Costs more tokens; leave <c>false</c> for snappy gameplay decisions.
        /// </summary>
        public bool UseReasoning { get; }
    }

    /// <summary>One typed field the brain must return, optionally constrained to a fixed set of strings (an enum).</summary>
    public sealed class BrainOutputField
    {
        /// <summary>Creates an output field.</summary>
        /// <param name="name">The field key the brain fills (also the key in <see cref="BrainQueryResult.Values"/>).</param>
        /// <param name="description">What the field means, to steer the brain.</param>
        /// <param name="type">The field's value type.</param>
        /// <param name="allowedStrings">For a <see cref="BrainFieldType.String"/> field, an optional enum the brain must choose from.</param>
        public BrainOutputField(
            string name,
            string description,
            BrainFieldType type = BrainFieldType.String,
            IReadOnlyList<string>? allowedStrings = null)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            Type = type;
            AllowedStrings = allowedStrings == null
                ? null
                : new ReadOnlyCollection<string>(new List<string>(allowedStrings));
        }

        /// <summary>The field key (e.g. <c>"action"</c>).</summary>
        public string Name { get; }

        /// <summary>What the field means.</summary>
        public string Description { get; }

        /// <summary>The field's value type.</summary>
        public BrainFieldType Type { get; }

        /// <summary>
        /// For a string field, the closed set of values the brain must choose from (e.g. an action enum); <c>null</c>
        /// for free-form text or non-string fields.
        /// </summary>
        public IReadOnlyList<string>? AllowedStrings { get; }
    }

    /// <summary>
    /// The result of a brain query. The brain's answer is a flat map from each requested <see cref="BrainOutputField.Name"/>
    /// to its value rendered as a string (consumers parse/enum-map as needed).
    /// </summary>
    public sealed class BrainQueryResult
    {
        /// <summary>Creates a brain query result.</summary>
        public BrainQueryResult(IReadOnlyDictionary<string, string> values)
        {
            Values = new ReadOnlyDictionary<string, string>(
                values == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(values, System.StringComparer.Ordinal));
        }

        /// <summary>
        /// The brain's answer: each requested field name mapped to its value as a string.
        /// </summary>
        public IReadOnlyDictionary<string, string> Values { get; }

        /// <summary>Gets a returned field value by name. Returns <c>false</c> when absent.</summary>
        public bool TryGet(string name, out string value)
        {
            if (name != null && Values.TryGetValue(name, out var found))
            {
                value = found;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    /// <summary>The value type of a <see cref="BrainOutputField"/> the brain must return.</summary>
    public enum BrainFieldType
    {
        /// <summary>Text (optionally constrained to <see cref="BrainOutputField.AllowedStrings"/>).</summary>
        String,

        /// <summary>A number.</summary>
        Number,

        /// <summary>A boolean.</summary>
        Boolean
    }

    /// <summary>
    /// A shared vocabulary for the common "comply or refuse" decision a robot brain can make when commanded — a
    /// convenience for mods that want a talk-to/persuade verb (sibling of <see cref="RobotBrainMode"/>/<see cref="RobotGait"/>).
    /// The brain-query service itself is generic and does not require this enum.
    /// </summary>
    public enum RobotDecision
    {
        /// <summary>The robot obeys the command.</summary>
        Comply,

        /// <summary>The robot stops/holds in place.</summary>
        Freeze,

        /// <summary>The robot disengages and moves away.</summary>
        Flee,

        /// <summary>The robot refuses (and may turn hostile).</summary>
        Resist,

        /// <summary>No decision could be resolved.</summary>
        Unknown
    }
}
