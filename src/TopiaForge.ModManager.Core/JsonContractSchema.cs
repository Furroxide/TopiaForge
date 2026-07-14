using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Describes which serialized JSON members belong to a typed config contract and which are nested schema
    /// objects. Collection contents remain data rather than schema so a save can remove dictionary/list entries.
    /// </summary>
    internal sealed class JsonContractSchema
    {
        private JsonContractSchema()
        {
        }

        public ISet<string> KnownMembers { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IDictionary<string, JsonContractSchema> ObjectMembers { get; } =
            new Dictionary<string, JsonContractSchema>(StringComparer.Ordinal);

        public static JsonContractSchema Build(Type contractType)
        {
            if (contractType == null)
            {
                throw new ArgumentNullException(nameof(contractType));
            }

            return Build(contractType, new Dictionary<Type, JsonContractSchema>());
        }

        private static JsonContractSchema Build(
            Type contractType,
            IDictionary<Type, JsonContractSchema> schemas)
        {
            contractType = Nullable.GetUnderlyingType(contractType) ?? contractType;
            if (schemas.TryGetValue(contractType, out var cached))
            {
                return cached;
            }

            var schema = new JsonContractSchema();
            schemas[contractType] = schema; // publish before walking members so recursive contracts terminate
            foreach (var member in GetSerializedMembers(contractType))
            {
                schema.KnownMembers.Add(member.Name);
                if (CanMergeAsContractObject(member.Type))
                {
                    schema.ObjectMembers[member.Name] = Build(member.Type, schemas);
                }
            }

            return schema;
        }

        private static IEnumerable<SerializedMember> GetSerializedMembers(Type contractType)
        {
            for (var cursor = contractType; cursor != null && cursor != typeof(object); cursor = cursor.BaseType)
            {
                var declaredContract = Attribute.IsDefined(cursor, typeof(DataContractAttribute), inherit: false);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                foreach (var field in cursor.GetFields(flags))
                {
                    var dataMember = GetDataMember(field);
                    if (declaredContract ? dataMember == null : !field.IsPublic || IsIgnored(field))
                    {
                        continue;
                    }

                    yield return new SerializedMember(MemberName(field.Name, dataMember), field.FieldType);
                }

                foreach (var property in cursor.GetProperties(flags))
                {
                    var dataMember = GetDataMember(property);
                    var publicAccessors = property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true;
                    if (property.GetIndexParameters().Length != 0
                        || (declaredContract ? dataMember == null : !publicAccessors || IsIgnored(property)))
                    {
                        continue;
                    }

                    yield return new SerializedMember(MemberName(property.Name, dataMember), property.PropertyType);
                }
            }
        }

        private static DataMemberAttribute? GetDataMember(MemberInfo member)
        {
            return (DataMemberAttribute?)Attribute.GetCustomAttribute(
                member,
                typeof(DataMemberAttribute),
                inherit: false);
        }

        private static bool IsIgnored(MemberInfo member)
        {
            return Attribute.IsDefined(member, typeof(IgnoreDataMemberAttribute), inherit: false);
        }

        private static string MemberName(string fallback, DataMemberAttribute? dataMember)
        {
            return string.IsNullOrEmpty(dataMember?.Name) ? fallback : dataMember!.Name;
        }

        private static bool CanMergeAsContractObject(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(object)
                || type == typeof(string)
                || type.IsPrimitive
                || type.IsEnum
                || type.IsPointer
                || type.IsByRef
                || typeof(IEnumerable).IsAssignableFrom(type))
            {
                return false;
            }

            // DataContractJsonSerializer represents these framework value types as scalar strings/numbers rather
            // than member objects. Other user-defined classes and structs can carry nested schema fields.
            if (type.Namespace == "System")
            {
                return false;
            }

            return type.IsClass || type.IsValueType;
        }

        private readonly struct SerializedMember
        {
            public SerializedMember(string name, Type type)
            {
                Name = name;
                Type = type;
            }

            public string Name { get; }
            public Type Type { get; }
        }
    }
}
