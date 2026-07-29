using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>User-editable UI settings.</summary>
    public sealed class {{TYPE_NAME}}Config
    {
        /// <summary>The nonreserved keyboard key used to toggle the mod window.</summary>
        public string ToggleKey { get; set; } = "U";

        internal static ConfigDefinition<{{TYPE_NAME}}Config> Definition { get; } =
            new ConfigDefinition<{{TYPE_NAME}}Config>(
                schemaVersion: 1,
                createDefault: () => new {{TYPE_NAME}}Config(),
                validate: value =>
                {
                    if (string.IsNullOrWhiteSpace(value.ToggleKey))
                    {
                        return OperationResult<bool>.Failure(
                            ModErrorCode.InvalidArgument,
                            "ToggleKey cannot be empty.");
                    }

                    return string.Equals(value.ToggleKey, "F8", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value.ToggleKey, "F10", StringComparison.OrdinalIgnoreCase)
                        ? OperationResult<bool>.Failure(
                            ModErrorCode.InvalidArgument,
                            "F8 and F10 are reserved by TopiaForge; choose another key.")
                        : OperationResult<bool>.Success(true);
                });
    }
}
