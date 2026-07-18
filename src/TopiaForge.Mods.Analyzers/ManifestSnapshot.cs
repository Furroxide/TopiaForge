using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Analyzers
{
    internal sealed class ManifestSnapshot
    {
        private const int MaximumManifestCharacters = 1024 * 1024;
        private readonly ImmutableHashSet<string> capabilities;
        private readonly ImmutableHashSet<string> dependencies;

        private ManifestSnapshot(
            string packageId,
            IEnumerable<string> capabilities,
            IEnumerable<string> dependencies)
        {
            PackageId = packageId;
            this.capabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
            this.dependencies = dependencies.ToImmutableHashSet(StringComparer.Ordinal);
        }

        public string PackageId { get; }

        public static ManifestSnapshot Read(
            ImmutableArray<AdditionalText> files,
            System.Threading.CancellationToken cancellationToken)
        {
            var file = files.FirstOrDefault(item => string.Equals(
                Path.GetFileName(item.Path),
                "topiaforge.mod.json",
                StringComparison.OrdinalIgnoreCase));
            var json = file?.GetText(cancellationToken)?.ToString();
            if (json == null || string.IsNullOrWhiteSpace(json) || json.Length > MaximumManifestCharacters)
            {
                return Empty();
            }

            try
            {
                return new ManifestParser(json).Parse();
            }
            catch (FormatException)
            {
                return Empty();
            }
        }

        public bool HasCapability(string capability) => capabilities.Contains(capability);

        public bool HasDependency(string dependencyId) => dependencies.Contains(dependencyId);

        public bool IsPackage(string packageId) => string.Equals(PackageId, packageId, StringComparison.Ordinal);

        private static ManifestSnapshot Empty() =>
            new ManifestSnapshot(string.Empty, Array.Empty<string>(), Array.Empty<string>());

        private sealed class ManifestParser
        {
            private const int MaximumDepth = 64;
            private const int MaximumTrackedEntries = 4096;
            private readonly string json;
            private readonly HashSet<string> capabilities = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> dependencies = new HashSet<string>(StringComparer.Ordinal);
            private int position;
            private string packageId = string.Empty;

            public ManifestParser(string json)
            {
                this.json = json;
            }

            public ManifestSnapshot Parse()
            {
                Expect('{');
                if (!TryConsume('}'))
                {
                    while (true)
                    {
                        var propertyName = ReadString();
                        Expect(':');
                        if (string.Equals(propertyName, "name", StringComparison.Ordinal))
                        {
                            ReadPackageId();
                        }
                        else if (string.Equals(propertyName, "capabilities", StringComparison.Ordinal))
                        {
                            ReadCapabilities();
                        }
                        else if (string.Equals(propertyName, "dependencies", StringComparison.Ordinal)
                            || string.Equals(propertyName, "optionalDependencies", StringComparison.Ordinal))
                        {
                            ReadDependencyMap();
                        }
                        else
                        {
                            SkipValue(1);
                        }

                        if (TryConsume('}'))
                        {
                            break;
                        }

                        Expect(',');
                    }
                }

                SkipWhitespace();
                if (position != json.Length)
                {
                    throw InvalidJson();
                }

                return new ManifestSnapshot(packageId, capabilities, dependencies);
            }

            private void ReadPackageId()
            {
                SkipWhitespace();
                if (Peek() == '"')
                {
                    packageId = ReadString();
                    return;
                }

                SkipValue(1);
            }

            private void ReadCapabilities()
            {
                SkipWhitespace();
                if (!TryConsume('['))
                {
                    SkipValue(1);
                    return;
                }

                if (TryConsume(']'))
                {
                    return;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() == '"')
                    {
                        var capability = ReadString();
                        if (capabilities.Count < MaximumTrackedEntries)
                        {
                            capabilities.Add(capability);
                        }
                    }
                    else
                    {
                        SkipValue(2);
                    }

                    if (TryConsume(']'))
                    {
                        return;
                    }

                    Expect(',');
                }
            }

            private void ReadDependencyMap()
            {
                SkipWhitespace();
                if (!TryConsume('{'))
                {
                    SkipValue(1);
                    return;
                }

                if (TryConsume('}'))
                {
                    return;
                }

                while (true)
                {
                    var dependencyId = ReadString();
                    if (dependencies.Count < MaximumTrackedEntries)
                    {
                        dependencies.Add(dependencyId);
                    }

                    Expect(':');
                    SkipValue(2);
                    if (TryConsume('}'))
                    {
                        return;
                    }

                    Expect(',');
                }
            }

            private void SkipValue(int depth)
            {
                if (depth > MaximumDepth)
                {
                    throw InvalidJson();
                }

                SkipWhitespace();
                switch (Peek())
                {
                    case '"':
                        ReadString();
                        return;
                    case '{':
                        SkipObject(depth + 1);
                        return;
                    case '[':
                        SkipArray(depth + 1);
                        return;
                    case 't':
                        ExpectLiteral("true");
                        return;
                    case 'f':
                        ExpectLiteral("false");
                        return;
                    case 'n':
                        ExpectLiteral("null");
                        return;
                    default:
                        SkipNumber();
                        return;
                }
            }

            private void SkipObject(int depth)
            {
                Expect('{');
                if (TryConsume('}'))
                {
                    return;
                }

                while (true)
                {
                    ReadString();
                    Expect(':');
                    SkipValue(depth);
                    if (TryConsume('}'))
                    {
                        return;
                    }

                    Expect(',');
                }
            }

            private void SkipArray(int depth)
            {
                Expect('[');
                if (TryConsume(']'))
                {
                    return;
                }

                while (true)
                {
                    SkipValue(depth);
                    if (TryConsume(']'))
                    {
                        return;
                    }

                    Expect(',');
                }
            }

            private string ReadString()
            {
                Expect('"');
                var value = new StringBuilder();
                while (position < json.Length)
                {
                    var current = json[position++];
                    if (current == '"')
                    {
                        return value.ToString();
                    }

                    if (current < 0x20)
                    {
                        throw InvalidJson();
                    }

                    if (current != '\\')
                    {
                        value.Append(current);
                        continue;
                    }

                    if (position >= json.Length)
                    {
                        throw InvalidJson();
                    }

                    var escaped = json[position++];
                    switch (escaped)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u': value.Append(ReadUnicodeEscape()); break;
                        default: throw InvalidJson();
                    }
                }

                throw InvalidJson();
            }

            private char ReadUnicodeEscape()
            {
                if (position + 4 > json.Length)
                {
                    throw InvalidJson();
                }

                var value = 0;
                for (var index = 0; index < 4; index++)
                {
                    value = (value << 4) + HexValue(json[position++]);
                }

                return (char)value;
            }

            private void SkipNumber()
            {
                SkipWhitespace();
                if (position < json.Length && json[position] == '-')
                {
                    position++;
                }

                if (position >= json.Length)
                {
                    throw InvalidJson();
                }

                if (json[position] == '0')
                {
                    position++;
                }
                else
                {
                    RequireDigit(oneToNine: true);
                    while (position < json.Length && IsAsciiDigit(json[position]))
                    {
                        position++;
                    }
                }

                if (position < json.Length && json[position] == '.')
                {
                    position++;
                    RequireDigit(oneToNine: false);
                    while (position < json.Length && IsAsciiDigit(json[position]))
                    {
                        position++;
                    }
                }

                if (position < json.Length && (json[position] == 'e' || json[position] == 'E'))
                {
                    position++;
                    if (position < json.Length && (json[position] == '+' || json[position] == '-'))
                    {
                        position++;
                    }

                    RequireDigit(oneToNine: false);
                    while (position < json.Length && IsAsciiDigit(json[position]))
                    {
                        position++;
                    }
                }
            }

            private void RequireDigit(bool oneToNine)
            {
                if (position >= json.Length || !IsAsciiDigit(json[position])
                    || (oneToNine && json[position] == '0'))
                {
                    throw InvalidJson();
                }

                position++;
            }

            private void ExpectLiteral(string value)
            {
                for (var index = 0; index < value.Length; index++)
                {
                    if (position >= json.Length || json[position++] != value[index])
                    {
                        throw InvalidJson();
                    }
                }
            }

            private void Expect(char value)
            {
                if (!TryConsume(value))
                {
                    throw InvalidJson();
                }
            }

            private bool TryConsume(char value)
            {
                SkipWhitespace();
                if (position >= json.Length || json[position] != value)
                {
                    return false;
                }

                position++;
                return true;
            }

            private char Peek()
            {
                SkipWhitespace();
                if (position >= json.Length)
                {
                    throw InvalidJson();
                }

                return json[position];
            }

            private void SkipWhitespace()
            {
                while (position < json.Length && IsJsonWhitespace(json[position]))
                {
                    position++;
                }
            }

            private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';

            private static bool IsJsonWhitespace(char value) =>
                value == ' ' || value == '\t' || value == '\r' || value == '\n';

            private static int HexValue(char value)
            {
                if (value >= '0' && value <= '9') return value - '0';
                if (value >= 'a' && value <= 'f') return value - 'a' + 10;
                if (value >= 'A' && value <= 'F') return value - 'A' + 10;
                throw InvalidJson();
            }

            private static FormatException InvalidJson() =>
                new FormatException("The TopiaForge manifest is not valid JSON.");
        }
    }
}
