using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>User-editable settings persisted by TopiaForge.</summary>
    public sealed class {{TYPE_NAME}}Config
    {
        /// <summary>The message returned by the <c>greet</c> command.</summary>
        public string Greeting { get; set; } = "Hello from {{DISPLAY_NAME}}!";

        internal static ConfigDefinition<{{TYPE_NAME}}Config> Definition { get; } =
            new ConfigDefinition<{{TYPE_NAME}}Config>(
                schemaVersion: 2,
                createDefault: () => new {{TYPE_NAME}}Config(),
                validate: value =>
                {
                    var greeting = value.Greeting ?? string.Empty;
                    return greeting.Length > 0 && greeting.Length <= 120
                        ? OperationResult<bool>.Success(true)
                        : OperationResult<bool>.Failure(
                            ModErrorCode.InvalidArgument,
                            "Greeting must contain between 1 and 120 characters.");
                },
                migrate: (_, value) =>
                {
                    // V1 stored the greeting exactly as entered. V2 trims accidental surrounding whitespace.
                    value.Greeting = (value.Greeting ?? string.Empty).Trim();
                    return OperationResult<{{TYPE_NAME}}Config>.Success(value);
                });
    }
}
