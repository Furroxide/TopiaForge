using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal sealed class MappedNativeWorld
    {
        public MappedNativeWorld(DiscoveredWorldDescriptor descriptor, string sourceKey)
        { Descriptor = descriptor; SourceKey = sourceKey; }
        public DiscoveredWorldDescriptor Descriptor { get; }
        public string SourceKey { get; }
    }
    internal static class WorldDiscoveryIdentity
    {
        public const int MaximumNativeEntries = 4096;
        public static OperationResult<IReadOnlyList<MappedNativeWorld>> Map(string family, NativeWorldSource source,
            IReadOnlyList<NativeWorldEntry> entries, int maximumResults)
        {
            if (!Enum.IsDefined(typeof(NativeWorldSource), source) || maximumResults < 1 || maximumResults > MaximumNativeEntries)
                return Failure(ModErrorCode.InvalidArgument, "Invalid discovery source or result budget.");
            try { _ = new DiscoveredWorldDescriptor(family + ".0", family, "Family"); }
            catch (ArgumentException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
            if (entries == null || entries.Count > MaximumNativeEntries)
                return Failure(ModErrorCode.External, "Native discovery exceeded its bounded inventory.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MappedNativeWorld>();
            foreach (var entry in entries)
            {
                if (entry == null || entry.Source != source)
                    return Failure(ModErrorCode.External, "Native discovery returned an entry from another source.");
                if (!keys.Add(entry.SourceKey))
                    return Failure(ModErrorCode.Conflict, "Native discovery contains an ambiguous duplicate source key.");
                try
                {
                    var id = CreateId(family, source, entry.SourceKey);
                    if (!ids.Add(id))
                        return Failure(ModErrorCode.Conflict, "The declared family has insufficient identifier space to distinguish its native worlds.");
                    result.Add(new MappedNativeWorld(new DiscoveredWorldDescriptor(id, family, entry.Name, entry.Description), entry.SourceKey));
                }
                catch (ArgumentException error)
                { return Failure(ModErrorCode.External, "Native world metadata is invalid: " + error.Message); }
            }
            // Validate the entire bounded inventory before taking a page: a collision must not depend on its order or requested size.
            return OperationResult<IReadOnlyList<MappedNativeWorld>>.Success(Array.AsReadOnly(result
                .OrderBy(x => x.Descriptor.Id, StringComparer.Ordinal).Take(maximumResults).ToArray()));
        }
        private static string CreateId(string family, NativeWorldSource source, string sourceKey)
        {
            var prefix = source == NativeWorldSource.CuratedLevels ? "curated\0" : "build-scenes\0";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(prefix + sourceKey));
            var suffix = new StringBuilder(64);
            foreach (var value in hash) suffix.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return family + "." + suffix.ToString(0, Math.Min(64, 95 - family.Length));
        }
        private static OperationResult<IReadOnlyList<MappedNativeWorld>> Failure(ModErrorCode code, string message) =>
            OperationResult<IReadOnlyList<MappedNativeWorld>>.Failure(code, message);
    }
}
