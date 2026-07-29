using System;
using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    [DataContract]
    internal sealed class CreatorContentConfig : ISelfNormalizingConfig
    {
        [DataMember(Name = "toggleKey", Order = 1)]
        public string ToggleKey { get; set; } = "F5";

        public void Normalize()
        {
            ToggleKey = IsPortableKey(ToggleKey) ? ToggleKey.Trim() : "F5";
        }

        public static OperationResult<bool> Validate(CreatorContentConfig config)
        {
            if (config == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Creator Content config is required.");
            }
            return IsPortableKey(config.ToggleKey)
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "toggleKey must be a portable key name.");
        }

        private static bool IsPortableKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 32) return false;
            foreach (var character in value.Trim())
            {
                if (!char.IsLetterOrDigit(character)) return false;
            }
            return true;
        }
    }
}
