using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Produces and verifies a deterministic metadata-level baseline for the public mod SDK.
    /// Exact comparison makes every public change reviewable; the assembly version remains stable
    /// at 0.1.0.0 while the 0.1 line accepts additive changes only.
    /// </summary>
    internal static class SdkPublicApiBaselineTests
    {
        private const string BaselineResourceName =
            "TopiaForge.ModManager.Tests.topiaforge.mods.abstractions.api.txt";

        public static void Run()
        {
            var actual = CreateBaseline();
            var assembly = typeof(SdkPublicApiBaselineTests).Assembly;
            using var stream = assembly.GetManifestResourceStream(BaselineResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    "The embedded SDK API baseline is missing. Restore " +
                    "baselines/topiaforge.mods.abstractions.api.txt.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var expected = Normalize(reader.ReadToEnd());
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(BuildDifference(expected, actual));
            }

            Console.WriteLine("SDK public API baseline passed.");
        }

        public static string CreateBaseline()
        {
            var sdkAssembly = typeof(ITopiaForgeMod).Assembly;
            var lines = new List<string>
            {
                "# TopiaForge.Mods.Abstractions public API baseline",
                "# Format version: 2",
                "# Regenerate with: dotnet run --project tests/TopiaForge.ModManager.Tests -c Release -- --print-sdk-api-baseline",
                "# Review every change. The stable 0.1 line permits additive API changes only.",
                string.Empty,
                "assembly " + sdkAssembly.GetName().FullName,
                "target-framework " + (sdkAssembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown"),
                string.Empty
            };

            var nullability = new NullabilityInfoContext();
            var types = sdkAssembly.GetExportedTypes()
                .OrderBy(type => FormatType(type, null), StringComparer.Ordinal)
                .ToArray();
            foreach (var type in types)
            {
                AppendType(lines, type, nullability);
            }

            return Normalize(string.Join("\n", lines));
        }

        private static void AppendType(List<string> lines, Type type, NullabilityInfoContext nullability)
        {
            lines.Add(FormatTypeDeclaration(type));

            var members = new List<string>();
            const BindingFlags declaredPublic = BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var constructor in type.GetConstructors(declaredPublic))
            {
                members.Add("  constructor " + FormatMethodFlags(constructor) +
                            ".ctor(" + FormatParameters(constructor.GetParameters(), nullability) + ")" +
                            FormatObsolete(constructor));
            }

            foreach (var field in type.GetFields(declaredPublic))
            {
                var modifiers = field.IsLiteral
                    ? "const "
                    : field.IsStatic
                        ? field.IsInitOnly ? "static readonly " : "static "
                        : field.IsInitOnly ? "readonly " : string.Empty;
                var line = "  field " + modifiers + FormatType(field.FieldType, nullability.Create(field)) +
                           " " + field.Name;
                if (field.IsLiteral)
                {
                    line += " = " + FormatValue(field.GetRawConstantValue(), field.FieldType);
                }

                members.Add(line + FormatObsolete(field));
            }

            foreach (var property in type.GetProperties(declaredPublic))
            {
                var getter = property.GetMethod?.IsPublic == true ? property.GetMethod : null;
                var setter = property.SetMethod?.IsPublic == true ? property.SetMethod : null;
                var accessor = getter ?? setter;
                var propertyNullability = getter != null
                    ? nullability.Create(getter.ReturnParameter)
                    : setter != null
                        ? nullability.Create(setter.GetParameters()[^1])
                        : null;
                var indexParameters = property.GetIndexParameters();
                var index = indexParameters.Length == 0
                    ? string.Empty
                    : "[" + FormatParameters(indexParameters, nullability) + "]";
                var accessors = new List<string>();
                if (getter != null)
                {
                    accessors.Add("get" + FormatAccessorFlags(getter));
                }

                if (setter != null)
                {
                    var initOnly = setter.ReturnParameter.GetRequiredCustomModifiers()
                        .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                    accessors.Add((initOnly ? "init" : "set") + FormatAccessorFlags(setter));
                }

                members.Add("  property " + (accessor?.IsStatic == true ? "static " : string.Empty) +
                            FormatType(property.PropertyType, propertyNullability) + " " + property.Name + index +
                            " { " + string.Join("; ", accessors) + "; }" + FormatObsolete(property));
            }

            foreach (var eventInfo in type.GetEvents(declaredPublic))
            {
                var add = eventInfo.AddMethod?.IsPublic == true ? eventInfo.AddMethod : null;
                var remove = eventInfo.RemoveMethod?.IsPublic == true ? eventInfo.RemoveMethod : null;
                var accessor = add ?? remove;
                var accessors = new List<string>();
                if (add != null)
                {
                    accessors.Add("add" + FormatAccessorFlags(add));
                }

                if (remove != null)
                {
                    accessors.Add("remove" + FormatAccessorFlags(remove));
                }

                members.Add("  event " + (accessor?.IsStatic == true ? "static " : string.Empty) +
                            FormatType(eventInfo.EventHandlerType!, nullability.Create(eventInfo)) + " " +
                            eventInfo.Name + " { " + string.Join("; ", accessors) + "; }" +
                            FormatObsolete(eventInfo));
            }

            foreach (var method in type.GetMethods(declaredPublic))
            {
                if (method.IsSpecialName &&
                    (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                     method.Name.StartsWith("set_", StringComparison.Ordinal) ||
                     method.Name.StartsWith("add_", StringComparison.Ordinal) ||
                     method.Name.StartsWith("remove_", StringComparison.Ordinal)))
                {
                    continue;
                }

                var genericArguments = method.IsGenericMethodDefinition
                    ? "<" + string.Join(", ", method.GetGenericArguments().Select(FormatGenericParameter)) + ">"
                    : string.Empty;
                var extension = method.IsDefined(typeof(ExtensionAttribute), false) ? "extension " : string.Empty;
                var constraints = FormatGenericConstraints(method.GetGenericArguments());
                members.Add("  method " + extension + FormatMethodFlags(method) +
                            FormatType(method.ReturnType, nullability.Create(method.ReturnParameter)) + " " +
                            method.Name + genericArguments + "(" +
                            FormatParameters(method.GetParameters(), nullability) + ")" + constraints +
                            FormatObsolete(method));
            }

            members.Sort(StringComparer.Ordinal);
            lines.AddRange(members);
            lines.Add(string.Empty);
        }

        private static string FormatTypeDeclaration(Type type)
        {
            var kind = type.IsEnum
                ? (type.IsDefined(typeof(FlagsAttribute), false) ? "flags enum" : "enum")
                : type.IsInterface
                    ? "interface"
                    : typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)
                        ? "delegate"
                        : type.IsValueType
                            ? (type.IsDefined(typeof(IsReadOnlyAttribute), false) ? "readonly struct" : "struct")
                            : type.IsAbstract && type.IsSealed
                                ? "static class"
                                : type.IsAbstract
                                    ? "abstract class"
                                    : type.IsSealed ? "sealed class" : "class";
            var declaration = "type public " + kind + " " + FormatType(type, null);
            if (type.IsEnum)
            {
                declaration += " : " + FormatType(Enum.GetUnderlyingType(type), null);
            }
            else
            {
                var inheritance = new List<string>();
                if (!type.IsInterface && type.BaseType != null)
                {
                    inheritance.Add(FormatType(type.BaseType, null));
                }

                inheritance.AddRange(type.GetInterfaces()
                    .Select(interfaceType => FormatType(interfaceType, null))
                    .OrderBy(name => name, StringComparer.Ordinal));
                if (inheritance.Count != 0)
                {
                    declaration += " : " + string.Join(", ", inheritance);
                }
            }

            declaration += FormatGenericConstraints(type.GetGenericArguments());
            return declaration + FormatObsolete(type);
        }

        private static string FormatParameters(
            IReadOnlyList<ParameterInfo> parameters,
            NullabilityInfoContext nullability)
        {
            return string.Join(", ", parameters.Select(parameter =>
            {
                var modifier = parameter.IsDefined(typeof(ParamArrayAttribute), false)
                    ? "params "
                    : parameter.ParameterType.IsByRef
                        ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref "
                        : string.Empty;
                var type = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()!
                    : parameter.ParameterType;
                var formatted = modifier + FormatType(type, nullability.Create(parameter)) + " " + parameter.Name;
                if (parameter.HasDefaultValue)
                {
                    formatted += " = " + FormatValue(parameter.DefaultValue, type);
                }

                return formatted;
            }));
        }

        private static string FormatMethodFlags(MethodBase method)
        {
            var flags = new List<string>();
            if (method.IsStatic)
            {
                flags.Add("static");
            }

            if (method.IsAbstract)
            {
                flags.Add("abstract");
            }

            if (method.IsVirtual)
            {
                flags.Add("virtual");
            }

            if (method.IsFinal)
            {
                flags.Add("final");
            }

            if ((method.Attributes & MethodAttributes.NewSlot) != 0)
            {
                flags.Add("newslot");
            }

            return flags.Count == 0 ? string.Empty : "[" + string.Join(",", flags) + "] ";
        }

        private static string FormatAccessorFlags(MethodInfo method)
        {
            var flags = FormatMethodFlags(method).TrimEnd();
            return flags.Length == 0 ? string.Empty : " " + flags;
        }

        private static string FormatGenericConstraints(IEnumerable<Type> arguments)
        {
            var clauses = new List<string>();
            foreach (var argument in arguments.Where(argument => argument.IsGenericParameter))
            {
                var constraints = new List<string>();
                var attributes = argument.GenericParameterAttributes;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                {
                    constraints.Add("class");
                }
                else if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                {
                    constraints.Add("struct");
                }

                constraints.AddRange(argument.GetGenericParameterConstraints()
                    .Select(constraint => FormatType(constraint, null))
                    .Where(constraint => constraint != "System.ValueType")
                    .OrderBy(constraint => constraint, StringComparer.Ordinal));
                if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                    (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
                {
                    constraints.Add("new()");
                }

                if (constraints.Count != 0)
                {
                    clauses.Add(" where " + argument.Name + " : " + string.Join(", ", constraints));
                }
            }

            return string.Concat(clauses);
        }

        private static string FormatGenericParameter(Type argument)
        {
            var variance = argument.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
            return variance == GenericParameterAttributes.Covariant
                ? "out " + argument.Name
                : variance == GenericParameterAttributes.Contravariant
                    ? "in " + argument.Name
                    : argument.Name;
        }

        private static string FormatType(Type type, NullabilityInfo? nullability)
        {
            if (type.IsByRef || type.IsPointer)
            {
                var suffix = type.IsByRef ? "&" : "*";
                return FormatType(type.GetElementType()!, nullability?.ElementType) + suffix;
            }

            if (type.IsArray)
            {
                var ranks = type.GetArrayRank() == 1 ? string.Empty : new string(',', type.GetArrayRank() - 1);
                return FormatType(type.GetElementType()!, nullability?.ElementType) + "[" + ranks + "]" +
                       FormatNullableSuffix(type, nullability);
            }

            if (type.IsGenericParameter)
            {
                return type.Name + FormatNullableSuffix(type, nullability);
            }

            var namedType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            var name = RemoveGenericArity((namedType.FullName ?? namedType.Name).Replace('+', '.'));
            if (type.IsGenericType)
            {
                var arguments = type.GetGenericArguments();
                var nullableArguments = nullability?.GenericTypeArguments;
                name += "<" + string.Join(", ", arguments.Select((argument, index) =>
                    FormatType(argument, nullableArguments != null && index < nullableArguments.Length
                        ? nullableArguments[index]
                        : null))) + ">";
            }

            return name + FormatNullableSuffix(type, nullability);
        }

        private static string FormatNullableSuffix(Type type, NullabilityInfo? nullability)
        {
            return !type.IsValueType && nullability?.ReadState == NullabilityState.Nullable ? "?" : string.Empty;
        }

        private static string RemoveGenericArity(string name)
        {
            var builder = new StringBuilder(name.Length);
            for (var index = 0; index < name.Length; index++)
            {
                if (name[index] != '`')
                {
                    builder.Append(name[index]);
                    continue;
                }

                while (index + 1 < name.Length && char.IsDigit(name[index + 1]))
                {
                    index++;
                }
            }

            return builder.ToString();
        }

        private static string FormatValue(object? value, Type declaredType)
        {
            if (value == null)
            {
                return "null";
            }

            if (ReferenceEquals(value, Missing.Value) || ReferenceEquals(value, DBNull.Value))
            {
                return "missing";
            }

            if (value is string text)
            {
                return JsonSerializer.Serialize(text);
            }

            if (value is char character)
            {
                return "U+" + ((int)character).ToString("X4", CultureInfo.InvariantCulture);
            }

            if (value is bool boolean)
            {
                return boolean ? "true" : "false";
            }

            if (declaredType.IsEnum)
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) +
                       " (" + (Enum.GetName(declaredType, value) ?? "unnamed") + ")";
            }

            if (value is float single)
            {
                return single.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value is double @double)
            {
                return @double.ToString("R", CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "unknown";
        }

        private static string FormatObsolete(MemberInfo member)
        {
            var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
            return obsolete == null
                ? string.Empty
                : " [obsolete message=" + JsonSerializer.Serialize(obsolete.Message ?? string.Empty) +
                  " error=" + (obsolete.IsError ? "true" : "false") + "]";
        }

        private static string Normalize(string value)
        {
            return value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd('\n') + "\n";
        }

        private static string BuildDifference(string expected, string actual)
        {
            var expectedLines = expected.Split('\n');
            var actualLines = actual.Split('\n');
            var removed = expectedLines.Except(actualLines, StringComparer.Ordinal).Take(25).ToArray();
            var added = actualLines.Except(expectedLines, StringComparer.Ordinal).Take(25).ToArray();
            var builder = new StringBuilder(
                "TopiaForge.Mods.Abstractions public API differs from its reviewed baseline. " +
                "Breaking changes are forbidden in the stable 0.1 assembly line; additive changes require review " +
                "and an intentional baseline refresh.\n");
            foreach (var line in removed)
            {
                builder.Append("- ").AppendLine(line);
            }

            foreach (var line in added)
            {
                builder.Append("+ ").AppendLine(line);
            }

            if (removed.Length == 0 && added.Length == 0)
            {
                var sharedLength = Math.Min(expectedLines.Length, actualLines.Length);
                var mismatch = Enumerable.Range(0, sharedLength)
                    .FirstOrDefault(index => !string.Equals(
                        expectedLines[index], actualLines[index], StringComparison.Ordinal), -1);
                if (mismatch >= 0)
                {
                    builder.Append("First ordering/duplicate mismatch at line ")
                        .Append(mismatch + 1)
                        .AppendLine(":")
                        .Append("- ").AppendLine(expectedLines[mismatch])
                        .Append("+ ").AppendLine(actualLines[mismatch]);
                }
                else if (expectedLines.Length != actualLines.Length)
                {
                    builder.Append("Line count changed from ")
                        .Append(expectedLines.Length)
                        .Append(" to ")
                        .Append(actualLines.Length)
                        .AppendLine(".");
                }
            }

            builder.Append("To inspect the complete candidate surface, run: dotnet run --project ")
                .Append("tests/TopiaForge.ModManager.Tests -c Release -- --print-sdk-api-baseline");
            return builder.ToString();
        }
    }
}
