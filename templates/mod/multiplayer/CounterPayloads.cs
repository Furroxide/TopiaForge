using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    // Multiplayer payloads are plain, sealed classes with public auto-properties of deterministic types, which the
    // generator turns into bounded binary codecs. A string or array member also needs [NetworkBound(maximum)]
    // (TFMP004), so that every payload has a hard size limit the host can enforce.

    /// <summary>The replicated state: the number every player sees.</summary>
    public sealed class CounterState
    {
        /// <summary>Gets or sets how many presses the host has accepted in this session.</summary>
        public int Count { get; set; }
    }

    /// <summary>A press. It carries nothing: the host, not the presser, decides what a press is worth.</summary>
    public sealed class IncrementRequest
    {
    }
}
