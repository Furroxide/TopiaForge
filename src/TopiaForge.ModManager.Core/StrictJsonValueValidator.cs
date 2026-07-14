using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Strict RFC 8259-style JSON value grammar used before preserving raw config members.</summary>
    internal static class StrictJsonValueValidator
    {
        private const int MaxContainerDepth = 128;

        public static void Validate(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var reader = new Reader(json);
            reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("Unexpected content after JSON value");
            }
        }

        private sealed class Reader
        {
            private readonly string json;
            private int index;

            public Reader(string json)
            {
                this.json = json;
            }

            public bool AtEnd => index == json.Length;

            public void ReadValue()
            {
                ReadValue(containerDepth: 0);
            }

            private void ReadValue(int containerDepth)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("Expected JSON value");
                }

                switch (json[index])
                {
                    case '{':
                        RequireContainerDepth(containerDepth);
                        ReadObject(containerDepth + 1);
                        return;
                    case '[':
                        RequireContainerDepth(containerDepth);
                        ReadArray(containerDepth + 1);
                        return;
                    case '"': ReadString(); return;
                    case 't': ReadLiteral("true"); return;
                    case 'f': ReadLiteral("false"); return;
                    case 'n': ReadLiteral("null"); return;
                    default:
                        ReadNumber();
                        return;
                }
            }

            public void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    var ch = json[index];
                    if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
                    {
                        return;
                    }

                    index++;
                }
            }

            public FormatException Error(string message)
            {
                return new FormatException(message + " at JSON offset " + index + ".");
            }

            private void ReadObject(int containerDepth)
            {
                index++;
                SkipWhitespace();
                if (Consume('}'))
                {
                    return;
                }

                while (true)
                {
                    if (AtEnd || json[index] != '"')
                    {
                        throw Error("Expected JSON object member name");
                    }

                    ReadString();
                    SkipWhitespace();
                    Require(':');
                    ReadValue(containerDepth);
                    SkipWhitespace();
                    if (Consume('}'))
                    {
                        return;
                    }

                    Require(',');
                    SkipWhitespace();
                }
            }

            private void ReadArray(int containerDepth)
            {
                index++;
                SkipWhitespace();
                if (Consume(']'))
                {
                    return;
                }

                while (true)
                {
                    ReadValue(containerDepth);
                    SkipWhitespace();
                    if (Consume(']'))
                    {
                        return;
                    }

                    Require(',');
                    SkipWhitespace();
                }
            }

            private void ReadString()
            {
                Require('"');
                while (!AtEnd)
                {
                    var ch = json[index++];
                    if (ch == '"')
                    {
                        return;
                    }

                    if (ch < 0x20)
                    {
                        throw Error("Unescaped control character in JSON string");
                    }

                    if (ch != '\\')
                    {
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("Unterminated JSON escape");
                    }

                    var escaped = json[index++];
                    if (escaped == '"' || escaped == '\\' || escaped == '/' || escaped == 'b'
                        || escaped == 'f' || escaped == 'n' || escaped == 'r' || escaped == 't')
                    {
                        continue;
                    }

                    if (escaped != 'u')
                    {
                        throw Error("Invalid JSON escape");
                    }

                    for (var digit = 0; digit < 4; digit++)
                    {
                        if (AtEnd || !IsHex(json[index++]))
                        {
                            throw Error("Invalid JSON unicode escape");
                        }
                    }
                }

                throw Error("Unterminated JSON string");
            }

            private void ReadLiteral(string literal)
            {
                for (var offset = 0; offset < literal.Length; offset++)
                {
                    if (AtEnd || json[index++] != literal[offset])
                    {
                        throw Error("Invalid JSON literal");
                    }
                }
            }

            private void ReadNumber()
            {
                Consume('-');
                if (AtEnd)
                {
                    throw Error("Invalid JSON number");
                }

                if (Consume('0'))
                {
                    if (!AtEnd && IsDigit(json[index]))
                    {
                        throw Error("A JSON number cannot have a leading zero");
                    }
                }
                else
                {
                    if (AtEnd || json[index] < '1' || json[index] > '9')
                    {
                        throw Error("Invalid JSON number");
                    }

                    while (!AtEnd && IsDigit(json[index]))
                    {
                        index++;
                    }
                }

                if (Consume('.'))
                {
                    ReadRequiredDigits("JSON fraction requires a digit");
                }

                if (!AtEnd && (json[index] == 'e' || json[index] == 'E'))
                {
                    index++;
                    if (!AtEnd && (json[index] == '+' || json[index] == '-'))
                    {
                        index++;
                    }

                    ReadRequiredDigits("JSON exponent requires a digit");
                }
            }

            private void ReadRequiredDigits(string error)
            {
                if (AtEnd || !IsDigit(json[index]))
                {
                    throw Error(error);
                }

                while (!AtEnd && IsDigit(json[index]))
                {
                    index++;
                }
            }

            private void RequireContainerDepth(int containerDepth)
            {
                if (containerDepth >= MaxContainerDepth)
                {
                    throw Error("JSON container nesting exceeds the " + MaxContainerDepth + " level limit");
                }
            }

            private bool Consume(char expected)
            {
                if (AtEnd || json[index] != expected)
                {
                    return false;
                }

                index++;
                return true;
            }

            private void Require(char expected)
            {
                if (!Consume(expected))
                {
                    throw Error("Expected '" + expected + "'");
                }
            }

            private static bool IsDigit(char ch)
            {
                return ch >= '0' && ch <= '9';
            }

            private static bool IsHex(char ch)
            {
                return IsDigit(ch)
                    || (ch >= 'a' && ch <= 'f')
                    || (ch >= 'A' && ch <= 'F');
            }
        }
    }
}
