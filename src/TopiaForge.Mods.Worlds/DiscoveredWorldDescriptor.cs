using System;
using System.Linq;

namespace TopiaForge.Mods
{
    /// <summary>Immutable identity and display metadata for one observed family instance.</summary>
    public sealed class DiscoveredWorldDescriptor
    {
        /// <summary>Creates an instance beneath a family with room for a dot and nonempty suffix.</summary>
        public DiscoveredWorldDescriptor(string id, string familyId, string name, string? description = null)
        {
            Id = Identifier(id, 96, nameof(id));
            FamilyId = Identifier(familyId, 94, nameof(familyId));
            if (!Id.StartsWith(FamilyId + ".", StringComparison.OrdinalIgnoreCase) || Id.Length <= FamilyId.Length + 1)
                throw new ArgumentException("A discovered instance requires a nonempty suffix beneath its family.");
            Name = Text(name, 1, 128, nameof(name));
            Description = description == null ? null : Text(description, 0, 1024, nameof(description));
        }
        /// <summary>Gets the concrete instance id, at most 96 ASCII characters.</summary>
        public string Id { get; }
        /// <summary>Gets the declared family id, at most 94 ASCII characters.</summary>
        public string FamilyId { get; }
        /// <summary>Gets the display name.</summary>
        public string Name { get; }
        /// <summary>Gets the optional description, preserving absence versus an explicit empty value.</summary>
        public string? Description { get; }

        private static string Identifier(string value, int maximum, string parameter)
        {
            if (value == null || value.Length < 4 || value.Length > maximum
                || !Ascii(value[0]) || value.Any(c => !Ascii(c) && c != '.' && c != '_' && c != '-'))
                throw new ArgumentException("Invalid declaration identity.", parameter);
            return value;
        }
        private static bool Ascii(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        private static string Text(string value, int minimum, int maximum, string parameter)
        {
            if (value == null) throw new ArgumentNullException(parameter);
            var count = 0;
            for (var i = 0; i < value.Length; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            if (count < minimum || count > maximum) throw new ArgumentException("Invalid display text length.", parameter);
            return value;
        }
    }
}
