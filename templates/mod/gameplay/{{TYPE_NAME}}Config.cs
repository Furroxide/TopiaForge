using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>User-editable settings for the aim scanner.</summary>
    public sealed class {{TYPE_NAME}}Config
    {
        /// <summary>The keyboard key used by the named, rebindable scan action.</summary>
        public string ActionKey { get; set; } = "G";

        /// <summary>The maximum aim-ray distance in world units.</summary>
        public float MaximumRange { get; set; } = 30f;

        internal static ConfigDefinition<{{TYPE_NAME}}Config> Definition { get; } =
            new ConfigDefinition<{{TYPE_NAME}}Config>(
                schemaVersion: 1,
                createDefault: () => new {{TYPE_NAME}}Config(),
                validate: value =>
                {
                    if (string.IsNullOrWhiteSpace(value.ActionKey))
                    {
                        return OperationResult<bool>.Failure(
                            ModErrorCode.InvalidArgument,
                            "ActionKey cannot be empty.");
                    }

                    return value.MaximumRange >= 1f && value.MaximumRange <= 250f
                        ? OperationResult<bool>.Success(true)
                        : OperationResult<bool>.Failure(
                            ModErrorCode.InvalidArgument,
                            "MaximumRange must be between 1 and 250 world units.");
                });
    }
}
