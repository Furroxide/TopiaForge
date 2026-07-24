using System;

namespace TopiaForge.CreatorContent
{
    internal static class CreatorIds
    {
        public static bool IsLocalId(string value, int maximumLength = 128)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= 'a' && character <= 'z')
                      || (character >= 'A' && character <= 'Z')
                      || (character >= '0' && character <= '9')
                      || character == '.' || character == '_' || character == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        public static string Qualify(string sourceId, string localId) =>
            sourceId.Trim().ToLowerInvariant() + ":" + localId.Trim().ToLowerInvariant();

        public static string QualifyAdapter(string sourceId, string localId) =>
            sourceId.Trim().ToLowerInvariant() + "." + localId.Trim().ToLowerInvariant();
    }
}
