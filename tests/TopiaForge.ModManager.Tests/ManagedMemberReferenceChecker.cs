using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Proves that every type and member a compiled assembly references in a given dependency also exists, with
    /// the same decoded signature, in another copy of that dependency. Used where the game preloads its own copy
    /// of an assembly the loader was compiled against: whichever copy the runtime binds, the references resolve.
    /// </summary>
    internal static class ManagedMemberReferenceChecker
    {
        internal static IReadOnlyList<string> FindUnsatisfied(string consumerPath, string dependencyName,
            string candidatePath, out int checkedReferences)
        {
            using var consumerStream = File.OpenRead(consumerPath);
            using var consumerPe = new PEReader(consumerStream);
            using var candidateStream = File.OpenRead(candidatePath);
            using var candidatePe = new PEReader(candidateStream);
            var consumer = consumerPe.GetMetadataReader();
            var candidate = candidatePe.GetMetadataReader();
            var available = Index(candidate);
            var missing = new List<string>();
            checkedReferences = 0;

            foreach (var handle in consumer.TypeReferences)
            {
                var name = TypeReferenceName(consumer, handle, dependencyName);
                if (name == null) continue;
                checkedReferences++;
                if (!available.Types.Contains(name)) missing.Add("type " + name);
            }

            var consumerTypes = new NameProvider(consumer);
            foreach (var handle in consumer.MemberReferences)
            {
                var reference = consumer.GetMemberReference(handle);
                var owner = OwnerName(consumer, reference.Parent, dependencyName);
                if (owner == null) continue;
                checkedReferences++;
                var member = consumer.GetString(reference.Name);
                var signature = reference.GetKind() == MemberReferenceKind.Method
                    ? MethodKey(reference.DecodeMethodSignature(consumerTypes, null))
                    : "field " + reference.DecodeFieldSignature(consumerTypes, null);
                var key = owner + "::" + member + " " + signature;
                if (!available.Members.Contains(key)) missing.Add(key);
            }

            return missing;
        }

        private static (HashSet<string> Types, HashSet<string> Members) Index(MetadataReader reader)
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            var members = new HashSet<string>(StringComparer.Ordinal);
            var provider = new NameProvider(reader);
            foreach (var handle in reader.TypeDefinitions)
            {
                var name = TypeDefinitionName(reader, handle);
                types.Add(name);
                var definition = reader.GetTypeDefinition(handle);
                foreach (var methodHandle in definition.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    members.Add(name + "::" + reader.GetString(method.Name) + " "
                        + MethodKey(method.DecodeSignature(provider, null)));
                }

                foreach (var fieldHandle in definition.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    members.Add(name + "::" + reader.GetString(field.Name) + " field "
                        + field.DecodeSignature(provider, null));
                }
            }

            foreach (var handle in reader.ExportedTypes)
            {
                var exported = reader.GetExportedType(handle);
                types.Add(Join(reader.GetString(exported.Namespace), reader.GetString(exported.Name)));
            }

            return (types, members);
        }

        private static string MethodKey(MethodSignature<string> signature) =>
            (signature.Header.IsInstance ? "instance " : "static ") + signature.GenericParameterCount + " "
            + signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + ")";

        private static string? OwnerName(MetadataReader reader, EntityHandle parent, string dependencyName)
        {
            if (parent.Kind == HandleKind.TypeReference)
                return TypeReferenceName(reader, (TypeReferenceHandle)parent, dependencyName);
            if (parent.Kind != HandleKind.TypeSpecification) return null;
            // A generic instantiation: members are declared on the open generic type definition.
            var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
            blob.ReadSignatureTypeCode(); // CLASS or VALUETYPE, both reported as TypeHandle
            var generic = blob.ReadTypeHandle();
            return generic.Kind == HandleKind.TypeReference
                ? TypeReferenceName(reader, (TypeReferenceHandle)generic, dependencyName)
                : null;
        }

        private static string? TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle,
            string dependencyName)
        {
            var reference = reader.GetTypeReference(handle);
            var scope = reference.ResolutionScope;
            string? prefix;
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                if (reader.GetString(assembly.Name) != dependencyName) return null;
                prefix = null;
            }
            else if (scope.Kind == HandleKind.TypeReference)
            {
                prefix = TypeReferenceName(reader, (TypeReferenceHandle)scope, dependencyName);
                if (prefix == null) return null;
            }
            else
            {
                return null;
            }

            var own = Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
            return prefix == null ? own : prefix + "/" + own;
        }

        private static string TypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            var own = Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
            var declaring = definition.GetDeclaringType();
            return declaring.IsNil ? own : TypeDefinitionName(reader, declaring) + "/" + own;
        }

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        /// <summary>Decodes signatures to assembly-independent type names.</summary>
        private sealed class NameProvider : ISignatureTypeProvider<string, object?>
        {
            private readonly MetadataReader reader;

            internal NameProvider(MetadataReader reader) => this.reader = reader;

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) =>
                TypeDefinitionName(r, handle);
            public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var reference = r.GetTypeReference(handle);
                var own = Join(r.GetString(reference.Namespace), r.GetString(reference.Name));
                return reference.ResolutionScope.Kind == HandleKind.TypeReference
                    ? GetTypeFromReference(r, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "/" + own
                    : own;
            }
            public string GetTypeFromSpecification(MetadataReader r, object? context,
                TypeSpecificationHandle handle, byte rawTypeKind) =>
                r.GetTypeSpecification(handle).DecodeSignature(this, context);
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + shape.Rank + "]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType + " pinned";
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
                genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericTypeParameter(object? context, int index) => "!" + index;
            public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + MethodKey(signature);
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
                unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
        }
    }
}
