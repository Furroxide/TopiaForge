using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TopiaForge.Mods.Multiplayer.Generators
{
    /// <summary>Generates bounded codecs, registration, descriptors, and typed submission proxies for multiplayer contracts.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class MultiplayerContractGenerator : IIncrementalGenerator
    {
        internal const int WireFormatRevision = 1;

        private const string ContractAttribute = "TopiaForge.Mods.MultiplayerContractAttribute";
        private const string StateAttribute = "TopiaForge.Mods.ReplicatedStateAttribute";
        private const string ObjectAttribute = "TopiaForge.Mods.ReplicatedObjectAttribute";
        private const string CommandAttribute = "TopiaForge.Mods.MultiplayerCommandAttribute";
        private const string EventAttribute = "TopiaForge.Mods.PresentationEventAttribute";
        private const string BoundAttribute = "TopiaForge.Mods.NetworkBoundAttribute";
        private const string ReplicatedStateType = "TopiaForge.Mods.ReplicatedState<T>";
        private const string OperationResultType = "TopiaForge.Mods.OperationResult<T>";

        private static readonly DiagnosticDescriptor PartialRequired = new DiagnosticDescriptor(
            "TFMP001",
            "Multiplayer contract must be partial",
            "Multiplayer contract '{0}' must be a non-generic top-level partial class",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor InvalidCommand = new DiagnosticDescriptor(
            "TFMP002",
            "Invalid multiplayer command",
            "Command '{0}' must be a non-static method with signature OperationResult<TResponse> Method(MultiplayerCommandContext, TRequest)",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor UnsupportedType = new DiagnosticDescriptor(
            "TFMP003",
            "Unsupported multiplayer payload type",
            "Type '{0}' is not supported by generated multiplayer codecs: {1}",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor MissingBound = new DiagnosticDescriptor(
            "TFMP004",
            "Unbounded multiplayer payload",
            "Member '{0}' must declare [NetworkBound(maximum)] because strings and collections are unbounded on the wire",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor PredictedNondeterminism = new DiagnosticDescriptor(
            "TFMP005",
            "Predicted command is nondeterministic",
            "Predicted command '{0}' uses nondeterministic API '{1}'",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor PredictedSideEffect = new DiagnosticDescriptor(
            "TFMP006",
            "Predicted command performs a process-local side effect",
            "Predicted command '{0}' accesses '{1}'; predicted handlers may only mutate generated replicated state and emit buffered presentation events",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor DuplicateId = new DiagnosticDescriptor(
            "TFMP007",
            "Duplicate multiplayer wire id",
            "Wire id '{0}' is declared more than once in contract '{1}'",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor PayloadLimitExceeded = new DiagnosticDescriptor(
            "TFMP008",
            "Multiplayer command payload exceeds its limit",
            "Command '{0}' declares a {1}-byte payload limit, but its generated request or response codec can require {2} bytes",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor InvalidId = new DiagnosticDescriptor(
            "TFMP009",
            "Invalid multiplayer wire id",
            "Wire id '{0}' must be non-empty, contain no control characters, and remain at most 128 characters after contract namespacing",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor InvalidObject = new DiagnosticDescriptor(
            "TFMP010",
            "Invalid replicated object handler",
            "Replicated object handler '{0}' must have signature OperationResult<TState> Method(ReplicatedObjectCommandContext, TState, TInput)",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor TransportPayloadLimitExceeded = new DiagnosticDescriptor(
            "TFMP011",
            "Multiplayer payload exceeds the transport limit",
            "{0} '{1}' has a generated codec bound of {2} bytes, above the 1048576-byte transport limit",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor GeneratedNameCollision = new DiagnosticDescriptor(
            "TFMP012",
            "Generated multiplayer member name collision",
            "Wire declaration '{0}' would generate member '{1}', which collides with another generated or author-defined member in contract '{2}'",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor ExplicitContractIdRequired = new DiagnosticDescriptor(
            "TFMP013",
            "Multiplayer contract id is required",
            "Multiplayer contract '{0}' must declare an explicit stable Id; class and namespace names are not wire identities",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true);

        private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <inheritdoc/>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
                    ContractAttribute,
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (syntaxContext, _) => (INamedTypeSymbol)syntaxContext.TargetSymbol)
                .Where(static symbol => symbol != null);

            context.RegisterSourceOutput(
                contracts.Combine(context.CompilationProvider),
                static (productionContext, pair) => Generate(productionContext, pair.Right, pair.Left));
        }

        private static void Generate(SourceProductionContext context, Compilation compilation, INamedTypeSymbol contract)
        {
            var declaration = contract.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ClassDeclarationSyntax;
            if (declaration == null || contract.ContainingType != null || contract.TypeParameters.Length != 0 ||
                !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                context.ReportDiagnostic(Diagnostic.Create(PartialRequired, declaration?.Identifier.GetLocation(), contract.Name));
                return;
            }

            var contractAttribute = FindAttribute(contract, ContractAttribute);
            var configuredId = contractAttribute?.NamedArguments.FirstOrDefault(pair => pair.Key == "Id").Value.Value as string;
            var missingContractId = string.IsNullOrWhiteSpace(configuredId);
            if (missingContractId)
                context.ReportDiagnostic(Diagnostic.Create(
                    ExplicitContractIdRequired,
                    declaration.Identifier.GetLocation(),
                    contract.Name));
            var contractId = missingContractId ? contract.ToDisplayString().ToLowerInvariant() : configuredId!;
            if (!IsValidIdentity(contractId))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidId, declaration.Identifier.GetLocation(), contractId));
                return;
            }

            var states = new List<StateModel>();
            var commands = new List<CommandModel>();
            var objects = new List<ObjectModel>();
            var events = new List<EventModel>();
            var predictedMethods = new List<IMethodSymbol>();
            var ids = new Dictionary<string, Location>(StringComparer.Ordinal);
            var invalid = missingContractId;

            foreach (var field in contract.GetMembers().OfType<IFieldSymbol>())
            {
                var attribute = FindAttribute(field, StateAttribute);
                if (attribute == null) continue;
                var id = GetConstructorString(attribute);
                if (!RegisterId(context, contract, contractId, ids, id, field.Locations.FirstOrDefault())) invalid = true;
                if (field.IsStatic || field.IsReadOnly || field.Type is not INamedTypeSymbol named ||
                    named.ConstructedFrom.ToDisplayString() != ReplicatedStateType)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedType,
                        field.Locations.FirstOrDefault(),
                        field.Type.ToDisplayString(),
                        "[ReplicatedState] fields must be mutable instance fields of type ReplicatedState<T>"));
                    invalid = true;
                    continue;
                }

                states.Add(new StateModel(field, id, named.TypeArguments[0]));
            }

            foreach (var method in contract.GetMembers().OfType<IMethodSymbol>())
            {
                var objectAttribute = FindAttribute(method, ObjectAttribute);
                if (objectAttribute != null)
                {
                    var id = GetConstructorString(objectAttribute);
                    if (!RegisterId(context, contract, contractId, ids, id, method.Locations.FirstOrDefault())) invalid = true;
                    if (!TryCreateObject(method, objectAttribute, id, out var replicatedObject))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InvalidObject, method.Locations.FirstOrDefault(), method.Name));
                        invalid = true;
                    }
                    else
                    {
                        objects.Add(replicatedObject!);
                        if (replicatedObject!.Prediction == 1)
                            predictedMethods.Add(method);
                    }
                }

                var commandAttribute = FindAttribute(method, CommandAttribute);
                if (commandAttribute != null)
                {
                    var id = GetConstructorString(commandAttribute);
                    if (!RegisterId(context, contract, contractId, ids, id, method.Locations.FirstOrDefault())) invalid = true;
                    if (!TryCreateCommand(method, commandAttribute, id, out var command))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InvalidCommand, method.Locations.FirstOrDefault(), method.Name));
                        invalid = true;
                    }
                    else
                    {
                        commands.Add(command!);
                        if (command!.Prediction == 1)
                        {
                            predictedMethods.Add(method);
                        }
                    }
                }

                var eventAttribute = FindAttribute(method, EventAttribute);
                if (eventAttribute != null)
                {
                    var id = GetConstructorString(eventAttribute);
                    if (!RegisterId(context, contract, contractId, ids, id, method.Locations.FirstOrDefault())) invalid = true;
                    if (method.IsStatic || !method.ReturnsVoid || method.Parameters.Length != 1 ||
                        method.Parameters[0].Type.IsValueType)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedType,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString(),
                            "[PresentationEvent] methods must be instance void methods with one reference-type payload"));
                        invalid = true;
                    }
                    else
                    {
                        events.Add(new EventModel(method, id, method.Parameters[0].Type));
                    }
                }
            }

            var generatedPresentationEmitters = new HashSet<string>(
                events.Select(item => "Emit" + item.Method.Name),
                StringComparer.Ordinal);
            foreach (var predictedMethod in predictedMethods)
            {
                invalid |= ReportPredictedSafetyDiagnostics(
                    context,
                    compilation,
                    predictedMethod,
                    generatedPresentationEmitters);
            }

            invalid |= ReportGeneratedNameCollisions(context, contract, commands, objects, events);

            var roots = states.Select(state => state.ValueType)
                .Concat(commands.SelectMany(command => new[] { command.RequestType, command.ResponseType }))
                .Concat(objects.SelectMany(item => new[] { item.StateType, item.InputType }))
                .Concat(events.Select(item => item.PayloadType))
                .Concat(GetAdditionalCodecTypes(contractAttribute))
                .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)
                .ToArray();
            var codecModels = new List<CodecModel>();
            foreach (var root in roots)
            {
                CodecModel? codec;
                try
                {
                    if (!TryBuildCodec(
                            context,
                            compilation,
                            root,
                            new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                            out codec))
                    {
                        invalid = true;
                        continue;
                    }
                }
                catch (OverflowException)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedType,
                        root.Locations.FirstOrDefault(),
                        root.ToDisplayString(),
                        "the declared bounds exceed the maximum supported encoded payload size"));
                    invalid = true;
                    continue;
                }

                codecModels.Add(codec!);
            }

            foreach (var command in commands)
            {
                var requestCodec = codecModels.FirstOrDefault(codec =>
                    SymbolEqualityComparer.Default.Equals(codec.Type, command.RequestType));
                var responseCodec = codecModels.FirstOrDefault(codec =>
                    SymbolEqualityComparer.Default.Equals(codec.Type, command.ResponseType));
                var required = Math.Max(requestCodec?.MaximumBytes ?? 0, responseCodec?.MaximumBytes ?? 0);
                if (required > command.MaximumPayloadBytes)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        PayloadLimitExceeded,
                        command.Method.Locations.FirstOrDefault(),
                        command.Method.Name,
                        command.MaximumPayloadBytes,
                        required));
                    invalid = true;
                }
            }

            foreach (var state in states)
            {
                var codec = codecModels.FirstOrDefault(item =>
                    SymbolEqualityComparer.Default.Equals(item.Type, state.ValueType));
                if ((codec?.MaximumBytes ?? 0) <= 1024 * 1024) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    TransportPayloadLimitExceeded,
                    state.Field.Locations.FirstOrDefault(),
                    "Replicated state",
                    state.Id,
                    codec!.MaximumBytes));
                invalid = true;
            }

            foreach (var item in events)
            {
                var codec = codecModels.FirstOrDefault(codecModel =>
                    SymbolEqualityComparer.Default.Equals(codecModel.Type, item.PayloadType));
                if ((codec?.MaximumBytes ?? 0) <= 1024 * 1024) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    TransportPayloadLimitExceeded,
                    item.Method.Locations.FirstOrDefault(),
                    "Presentation event",
                    item.Id,
                    codec!.MaximumBytes));
                invalid = true;
            }

            foreach (var item in objects)
            {
                var stateCodec = codecModels.FirstOrDefault(codec =>
                    SymbolEqualityComparer.Default.Equals(codec.Type, item.StateType));
                var inputCodec = codecModels.FirstOrDefault(codec =>
                    SymbolEqualityComparer.Default.Equals(codec.Type, item.InputType));
                var required = Math.Max(stateCodec?.MaximumBytes ?? 0, inputCodec?.MaximumBytes ?? 0);
                if (required > item.MaximumPayloadBytes)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        PayloadLimitExceeded,
                        item.Method.Locations.FirstOrDefault(),
                        item.Method.Name,
                        item.MaximumPayloadBytes,
                        required));
                    invalid = true;
                }
            }

            if (invalid) return;

            var source = Render(contract, contractId, states, commands, objects, events, codecModels);
            var hintName = Sanitize(contract.ToDisplayString()) + ".Multiplayer.g.cs";
            context.AddSource(hintName, source);
        }

        private static bool RegisterId(
            SourceProductionContext context,
            INamedTypeSymbol contract,
            string contractId,
            IDictionary<string, Location> ids,
            string id,
            Location? location)
        {
            if (!IsValidIdentity(id) || !IsValidIdentity(Namespaced(contractId, id)))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidId, location, id));
                return false;
            }

            if (ids.ContainsKey(id))
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateId, location, id, contract.Name));
                return false;
            }

            ids.Add(id, location ?? Location.None);
            return true;
        }

        private static bool ReportGeneratedNameCollisions(
            SourceProductionContext context,
            INamedTypeSymbol contract,
            IEnumerable<CommandModel> commands,
            IEnumerable<ObjectModel> objects,
            IEnumerable<EventModel> events)
        {
            var names = new HashSet<string>(contract.GetMembers().Select(member => member.Name), StringComparer.Ordinal);
            var invalid = false;
            foreach (var name in new[] { "MultiplayerContractDescriptor", "GetCodec", "BindMultiplayer" })
            {
                if (names.Add(name)) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    GeneratedNameCollision,
                    contract.Locations.FirstOrDefault(),
                    contract.Name,
                    name,
                    contract.Name));
                invalid = true;
            }

            foreach (var command in commands)
            {
                foreach (var name in new[]
                         {
                             PublicName(command.Id) + "CommandType",
                             "Submit" + command.Method.Name + "Async"
                         })
                {
                    if (names.Add(name)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(
                        GeneratedNameCollision,
                        command.Method.Locations.FirstOrDefault(),
                        command.Id,
                        name,
                        contract.Name));
                    invalid = true;
                }
            }

            foreach (var item in objects)
            {
                var baseName = PublicName(item.Id);
                var generated = new[]
                {
                    baseName + "ObjectType",
                    "Spawn" + baseName + "Object",
                    "TryGet" + baseName + "Object",
                    "Get" + baseName + "Objects",
                    "Subscribe" + baseName + "Objects"
                };
                foreach (var name in generated)
                {
                    if (names.Add(name)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(
                        GeneratedNameCollision,
                        item.Method.Locations.FirstOrDefault(),
                        item.Id,
                        name,
                        contract.Name));
                    invalid = true;
                }
            }

            foreach (var item in events)
            {
                foreach (var name in new[]
                         {
                             PublicName(item.Id) + "EventType",
                             "Emit" + item.Method.Name,
                             "Publish" + item.Method.Name
                         })
                {
                    if (names.Add(name)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(
                        GeneratedNameCollision,
                        item.Method.Locations.FirstOrDefault(),
                        item.Id,
                        name,
                        contract.Name));
                    invalid = true;
                }
            }

            return invalid;
        }

        private static bool IsValidIdentity(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character => !char.IsControl(character));

        private static bool TryCreateCommand(
            IMethodSymbol method,
            AttributeData attribute,
            string id,
            out CommandModel? command)
        {
            command = null;
            if (method.IsStatic || method.IsAsync || method.IsGenericMethod || method.Parameters.Length != 2 ||
                method.Parameters[0].Type.ToDisplayString() != "TopiaForge.Mods.MultiplayerCommandContext" ||
                method.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
                method.Parameters[1].Type.IsValueType || method.ReturnType is not INamedTypeSymbol result ||
                result.ConstructedFrom.ToDisplayString() != OperationResultType || result.TypeArguments[0].IsValueType)
            {
                return false;
            }

            var prediction = GetNamedInt(attribute, "Prediction", 0);
            var rate = GetNamedInt(attribute, "MaximumPerSecond", 30);
            var maximumPayload = GetNamedInt(attribute, "MaximumPayloadBytes", 16 * 1024);
            if (prediction < 0 || prediction > 1 || rate < 1 || rate > 1000 || maximumPayload < 64 || maximumPayload > 1024 * 1024)
            {
                return false;
            }

            command = new CommandModel(method, id, method.Parameters[1].Type, result.TypeArguments[0], prediction, rate, maximumPayload);
            return true;
        }

        private static bool TryCreateObject(
            IMethodSymbol method,
            AttributeData attribute,
            string id,
            out ObjectModel? replicatedObject)
        {
            replicatedObject = null;
            if (method.IsAsync || method.IsGenericMethod || method.Parameters.Length != 3 ||
                method.Parameters[0].Type.ToDisplayString() != "TopiaForge.Mods.ReplicatedObjectCommandContext" ||
                method.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
                method.Parameters[1].Type.IsValueType || method.Parameters[2].Type.IsValueType ||
                method.ReturnType is not INamedTypeSymbol result ||
                result.ConstructedFrom.ToDisplayString() != OperationResultType ||
                !SymbolEqualityComparer.Default.Equals(result.TypeArguments[0], method.Parameters[1].Type))
            {
                return false;
            }

            var prediction = GetNamedInt(attribute, "Prediction", 0);
            var rate = GetNamedInt(attribute, "MaximumPerSecond", 30);
            var maximumPayload = GetNamedInt(attribute, "MaximumPayloadBytes", 16 * 1024);
            if (prediction < 0 || prediction > 1 || rate < 1 || rate > 1000 ||
                maximumPayload < 64 || maximumPayload > 1024 * 1024)
            {
                return false;
            }

            replicatedObject = new ObjectModel(
                method,
                id,
                method.Parameters[1].Type,
                method.Parameters[2].Type,
                prediction,
                rate,
                maximumPayload);
            return true;
        }

        private static bool ReportPredictedSafetyDiagnostics(
            SourceProductionContext context,
            Compilation compilation,
            IMethodSymbol method,
            ISet<string> generatedPresentationEmitters)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var inspectedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            return ReportPredictedMethodSafetyDiagnostics(
                context,
                compilation,
                method.Name,
                method,
                generatedPresentationEmitters,
                reported,
                inspectedMethods);
        }

        private static bool ReportPredictedMethodSafetyDiagnostics(
            SourceProductionContext context,
            Compilation compilation,
            string predictedMethodName,
            IMethodSymbol method,
            ISet<string> generatedPresentationEmitters,
            ISet<string> reported,
            ISet<IMethodSymbol> inspectedMethods)
        {
            method = method.PartialImplementationPart ?? method;
            if (!inspectedMethods.Add(method)) return false;

            var invalid = false;
            foreach (var syntaxReference in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax();
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                var root = GetPredictedMethodOperation(model, syntax);
                if (root == null) continue;

                foreach (var operation in DescendantsAndSelf(root))
                {
                    var symbol = ReferencedSymbol(operation);
                    var display = symbol?.ToDisplayString() ?? operation.Syntax.ToString();
                    if (IsNondeterministic(display))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedNondeterminism,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (IsProcessLocalSideEffect(display))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (operation is IInvocationOperation invocation &&
                             !IsAllowedPredictedInvocation(invocation))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (operation is IObjectCreationOperation creation &&
                             !IsAllowedPredictedObjectCreation(creation))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (operation is IObjectCreationOperation allowedCreation &&
                             allowedCreation.Type is INamedTypeSymbol createdType &&
                             TryFindUnsafeTypeInitialization(
                                 compilation,
                                 createdType,
                                 new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                                 out var initializerIssue))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            initializerIssue!.IsNondeterministic
                                ? PredictedNondeterminism
                                : PredictedSideEffect,
                            initializerIssue.Location,
                            predictedMethodName,
                            initializerIssue.Display);
                    }
                    else if (operation is IPropertyReferenceOperation property &&
                             !IsAllowedPredictedProperty(property))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (operation is IFieldReferenceOperation field &&
                             !IsAllowedPredictedField(field))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            display);
                    }
                    else if (operation is IMethodReferenceOperation methodReference)
                    {
                        if (!TryGetAllowedPredictedCallback(methodReference, out var callback) ||
                            !HasPredictedMethodBody(callback!))
                        {
                            invalid |= ReportPredictedDiagnostic(
                                context,
                                reported,
                                PredictedSideEffect,
                                operation.Syntax.GetLocation(),
                                predictedMethodName,
                                display);
                        }
                        else
                        {
                            invalid |= ReportPredictedMethodSafetyDiagnostics(
                                context,
                                compilation,
                                predictedMethodName,
                                callback!,
                                generatedPresentationEmitters,
                                reported,
                                inspectedMethods);
                        }
                    }
                    else if (IsUserDefinedOperation(operation, out var operatorMethod))
                    {
                        invalid |= ReportPredictedDiagnostic(
                            context,
                            reported,
                            PredictedSideEffect,
                            operation.Syntax.GetLocation(),
                            predictedMethodName,
                            operatorMethod!.ToDisplayString());
                    }
                }

                foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol != null ||
                        IsGeneratedPresentationEmitter(invocation, generatedPresentationEmitters)) continue;
                    invalid |= ReportPredictedDiagnostic(
                        context,
                        reported,
                        PredictedSideEffect,
                        invocation.GetLocation(),
                        predictedMethodName,
                        invocation.Expression.ToString());
                }
            }

            return invalid;
        }

        private static IOperation? GetPredictedMethodOperation(SemanticModel model, SyntaxNode syntax) => syntax switch
        {
            MethodDeclarationSyntax declaration => declaration.Body == null
                ? declaration.ExpressionBody == null ? null : model.GetOperation(declaration.ExpressionBody.Expression)
                : model.GetOperation(declaration.Body),
            LocalFunctionStatementSyntax local => local.Body == null
                ? local.ExpressionBody == null ? null : model.GetOperation(local.ExpressionBody.Expression)
                : model.GetOperation(local.Body),
            _ => model.GetOperation(syntax)
        };

        private static bool HasPredictedMethodBody(IMethodSymbol method)
        {
            method = method.PartialImplementationPart ?? method;
            if (method.IsAbstract || method.IsExtern) return false;
            return method.DeclaringSyntaxReferences.Any(reference =>
            {
                var syntax = reference.GetSyntax();
                return syntax is MethodDeclarationSyntax declaration &&
                       (declaration.Body != null || declaration.ExpressionBody != null) ||
                       syntax is LocalFunctionStatementSyntax local &&
                       (local.Body != null || local.ExpressionBody != null);
            });
        }

        private static bool TryGetAllowedPredictedCallback(
            IMethodReferenceOperation reference,
            out IMethodSymbol? callback)
        {
            callback = null;
            IOperation current = reference;
            while (current.Parent is IDelegateCreationOperation || current.Parent is IConversionOperation)
            {
                current = current.Parent;
            }

            if (current.Parent is not IArgumentOperation argument ||
                argument.Parent is not IInvocationOperation invocation ||
                argument.Parameter?.Type.TypeKind != TypeKind.Delegate ||
                !IsAllowedPredictedInvocation(invocation))
            {
                return false;
            }

            callback = reference.Method;
            return true;
        }

        private static bool ReportPredictedDiagnostic(
            SourceProductionContext context,
            ISet<string> reported,
            DiagnosticDescriptor descriptor,
            Location location,
            string methodName,
            string display)
        {
            var key = descriptor.Id + ":" + location.SourceSpan.Start + ":" + location.SourceSpan.Length;
            if (!reported.Add(key)) return false;
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, methodName, display));
            return true;
        }

        private static IEnumerable<IOperation> DescendantsAndSelf(IOperation root)
        {
            var stack = new Stack<IOperation>();
            stack.Push(root);
            while (stack.Count != 0)
            {
                var current = stack.Pop();
                yield return current;
                var children = current.ChildOperations.ToArray();
                for (var index = children.Length - 1; index >= 0; index--) stack.Push(children[index]);
            }
        }

        private static ISymbol? ReferencedSymbol(IOperation operation) => operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IMethodReferenceOperation methodReference => methodReference.Method,
            IObjectCreationOperation creation => creation.Constructor,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IBinaryOperation binary => binary.OperatorMethod,
            IUnaryOperation unary => unary.OperatorMethod,
            IConversionOperation conversion => conversion.OperatorMethod,
            ICompoundAssignmentOperation compound => compound.OperatorMethod,
            IIncrementOrDecrementOperation increment => increment.OperatorMethod,
            _ => null
        };

        private static bool IsAllowedPredictedInvocation(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            if (method.ContainingType?.ToDisplayString() == "TopiaForge.Mods.MultiplayerCommandContext" &&
                method.Name == "Emit") return true;
            if (method.ContainingType is INamedTypeSymbol containing &&
                containing.ConstructedFrom.ToDisplayString() == "TopiaForge.Mods.ReplicatedState<T>" &&
                method.Name == "Update") return true;
            if (method.ContainingType is INamedTypeSymbol operation &&
                operation.ConstructedFrom.ToDisplayString() == OperationResultType &&
                (method.Name == "Success" || method.Name == "Failure")) return true;
            if (method.ContainingType is INamedTypeSymbol result &&
                result.ConstructedFrom.ToDisplayString() == OperationResultType &&
                method.Name == "TryGetValue" && IsRootedInLocal(invocation.Instance)) return true;
            return false;
        }

        private static bool IsAllowedPredictedObjectCreation(IObjectCreationOperation creation) =>
            creation.Constructor?.IsImplicitlyDeclared == true &&
            creation.Type?.Locations.Any(location => location.IsInSource) == true;

        private static bool TryFindUnsafeTypeInitialization(
            Compilation compilation,
            INamedTypeSymbol type,
            ISet<ITypeSymbol> inspectingTypes,
            out InitializerSafetyIssue? issue)
        {
            issue = null;
            if (!type.Locations.Any(location => location.IsInSource))
            {
                issue = new InitializerSafetyIssue(
                    type.Locations.FirstOrDefault() ?? Location.None,
                    type.ToDisplayString() + " (initializer source is unavailable)",
                    false);
                return true;
            }

            if (type.BaseType?.SpecialType != SpecialType.System_Object)
            {
                issue = new InitializerSafetyIssue(
                    type.Locations.FirstOrDefault() ?? Location.None,
                    type.ToDisplayString() + " (base-constructor execution)",
                    false);
                return true;
            }

            if (!inspectingTypes.Add(type))
            {
                issue = new InitializerSafetyIssue(
                    type.Locations.FirstOrDefault() ?? Location.None,
                    type.ToDisplayString() + " (recursive initializer construction)",
                    false);
                return true;
            }

            try
            {
                foreach (var initializer in GetTypeInitializerOperations(compilation, type))
                {
                    if (TryFindUnsafeInitializerOperation(
                            compilation,
                            initializer,
                            inspectingTypes,
                            out issue)) return true;
                }

                var staticConstructor = type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(method =>
                    method.MethodKind == MethodKind.StaticConstructor && !method.IsImplicitlyDeclared);
                if (staticConstructor != null)
                {
                    issue = new InitializerSafetyIssue(
                        staticConstructor.Locations.FirstOrDefault() ?? type.Locations.FirstOrDefault() ?? Location.None,
                        staticConstructor.ToDisplayString(),
                        false);
                    return true;
                }

                var finalizer = type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(method =>
                    method.MethodKind == MethodKind.Destructor);
                if (finalizer != null)
                {
                    issue = new InitializerSafetyIssue(
                        finalizer.Locations.FirstOrDefault() ?? type.Locations.FirstOrDefault() ?? Location.None,
                        finalizer.ToDisplayString(),
                        false);
                    return true;
                }

                return false;
            }
            finally
            {
                inspectingTypes.Remove(type);
            }
        }

        private static IEnumerable<IOperation> GetTypeInitializerOperations(
            Compilation compilation,
            INamedTypeSymbol type)
        {
            var operations = new List<IOperation>();
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member is IFieldSymbol { IsConst: true }) continue;
                foreach (var syntaxReference in member.DeclaringSyntaxReferences)
                {
                    var syntax = syntaxReference.GetSyntax();
                    ExpressionSyntax? initializer = syntax switch
                    {
                        VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                        PropertyDeclarationSyntax property => property.Initializer?.Value,
                        _ => null
                    };
                    if (initializer == null) continue;
                    var operation = compilation.GetSemanticModel(initializer.SyntaxTree).GetOperation(initializer);
                    if (operation != null) operations.Add(operation);
                }
            }

            return operations
                .OrderBy(operation => operation.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(operation => operation.Syntax.SpanStart);
        }

        private static bool TryFindUnsafeInitializerOperation(
            Compilation compilation,
            IOperation root,
            ISet<ITypeSymbol> inspectingTypes,
            out InitializerSafetyIssue? issue)
        {
            issue = null;
            foreach (var operation in DescendantsAndSelf(root))
            {
                var symbol = ReferencedSymbol(operation);
                var display = symbol?.ToDisplayString() ?? operation.Syntax.ToString();
                if (IsNondeterministic(display))
                {
                    issue = new InitializerSafetyIssue(operation.Syntax.GetLocation(), display, true);
                    return true;
                }

                if (IsProcessLocalSideEffect(display))
                {
                    issue = new InitializerSafetyIssue(operation.Syntax.GetLocation(), display, false);
                    return true;
                }

                if (operation is IInvocationOperation invocation && !IsAllowedPureInitializerInvocation(invocation) ||
                    operation is IPropertyReferenceOperation property && !IsAllowedInitializerProperty(property) ||
                    operation is IFieldReferenceOperation field && !IsAllowedInitializerField(field) ||
                    operation is IMethodReferenceOperation ||
                    IsUserDefinedOperation(operation, out _))
                {
                    issue = new InitializerSafetyIssue(operation.Syntax.GetLocation(), display, false);
                    return true;
                }

                if (operation is not IObjectCreationOperation creation) continue;
                if (!IsAllowedPredictedObjectCreation(creation) || creation.Type is not INamedTypeSymbol createdType)
                {
                    issue = new InitializerSafetyIssue(operation.Syntax.GetLocation(), display, false);
                    return true;
                }

                if (TryFindUnsafeTypeInitialization(compilation, createdType, inspectingTypes, out issue)) return true;
            }

            return false;
        }

        private static bool IsAllowedPureInitializerInvocation(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            return method.IsStatic && method.Name == "Empty" && method.TypeArguments.Length == 1 &&
                   method.Parameters.Length == 0 && method.ContainingType?.ToDisplayString() == "System.Array";
        }

        private static bool IsAllowedInitializerProperty(IPropertyReferenceOperation reference) =>
            !reference.Property.IsStatic &&
            reference.Property.Locations.Any(location => location.IsInSource) &&
            IsAutoProperty(reference.Property) &&
            IsObjectInitializerAssignment(reference.Syntax);

        private static bool IsAllowedInitializerField(IFieldReferenceOperation reference)
        {
            var field = reference.Field;
            if (field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum) return true;
            if (field.ContainingType?.SpecialType == SpecialType.System_String && field.Name == "Empty") return true;
            return !field.IsStatic && field.Locations.Any(location => location.IsInSource) &&
                   IsObjectInitializerAssignment(reference.Syntax);
        }

        private static bool IsAllowedPredictedProperty(IPropertyReferenceOperation reference)
        {
            var property = reference.Property;
            if (IsObjectInitializerAssignment(reference.Syntax)) return IsAutoProperty(property);
            if (property.ContainingType is INamedTypeSymbol result &&
                result.ConstructedFrom.ToDisplayString() == OperationResultType &&
                (property.Name == "Succeeded" || property.Name == "Value" || property.Name == "ErrorCode" ||
                 property.Name == "ErrorMessage") && IsRootedInLocal(reference.Instance)) return true;
            var containingType = property.ContainingType?.ToDisplayString();
            if ((containingType == "TopiaForge.Mods.MultiplayerCommandContext" ||
                 containingType == "TopiaForge.Mods.ReplicatedObjectCommandContext") &&
                IsRootedInParameter(reference.Instance)) return true;
            if (IsDeterministicMultiplayerValueType(containingType) &&
                IsRootedInPredictedData(reference.Instance)) return true;
            if (property.ContainingType?.SpecialType == SpecialType.System_String && property.Name == "Length" &&
                IsRootedInPredictedData(reference.Instance)) return true;
            return property.Locations.Any(location => location.IsInSource) && IsAutoProperty(property) &&
                   IsRootedInPredictedData(reference.Instance);
        }

        private static bool IsAllowedPredictedField(IFieldReferenceOperation reference)
        {
            var field = reference.Field;
            if (field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum) return true;
            if (field.ContainingType?.SpecialType == SpecialType.System_String && field.Name == "Empty") return true;
            if (field.Type is INamedTypeSymbol state &&
                state.ConstructedFrom.ToDisplayString() == "TopiaForge.Mods.ReplicatedState<T>" &&
                field.Locations.Any(location => location.IsInSource)) return true;
            return field.Locations.Any(location => location.IsInSource) &&
                   IsRootedInPredictedData(reference.Instance);
        }

        private static bool IsUserDefinedOperation(IOperation operation, out IMethodSymbol? method)
        {
            method = operation switch
            {
                IBinaryOperation binary => binary.OperatorMethod,
                IUnaryOperation unary => unary.OperatorMethod,
                IConversionOperation conversion => conversion.OperatorMethod,
                ICompoundAssignmentOperation compound => compound.OperatorMethod,
                IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                _ => null
            };
            return method != null;
        }

        private static bool IsRootedInLocal(IOperation? operation) =>
            Unwrap(operation) is ILocalReferenceOperation;

        private static bool IsRootedInParameter(IOperation? operation) =>
            Unwrap(operation) is IParameterReferenceOperation;

        private static bool IsRootedInPredictedData(IOperation? operation)
        {
            operation = Unwrap(operation);
            return operation is IParameterReferenceOperation || operation is ILocalReferenceOperation ||
                   operation is IPropertyReferenceOperation property && IsRootedInPredictedData(property.Instance) ||
                   operation is IFieldReferenceOperation field && IsRootedInPredictedData(field.Instance);
        }

        private static IOperation? Unwrap(IOperation? operation)
        {
            while (operation is IConversionOperation conversion) operation = conversion.Operand;
            return operation;
        }

        private static bool IsDeterministicMultiplayerValueType(string? type) =>
            type == "TopiaForge.Mods.MultiplayerSessionId" ||
            type == "TopiaForge.Mods.ParticipantId" ||
            type == "TopiaForge.Mods.NetworkObjectId" ||
            type == "TopiaForge.Mods.NetworkTick" ||
            type == "TopiaForge.Mods.SessionSeed";

        private static bool IsGeneratedPresentationEmitter(
            InvocationExpressionSyntax invocation,
            ISet<string> generatedPresentationEmitters)
        {
            string? name = null;
            if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                name = identifier.Identifier.ValueText;
            }
            else if (invocation.Expression is MemberAccessExpressionSyntax member &&
                     member.Expression is ThisExpressionSyntax)
            {
                name = member.Name.Identifier.ValueText;
            }

            return name != null && generatedPresentationEmitters.Contains(name);
        }

        private static bool IsAutoProperty(IPropertySymbol property)
        {
            foreach (var reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not PropertyDeclarationSyntax declaration ||
                    declaration.ExpressionBody != null || declaration.AccessorList == null ||
                    declaration.AccessorList.Accessors.Any(accessor =>
                        accessor.Body != null || accessor.ExpressionBody != null)) return false;
            }

            return true;
        }

        private static bool IsObjectInitializerAssignment(SyntaxNode node) =>
            node.Ancestors().OfType<InitializerExpressionSyntax>().Any(initializer =>
                initializer.IsKind(SyntaxKind.ObjectInitializerExpression));

        private static bool IsNondeterministic(string symbol) =>
            symbol.IndexOf("System.Random", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.DateTime.Now", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.DateTime.UtcNow", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.DateTimeOffset.Now", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.DateTimeOffset.UtcNow", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.Guid.NewGuid", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.Security.Cryptography.RandomNumberGenerator", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("UnityEngine.Random", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.Environment.TickCount", StringComparison.Ordinal) >= 0 ||
            symbol.IndexOf("System.Diagnostics.Stopwatch", StringComparison.Ordinal) >= 0;

        private static bool IsProcessLocalSideEffect(string symbol)
        {
            var banned = new[]
            {
                "System.IO.", "UnityEngine.", "TopiaForge.Mods.IModContext",
                "TopiaForge.Mods.IModLogger", "TopiaForge.Mods.ILocalPlayerService",
                "TopiaForge.Mods.IInputService", "TopiaForge.Mods.IAudioService",
                "TopiaForge.Mods.IUiService", "TopiaForge.Mods.ILocalModStorageService",
                "TopiaForge.Mods.IModConfigService", "TopiaForge.Mods.IModFiles",
                "TopiaForge.Mods.IModScheduler", "TopiaForge.Mods.IGameTime",
                "TopiaForge.Mods.IPhysicsService", "TopiaForge.Mods.IEntityService",
                "TopiaForge.Mods.ISceneService", "TopiaForge.Mods.IInteractionService",
                "TopiaForge.Mods.IItemService", "TopiaForge.Mods.IModEvents",
                "TopiaForge.Mods.ILocalizationService", "TopiaForge.Mods.IAssetService",
                "TopiaForge.Mods.ICommandService", "TopiaForge.Mods.IDiagnosticsService",
                "TopiaForge.Mods.IExtensionService", "TopiaForge.Mods.IMultiplayerSession",
                "TopiaForge.Mods.IReplicatedObject", "TopiaForge.Mods.IReplicatedObjectTypeRegistration",
                "TopiaForge.Mods.IPresentationEventRegistration", "TopiaForge.Mods.IWorldGamemodeService",
                "TopiaForge.Mods.ITimeControlService"
            };
            return banned.Any(value => symbol.IndexOf(value, StringComparison.Ordinal) >= 0);
        }

        private static bool TryBuildCodec(
            SourceProductionContext context,
            Compilation compilation,
            ITypeSymbol type,
            ISet<ITypeSymbol> visiting,
            out CodecModel? codec)
        {
            codec = null;
            if (type.IsValueType || type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Array)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(),
                    "root state, request, response, and event payloads must be concrete reference-type DTOs"));
                return false;
            }

            if (type is not INamedTypeSymbol named || named.IsAbstract || !named.IsSealed ||
                named.TypeKind != TypeKind.Class || named.IsGenericType ||
                named.BaseType?.SpecialType != SpecialType.System_Object ||
                IsNativeOrFrameworkObject(named) || !named.Constructors.Any(constructor =>
                    constructor.Parameters.Length == 0 && constructor.IsImplicitlyDeclared &&
                    constructor.DeclaredAccessibility != Accessibility.Private))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(),
                    "payload DTOs must be sealed, non-generic, non-native classes that directly inherit object and use their implicit parameterless constructor"));
                return false;
            }

            if (TryFindUnsafeTypeInitialization(
                    compilation,
                    named,
                    new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                    out var initializerIssue))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    initializerIssue!.Location,
                    type.ToDisplayString(),
                    "payload construction must not execute static constructors, finalizers, or initializer code that is not compile-time deterministic; unsafe initializer '" +
                    initializerIssue.Display + "'"));
                return false;
            }

            if (!visiting.Add(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(),
                    "recursive payload graphs are not supported"));
                return false;
            }

            var members = new List<CodecMember>();
            var success = true;
            foreach (var member in named.GetMembers().OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                ITypeSymbol? memberType = null;
                if (member is IPropertySymbol property && !property.IsStatic && property.GetMethod != null &&
                    property.SetMethod != null && property.GetMethod.DeclaredAccessibility == Accessibility.Public &&
                    property.SetMethod.DeclaredAccessibility == Accessibility.Public && !property.SetMethod.IsInitOnly &&
                    !property.IsRequired)
                {
                    if (!IsAutoProperty(property))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedType,
                            member.Locations.FirstOrDefault(),
                            member.ToDisplayString(),
                            "payload properties must be auto-properties so serialization and prediction cannot invoke hidden code"));
                        success = false;
                        continue;
                    }
                    memberType = property.Type;
                }
                else if (member is IFieldSymbol field && !field.IsStatic && !field.IsReadOnly &&
                         field.DeclaredAccessibility == Accessibility.Public && !field.IsRequired)
                {
                    memberType = field.Type;
                }

                if (memberType == null)
                {
                    if (IsUnsupportedPublicDataMember(member))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedType,
                            member.Locations.FirstOrDefault(),
                            member.ToDisplayString(),
                            "public payload fields and properties must be mutable, non-required, and have public get/set accessors"));
                        success = false;
                    }

                    continue;
                }
                var boundAttribute = FindAttribute(member, BoundAttribute);
                var bound = boundAttribute == null ? (int?)null : GetConstructorInt(boundAttribute);
                if (bound.HasValue && (bound.Value < 1 || bound.Value > 65536))
                {
                    context.ReportDiagnostic(Diagnostic.Create(MissingBound, member.Locations.FirstOrDefault(), member.Name));
                    success = false;
                    continue;
                }
                if (RequiresBound(memberType) && bound == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(MissingBound, member.Locations.FirstOrDefault(), member.Name));
                    success = false;
                    continue;
                }

                if (!TryGetMaximumBytes(
                        context,
                        compilation,
                        memberType,
                        bound,
                        visiting,
                        out var maximumBytes,
                        out var reason))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedType,
                        member.Locations.FirstOrDefault(),
                        memberType.ToDisplayString(),
                        reason));
                    success = false;
                    continue;
                }

                members.Add(new CodecMember(member.Name, memberType, bound, maximumBytes));
            }

            visiting.Remove(type);
            if (!success) return false;
            codec = new CodecModel(type, members, members.Sum(member => member.MaximumBytes));
            return true;
        }

        private static bool IsNativeOrFrameworkObject(INamedTypeSymbol type)
        {
            var namespaceName = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return namespaceName == "UnityEngine" || namespaceName.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                   namespaceName == "System.Reflection" || namespaceName.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                   type.SpecialType == SpecialType.System_Object ||
                   type.ToDisplayString() == "System.Type" ||
                   type.ToDisplayString() == "System.IntPtr" ||
                   type.ToDisplayString() == "System.UIntPtr" ||
                   type.TypeKind == TypeKind.Delegate;
        }

        private static bool IsUnsupportedPublicDataMember(ISymbol member)
        {
            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) return false;
            return member is IFieldSymbol || member is IPropertySymbol property && !property.IsIndexer;
        }

        private static bool TryGetMaximumBytes(
            SourceProductionContext context,
            Compilation compilation,
            ITypeSymbol type,
            int? bound,
            ISet<ITypeSymbol> visiting,
            out int maximumBytes,
            out string reason)
        {
            reason = string.Empty;
            maximumBytes = type.SpecialType switch
            {
                SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
                SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char => 2,
                SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
                SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
                SpecialType.System_String when bound.HasValue => checked(4 + (bound.Value * 4)),
                _ => 0
            };
            if (maximumBytes != 0) return true;
            if (type.TypeKind == TypeKind.Enum)
            {
                return TryGetMaximumBytes(
                    context,
                    compilation,
                    ((INamedTypeSymbol)type).EnumUnderlyingType!,
                    null,
                    visiting,
                    out maximumBytes,
                    out reason);
            }

            if (type is IArrayTypeSymbol array && array.Rank == 1 && bound.HasValue)
            {
                if (!TryGetMaximumBytes(
                        context,
                        compilation,
                        array.ElementType,
                        null,
                        visiting,
                        out var elementBytes,
                        out reason)) return false;
                maximumBytes = checked(4 + (bound.Value * elementBytes));
                return true;
            }

            if (type.IsReferenceType && TryBuildCodec(context, compilation, type, visiting, out var nested))
            {
                maximumBytes = checked(1 + nested!.MaximumBytes);
                return true;
            }

            reason = "only deterministic primitives, enums, bounded strings/arrays, and supported DTOs are allowed";
            return false;
        }

        private static bool RequiresBound(ITypeSymbol type) =>
            type.SpecialType == SpecialType.System_String || type is IArrayTypeSymbol;

        private static string Render(
            INamedTypeSymbol contract,
            string contractId,
            IReadOnlyList<StateModel> states,
            IReadOnlyList<CommandModel> commands,
            IReadOnlyList<ObjectModel> objects,
            IReadOnlyList<EventModel> events,
            IReadOnlyList<CodecModel> codecs)
        {
            var schema = BuildSchema(WireFormatRevision, contractId, states, commands, objects, events, codecs);
            var hash = Sha256(schema);
            var builder = new StringBuilder();
            builder.Append("// TopiaForge.MultiplayerContractLock:v2:")
                .AppendLine(BuildContractLockMarker(
                    WireFormatRevision,
                    contractId,
                    hash,
                    states,
                    commands,
                    objects,
                    events));
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            if (!contract.ContainingNamespace.IsGlobalNamespace)
            {
                builder.Append("namespace ").Append(contract.ContainingNamespace.ToDisplayString()).AppendLine();
                builder.AppendLine("{");
            }

            builder.Append("partial class ").Append(contract.Name).AppendLine(" : global::TopiaForge.Mods.IGeneratedMultiplayerContract");
            builder.AppendLine("{");
            builder.AppendLine("    private global::TopiaForge.Mods.IMultiplayerSession? __topiaforgeMultiplayerSession;");
            builder.AppendLine("    private global::System.IDisposable? __topiaforgeMultiplayerBinding;");
            builder.AppendLine();
            builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            builder.AppendLine("    public global::TopiaForge.Mods.MultiplayerContractDescriptor MultiplayerContractDescriptor { get; } =");
            builder.Append("        new global::TopiaForge.Mods.MultiplayerContractDescriptor(").Append(Literal(contractId)).Append(", ")
                .Append(WireFormatRevision.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(Literal(hash)).AppendLine(",");
            builder.Append("            new string[] { ").Append(string.Join(", ", states.OrderBy(state => state.Id, StringComparer.Ordinal).Select(state => Literal(Namespaced(contractId, state.Id))))).AppendLine(" },");
            builder.Append("            new string[] { ").Append(string.Join(", ", commands.OrderBy(command => command.Id, StringComparer.Ordinal).Select(command => Literal(Namespaced(contractId, command.Id))))).AppendLine(" },");
            builder.Append("            new string[] { ").Append(string.Join(", ", objects.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => Literal(Namespaced(contractId, item.Id))))).AppendLine(" },");
            builder.Append("            new string[] { ").Append(string.Join(", ", events.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => Literal(Namespaced(contractId, item.Id))))).AppendLine(" });");
            builder.AppendLine();

            foreach (var command in commands)
            {
                var name = PublicName(command.Id);
                builder.Append("    private static readonly global::TopiaForge.Mods.MultiplayerCommandType<")
                    .Append(TypeName(command.RequestType)).Append(", ").Append(TypeName(command.ResponseType))
                    .Append("> __topiaforgeCommandType_").Append(name)
                    .Append(" = new global::TopiaForge.Mods.MultiplayerCommandType<")
                    .Append(TypeName(command.RequestType)).Append(", ").Append(TypeName(command.ResponseType))
                    .Append(">(").Append(Literal(Namespaced(contractId, command.Id))).AppendLine(");");
                builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                builder.Append("    public global::TopiaForge.Mods.MultiplayerCommandType<")
                    .Append(TypeName(command.RequestType)).Append(", ").Append(TypeName(command.ResponseType))
                    .Append("> ").Append(name).Append("CommandType => __topiaforgeCommandType_").Append(name).AppendLine(";");
                builder.AppendLine();
            }

            foreach (var item in objects)
            {
                var name = PublicName(item.Id);
                builder.Append("    private static readonly global::TopiaForge.Mods.ReplicatedObjectType<")
                    .Append(TypeName(item.StateType)).Append(", ").Append(TypeName(item.InputType)).Append("> __topiaforgeObjectType_")
                    .Append(name).Append(" = new global::TopiaForge.Mods.ReplicatedObjectType<")
                    .Append(TypeName(item.StateType)).Append(", ").Append(TypeName(item.InputType)).Append(">(")
                    .Append(Literal(Namespaced(contractId, item.Id))).AppendLine(");");
                builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                builder.Append("    public global::TopiaForge.Mods.ReplicatedObjectType<")
                    .Append(TypeName(item.StateType)).Append(", ").Append(TypeName(item.InputType)).Append("> ")
                    .Append(name).Append("ObjectType => __topiaforgeObjectType_").Append(name).AppendLine(";");
                builder.AppendLine();
            }

            foreach (var item in events)
            {
                var name = PublicName(item.Id);
                builder.Append("    private static readonly global::TopiaForge.Mods.PresentationEventType<")
                    .Append(TypeName(item.PayloadType)).Append("> __topiaforgeEventType_").Append(name)
                    .Append(" = new global::TopiaForge.Mods.PresentationEventType<").Append(TypeName(item.PayloadType))
                    .Append(">(").Append(Literal(Namespaced(contractId, item.Id))).Append(", ")
                    .Append(CodecName(item.PayloadType)).AppendLine(".Instance);");
                builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                builder.Append("    public global::TopiaForge.Mods.PresentationEventType<")
                    .Append(TypeName(item.PayloadType)).Append("> ").Append(name)
                    .Append("EventType => __topiaforgeEventType_").Append(name).AppendLine(";");
                builder.AppendLine();
            }

            builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            builder.AppendLine("    public global::TopiaForge.Mods.IMultiplayerCodec<T> GetCodec<T>() where T : class");
            builder.AppendLine("    {");
            foreach (var codec in codecs.OrderBy(codec => TypeName(codec.Type), StringComparer.Ordinal))
            {
                builder.Append("        if (typeof(T) == typeof(").Append(TypeName(codec.Type)).Append(")) return (global::TopiaForge.Mods.IMultiplayerCodec<T>)(object)")
                    .Append(CodecName(codec.Type)).AppendLine(".Instance;");
            }

            builder.AppendLine("        throw new global::System.InvalidOperationException(\"No generated multiplayer codec exists for '\" + typeof(T).FullName + \"'. Add typeof(\" + typeof(T).Name + \") to [MultiplayerContract] only when the DTO is used through a manual low-level API.\");");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public global::TopiaForge.Mods.OperationResult<global::System.IDisposable> BindMultiplayer(global::TopiaForge.Mods.IMultiplayerSession session)");
            builder.AppendLine("    {");
            builder.AppendLine("        if (session == null) throw new global::System.ArgumentNullException(nameof(session));");
            builder.AppendLine("        if (__topiaforgeMultiplayerBinding != null)");
            builder.AppendLine("            return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Failure(global::TopiaForge.Mods.ModErrorCode.Conflict, \"The generated multiplayer contract is already bound.\");");
            builder.AppendLine("        var binding = new __TopiaForgeMultiplayerBinding(() =>");
            builder.AppendLine("        {");
            builder.AppendLine("            __topiaforgeMultiplayerSession = null;");
            builder.AppendLine("            __topiaforgeMultiplayerBinding = null;");
            builder.AppendLine("        });");
            builder.AppendLine("        try");
            builder.AppendLine("        {");
            foreach (var state in states)
            {
                var codecName = CodecName(state.ValueType);
                var resultName = "state_" + Sanitize(state.Field.Name);
                builder.Append("            var ").Append(resultName).Append(" = this.").Append(state.Field.Name)
                    .Append(".Bind(session, ").Append(Literal(Namespaced(contractId, state.Id))).Append(", ")
                    .Append(codecName).AppendLine(".Instance);");
                builder.Append("            if (!").Append(resultName).AppendLine(".TryGetValue(out var connection_" + Sanitize(state.Field.Name) + ")) { binding.Dispose(); return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Failure(" + resultName + ".ErrorCode, " + resultName + ".ErrorMessage); }");
                builder.Append("            binding.Add(connection_").Append(Sanitize(state.Field.Name)).AppendLine(");");
            }

            for (var commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                var command = commands[commandIndex];
                var uniqueName = Sanitize(command.Method.Name) + "_" + commandIndex.ToString(CultureInfo.InvariantCulture);
                var resultName = "command_" + uniqueName;
                builder.Append("            var ").Append(resultName).Append(" = session.RegisterCommand(new global::TopiaForge.Mods.MultiplayerCommandDefinition<")
                    .Append(TypeName(command.RequestType)).Append(", ").Append(TypeName(command.ResponseType)).Append(">(")
                    .Append("__topiaforgeCommandType_").Append(PublicName(command.Id)).Append(", ").Append(CodecName(command.RequestType)).Append(".Instance, ")
                    .Append(CodecName(command.ResponseType)).Append(".Instance, this.").Append(command.Method.Name)
                    .Append(", (global::TopiaForge.Mods.PredictionMode)").Append(command.Prediction.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(command.MaximumPerSecond.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(command.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture)).AppendLine("));");
                builder.Append("            if (!").Append(resultName).AppendLine(".TryGetValue(out var registration_" + uniqueName + ")) { binding.Dispose(); return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Failure(" + resultName + ".ErrorCode, " + resultName + ".ErrorMessage); }");
                builder.Append("            binding.Add(registration_").Append(uniqueName).AppendLine(");");
            }

            for (var objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                var item = objects[objectIndex];
                var uniqueName = PublicName(item.Id) + "_" + objectIndex.ToString(CultureInfo.InvariantCulture);
                var resultName = "objectType_" + uniqueName;
                builder.Append("            var ").Append(resultName).Append(" = session.RegisterObjectType(new global::TopiaForge.Mods.ReplicatedObjectTypeDefinition<")
                    .Append(TypeName(item.StateType)).Append(", ").Append(TypeName(item.InputType)).Append(">(__topiaforgeObjectType_")
                    .Append(PublicName(item.Id)).Append(", ").Append(CodecName(item.StateType)).Append(".Instance, ")
                    .Append(CodecName(item.InputType)).Append(".Instance, ").Append(item.Method.Name)
                    .Append(", (global::TopiaForge.Mods.PredictionMode)").Append(item.Prediction.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(item.MaximumPerSecond.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(item.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture)).AppendLine("));");
                builder.Append("            if (!").Append(resultName).AppendLine(".TryGetValue(out var registration_" + uniqueName + ")) { binding.Dispose(); return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Failure(" + resultName + ".ErrorCode, " + resultName + ".ErrorMessage); }");
                builder.Append("            binding.Add(registration_").Append(uniqueName).AppendLine(");");
            }

            for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
            {
                var item = events[eventIndex];
                var uniqueName = PublicName(item.Id) + "_" + eventIndex.ToString(CultureInfo.InvariantCulture);
                var resultName = "presentation_" + uniqueName;
                builder.Append("            var ").Append(resultName).Append(" = session.RegisterPresentation(new global::TopiaForge.Mods.PresentationEventDefinition<")
                    .Append(TypeName(item.PayloadType)).Append(">(__topiaforgeEventType_").Append(PublicName(item.Id))
                    .Append(", session.Snapshot.HasPresentation ? (global::System.Action<").Append(TypeName(item.PayloadType)).Append(">)this.")
                    .Append(item.Method.Name).AppendLine(" : null));");
                builder.Append("            if (!").Append(resultName).AppendLine(".TryGetValue(out var registration_" + uniqueName + ")) { binding.Dispose(); return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Failure(" + resultName + ".ErrorCode, " + resultName + ".ErrorMessage); }");
                builder.Append("            binding.Add(registration_").Append(uniqueName).AppendLine(");");
            }

            builder.AppendLine("            __topiaforgeMultiplayerSession = session;");
            builder.AppendLine("            __topiaforgeMultiplayerBinding = binding;");
            builder.AppendLine("            return global::TopiaForge.Mods.OperationResult<global::System.IDisposable>.Success(binding);");
            builder.AppendLine("        }");
            builder.AppendLine("        catch");
            builder.AppendLine("        {");
            builder.AppendLine("            binding.Dispose();");
            builder.AppendLine("            throw;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            foreach (var command in commands)
            {
                builder.Append("    public global::System.Threading.Tasks.Task<global::TopiaForge.Mods.MultiplayerCommandConfirmation<")
                    .Append(TypeName(command.ResponseType)).Append(">> Submit").Append(command.Method.Name).Append("Async(")
                    .Append(TypeName(command.RequestType)).Append(" request, global::System.Threading.CancellationToken cancellationToken = default)")
                    .AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated command proxy is used.\");");
                builder.Append("        return __topiaforgeMultiplayerSession.SubmitAsync<").Append(TypeName(command.RequestType)).Append(", ")
                    .Append(TypeName(command.ResponseType)).Append(">(__topiaforgeCommandType_").Append(PublicName(command.Id))
                    .AppendLine(", request, cancellationToken);");
                builder.AppendLine("    }");
                builder.AppendLine();
            }

            foreach (var item in objects)
            {
                var name = PublicName(item.Id);
                var stateType = TypeName(item.StateType);
                var inputType = TypeName(item.InputType);
                builder.Append("    public global::TopiaForge.Mods.OperationResult<global::TopiaForge.Mods.IReplicatedObject<")
                    .Append(stateType).Append(", ").Append(inputType).Append(">> Spawn").Append(name)
                    .Append("Object(").Append(stateType)
                    .Append(" initialState, global::TopiaForge.Mods.ParticipantId? ownerId = null)").AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated replicated-object proxy is used.\");");
                builder.Append("        return __topiaforgeMultiplayerSession.SpawnObject(__topiaforgeObjectType_").Append(name)
                    .AppendLine(", initialState, ownerId);");
                builder.AppendLine("    }");
                builder.AppendLine();
                builder.Append("    public bool TryGet").Append(name)
                    .Append("Object(global::TopiaForge.Mods.NetworkObjectId id, out global::TopiaForge.Mods.IReplicatedObject<")
                    .Append(stateType).Append(", ").Append(inputType).Append(">? replicatedObject)").AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated replicated-object proxy is used.\");");
                builder.Append("        return __topiaforgeMultiplayerSession.TryGetObject(__topiaforgeObjectType_").Append(name)
                    .AppendLine(", id, out replicatedObject);");
                builder.AppendLine("    }");
                builder.AppendLine();
                builder.Append("    public global::System.Collections.Generic.IReadOnlyList<global::TopiaForge.Mods.IReplicatedObject<")
                    .Append(stateType).Append(", ").Append(inputType).Append(">> Get").Append(name)
                    .AppendLine("Objects()");
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated replicated-object proxy is used.\");");
                builder.Append("        return __topiaforgeMultiplayerSession.GetObjects(__topiaforgeObjectType_").Append(name).AppendLine(");");
                builder.AppendLine("    }");
                builder.AppendLine();
                builder.Append("    public global::System.IDisposable Subscribe").Append(name)
                    .Append("Objects(global::System.Action<global::TopiaForge.Mods.ReplicatedObjectChange<")
                    .Append(stateType).Append(", ").Append(inputType).Append(">> handler)").AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated replicated-object proxy is used.\");");
                builder.AppendLine("        if (handler == null) throw new global::System.ArgumentNullException(nameof(handler));");
                builder.Append("        return __topiaforgeMultiplayerSession.SubscribeObjects(__topiaforgeObjectType_").Append(name)
                    .AppendLine(", handler);");
                builder.AppendLine("    }");
                builder.AppendLine();
            }

            foreach (var item in events)
            {
                var eventName = PublicName(item.Id);
                builder.Append("    public global::TopiaForge.Mods.OperationResult<bool> Emit").Append(item.Method.Name)
                    .Append("(global::TopiaForge.Mods.MultiplayerCommandContext context, ").Append(TypeName(item.PayloadType))
                    .Append(" value, global::TopiaForge.Mods.MultiplayerAudience audience = global::TopiaForge.Mods.MultiplayerAudience.Everyone)")
                    .AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (context == null) throw new global::System.ArgumentNullException(nameof(context));");
                builder.Append("        return context.Emit(__topiaforgeEventType_").Append(eventName).AppendLine(", value, audience);");
                builder.AppendLine("    }");
                builder.AppendLine();
                builder.Append("    public global::TopiaForge.Mods.OperationResult<bool> Publish").Append(item.Method.Name)
                    .Append("(").Append(TypeName(item.PayloadType))
                    .Append(" value, global::TopiaForge.Mods.MultiplayerAudience audience = global::TopiaForge.Mods.MultiplayerAudience.Everyone)")
                    .AppendLine();
                builder.AppendLine("    {");
                builder.AppendLine("        if (__topiaforgeMultiplayerSession == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated presentation proxy is used.\");");
                builder.Append("        return __topiaforgeMultiplayerSession.PublishPresentation(__topiaforgeEventType_").Append(eventName).AppendLine(", value, audience);");
                builder.AppendLine("    }");
                builder.AppendLine();
            }

            RenderBinding(builder);
            foreach (var codec in codecs.OrderBy(codec => TypeName(codec.Type), StringComparer.Ordinal))
            {
                RenderCodec(builder, codec);
            }

            builder.AppendLine("}");
            if (!contract.ContainingNamespace.IsGlobalNamespace) builder.AppendLine("}");
            return builder.ToString();
        }

        private static string BuildContractLockMarker(
            int wireFormatRevision,
            string contractId,
            string schemaSha256,
            IEnumerable<StateModel> states,
            IEnumerable<CommandModel> commands,
            IEnumerable<ObjectModel> objects,
            IEnumerable<EventModel> events)
        {
            const string separator = "\u001f";
            var payload = string.Join("\n", new[]
            {
                contractId,
                wireFormatRevision.ToString(CultureInfo.InvariantCulture),
                schemaSha256,
                string.Join(separator, states.OrderBy(state => state.Id, StringComparer.Ordinal)
                    .Select(state => Namespaced(contractId, state.Id))),
                string.Join(separator, commands.OrderBy(command => command.Id, StringComparer.Ordinal)
                    .Select(command => Namespaced(contractId, command.Id))),
                string.Join(separator, objects.OrderBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => Namespaced(contractId, item.Id))),
                string.Join(separator, events.OrderBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => Namespaced(contractId, item.Id)))
            });
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

        private static void RenderBinding(StringBuilder builder)
        {
            builder.AppendLine("    private sealed class __TopiaForgeMultiplayerBinding : global::System.IDisposable");
            builder.AppendLine("    {");
            builder.AppendLine("        private readonly global::System.Collections.Generic.List<global::System.IDisposable> items = new global::System.Collections.Generic.List<global::System.IDisposable>();");
            builder.AppendLine("        private global::System.Action? onDispose;");
            builder.AppendLine("        private bool disposed;");
            builder.AppendLine("        internal __TopiaForgeMultiplayerBinding(global::System.Action onDispose) { this.onDispose = onDispose ?? throw new global::System.ArgumentNullException(nameof(onDispose)); }");
            builder.AppendLine("        public void Add(global::System.IDisposable item) { if (disposed) item.Dispose(); else items.Add(item); }");
            builder.AppendLine("        public void Dispose() { if (disposed) return; disposed = true; for (var index = items.Count - 1; index >= 0; index--) items[index].Dispose(); items.Clear(); var callback = onDispose; onDispose = null; callback?.Invoke(); }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private sealed class __TopiaForgeRegistrationGuard : global::System.IDisposable");
            builder.AppendLine("    {");
            builder.AppendLine("        private global::System.IDisposable? item;");
            builder.AppendLine("        internal __TopiaForgeRegistrationGuard(global::System.IDisposable item) { this.item = item ?? throw new global::System.ArgumentNullException(nameof(item)); }");
            builder.AppendLine("        internal void Replace(global::System.IDisposable replacement) { if (replacement == null) throw new global::System.ArgumentNullException(nameof(replacement)); item = replacement; }");
            builder.AppendLine("        public void Dispose() { var captured = item; item = null; captured?.Dispose(); }");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private static void RenderCodec(StringBuilder builder, CodecModel codec)
        {
            var codecName = CodecName(codec.Type);
            var typeName = TypeName(codec.Type);
            builder.Append("    private sealed class ").Append(codecName).Append(" : global::TopiaForge.Mods.IMultiplayerCodec<")
                .Append(typeName).AppendLine(">");
            builder.AppendLine("    {");
            builder.Append("        internal static readonly ").Append(codecName).Append(" Instance = new ").Append(codecName).AppendLine("();");
            builder.AppendLine("        private static readonly global::System.Text.Encoding StrictUtf8 = new global::System.Text.UTF8Encoding(false, true);");
            builder.Append("        public int MaximumEncodedBytes => ").Append(codec.MaximumBytes.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
            builder.Append("        public global::TopiaForge.Mods.OperationResult<byte[]> Encode(").Append(typeName).AppendLine(" value)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (value == null) throw new global::System.ArgumentNullException(nameof(value));");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            builder.AppendLine("                using var stream = new global::System.IO.MemoryStream();");
            builder.AppendLine("                using var writer = new global::System.IO.BinaryWriter(stream, StrictUtf8, true);");
            foreach (var member in codec.Members) RenderWrite(builder, "value." + member.Name, member.Type, member.Bound, "                ");
            builder.AppendLine("                writer.Flush();");
            builder.AppendLine("                if (stream.Length > MaximumEncodedBytes) return global::TopiaForge.Mods.OperationResult<byte[]>.Failure(global::TopiaForge.Mods.ModErrorCode.InvalidArgument, \"Encoded multiplayer payload exceeded its generated bound.\");");
            builder.AppendLine("                return global::TopiaForge.Mods.OperationResult<byte[]>.Success(stream.ToArray());");
            builder.AppendLine("            }");
            builder.AppendLine("            catch (global::System.Exception exception) when (exception is global::System.IO.IOException || exception is global::System.ArgumentException || exception is global::System.OverflowException)");
            builder.AppendLine("            { return global::TopiaForge.Mods.OperationResult<byte[]>.Failure(global::TopiaForge.Mods.ModErrorCode.InvalidArgument, exception.Message); }");
            builder.AppendLine("        }");
            builder.Append("        public global::TopiaForge.Mods.OperationResult<").Append(typeName).AppendLine("> Decode(byte[] bytes)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (bytes == null) throw new global::System.ArgumentNullException(nameof(bytes));");
            builder.AppendLine("            if (bytes.Length > MaximumEncodedBytes) return global::TopiaForge.Mods.OperationResult<" + typeName + ">.Failure(global::TopiaForge.Mods.ModErrorCode.InvalidArgument, \"Encoded multiplayer payload exceeded its generated bound.\");");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            builder.AppendLine("                using var stream = new global::System.IO.MemoryStream(bytes, false);");
            builder.AppendLine("                using var reader = new global::System.IO.BinaryReader(stream, StrictUtf8, true);");
            builder.Append("                var value = new ").Append(typeName).AppendLine("();");
            foreach (var member in codec.Members) RenderRead(builder, "value." + member.Name, member.Type, member.Bound, "                ");
            builder.AppendLine("                if (stream.Position != stream.Length) return global::TopiaForge.Mods.OperationResult<" + typeName + ">.Failure(global::TopiaForge.Mods.ModErrorCode.InvalidArgument, \"Encoded multiplayer payload contained trailing bytes.\");");
            builder.AppendLine("                return global::TopiaForge.Mods.OperationResult<" + typeName + ">.Success(value);");
            builder.AppendLine("            }");
            builder.AppendLine("            catch (global::System.Exception exception) when (exception is global::System.IO.IOException || exception is global::System.ArgumentException || exception is global::System.OverflowException)");
            builder.AppendLine("            { return global::TopiaForge.Mods.OperationResult<" + typeName + ">.Failure(global::TopiaForge.Mods.ModErrorCode.InvalidArgument, exception.Message); }");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private static void RenderWrite(StringBuilder builder, string expression, ITypeSymbol type, int? bound, string indent)
        {
            if (type.SpecialType == SpecialType.System_String)
            {
                var suffix = ValueSuffix(expression);
                builder.Append(indent).Append("if (").Append(expression).Append(" == null || ").Append(expression).Append(".Length > ").Append(bound!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.ArgumentException(\"A bounded string was null or exceeded its generated character limit.\");");
                builder.Append(indent).Append("var bytes_").Append(suffix).Append(" = StrictUtf8.GetBytes(").Append(expression).AppendLine(");");
                builder.Append(indent).Append("if (bytes_").Append(suffix).Append(".Length > ").Append((bound.Value * 4).ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.ArgumentException(\"A bounded string exceeded its generated UTF-8 limit.\");");
                builder.Append(indent).Append("writer.Write(bytes_").Append(suffix).AppendLine(".Length);");
                builder.Append(indent).Append("writer.Write(bytes_").Append(suffix).AppendLine(");");
                return;
            }

            if (type.SpecialType == SpecialType.System_Char)
            {
                builder.Append(indent).Append("writer.Write((global::System.UInt16)").Append(expression).AppendLine(");");
                return;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType!;
                if (underlying.SpecialType == SpecialType.System_SByte)
                {
                    builder.Append(indent).Append("writer.Write(unchecked((global::System.Byte)(global::System.SByte)").Append(expression).AppendLine("));");
                }
                else
                {
                    builder.Append(indent).Append("writer.Write((").Append(TypeName(underlying)).Append(")").Append(expression).AppendLine(");");
                }

                return;
            }

            if (type is IArrayTypeSymbol array)
            {
                var suffix = ValueSuffix(expression);
                builder.Append(indent).Append("if (").Append(expression).Append(" == null || ").Append(expression).Append(".Length > ")
                    .Append(bound!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.ArgumentException(\"A bounded array was null or exceeded its generated limit.\");");
                builder.Append(indent).Append("writer.Write(").Append(expression).AppendLine(".Length);");
                builder.Append(indent).Append("for (var index_").Append(suffix).Append(" = 0; index_").Append(suffix).Append(" < ").Append(expression).Append(".Length; index_").Append(suffix).AppendLine("++)");
                builder.Append(indent).AppendLine("{");
                RenderWrite(builder, expression + "[index_" + suffix + "]", array.ElementType, null, indent + "    ");
                builder.Append(indent).AppendLine("}");
                return;
            }

            if (type.IsReferenceType)
            {
                builder.Append(indent).Append("if (").Append(expression).AppendLine(" == null) { writer.Write(false); } else");
                builder.Append(indent).AppendLine("{");
                builder.Append(indent).AppendLine("    writer.Write(true);");
                var nested = GetSerializableMembers((INamedTypeSymbol)type);
                foreach (var member in nested)
                {
                    RenderWrite(builder, expression + "." + member.Name, member.Type, member.Bound, indent + "    ");
                }
                builder.Append(indent).AppendLine("}");
                return;
            }

            if (type.SpecialType == SpecialType.System_SByte)
            {
                builder.Append(indent).Append("writer.Write(unchecked((global::System.Byte)").Append(expression).AppendLine("));");
            }
            else
            {
                builder.Append(indent).Append("writer.Write(").Append(expression).AppendLine(");");
            }
        }

        private static void RenderRead(StringBuilder builder, string target, ITypeSymbol type, int? bound, string indent)
        {
            if (type.SpecialType == SpecialType.System_String)
            {
                var suffix = ValueSuffix(target);
                builder.Append(indent).Append("var length_").Append(suffix).AppendLine(" = reader.ReadInt32();");
                builder.Append(indent).Append("if (length_").Append(suffix).Append(" < 0 || length_").Append(suffix).Append(" > ").Append((bound!.Value * 4).ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.IO.InvalidDataException(\"A bounded string length was invalid.\");");
                builder.Append(indent).Append("var bytes_").Append(suffix).Append(" = reader.ReadBytes(length_").Append(suffix).AppendLine(");");
                builder.Append(indent).Append("if (bytes_").Append(suffix).Append(".Length != length_").Append(suffix).AppendLine(") throw new global::System.IO.EndOfStreamException();");
                builder.Append(indent).Append(target).Append(" = StrictUtf8.GetString(bytes_").Append(suffix).AppendLine(");");
                builder.Append(indent).Append("if (").Append(target).Append(".Length > ").Append(bound.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.IO.InvalidDataException(\"A bounded string exceeded its generated character limit.\");");
                return;
            }

            if (type.SpecialType == SpecialType.System_Char)
            {
                builder.Append(indent).Append(target).AppendLine(" = (global::System.Char)reader.ReadUInt16();");
                return;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType!;
                if (underlying.SpecialType == SpecialType.System_SByte)
                {
                    builder.Append(indent).Append(target).Append(" = (").Append(TypeName(type)).AppendLine(")unchecked((global::System.SByte)reader.ReadByte());");
                }
                else
                {
                    builder.Append(indent).Append(target).Append(" = (").Append(TypeName(type)).Append(")reader.").Append(ReadMethod(underlying)).AppendLine("();");
                }

                return;
            }

            if (type is IArrayTypeSymbol array)
            {
                var suffix = ValueSuffix(target);
                builder.Append(indent).Append("var length_").Append(suffix).AppendLine(" = reader.ReadInt32();");
                builder.Append(indent).Append("if (length_").Append(suffix).Append(" < 0 || length_").Append(suffix).Append(" > ").Append(bound!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(") throw new global::System.IO.InvalidDataException(\"A bounded array length was invalid.\");");
                builder.Append(indent).Append(target).Append(" = new ").Append(TypeName(array.ElementType)).Append("[length_").Append(suffix).AppendLine("]; ");
                builder.Append(indent).Append("for (var index_").Append(suffix).Append(" = 0; index_").Append(suffix).Append(" < length_").Append(suffix).Append("; index_").Append(suffix).AppendLine("++)");
                builder.Append(indent).AppendLine("{");
                RenderRead(builder, target + "[index_" + suffix + "]", array.ElementType, null, indent + "    ");
                builder.Append(indent).AppendLine("}");
                return;
            }

            if (type.IsReferenceType)
            {
                builder.Append(indent).AppendLine("if (!reader.ReadBoolean())");
                builder.Append(indent).Append("    ").Append(target).AppendLine(" = null!;");
                builder.Append(indent).AppendLine("else");
                builder.Append(indent).AppendLine("{");
                builder.Append(indent).Append("    ").Append(target).Append(" = new ").Append(NonNullableTypeName(type)).AppendLine("();");
                foreach (var member in GetSerializableMembers((INamedTypeSymbol)type))
                {
                    RenderRead(builder, target + "." + member.Name, member.Type, member.Bound, indent + "    ");
                }
                builder.Append(indent).AppendLine("}");
                return;
            }

            if (type.SpecialType == SpecialType.System_SByte)
            {
                builder.Append(indent).Append(target).AppendLine(" = unchecked((global::System.SByte)reader.ReadByte());");
            }
            else
            {
                builder.Append(indent).Append(target).Append(" = reader.").Append(ReadMethod(type)).AppendLine("();");
            }
        }

        private static IReadOnlyList<CodecMember> GetSerializableMembers(INamedTypeSymbol type)
        {
            var members = new List<CodecMember>();
            foreach (var member in type.GetMembers().OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                ITypeSymbol? memberType = null;
                if (member is IPropertySymbol property && !property.IsStatic && property.GetMethod != null && property.SetMethod != null &&
                    property.GetMethod.DeclaredAccessibility == Accessibility.Public && property.SetMethod.DeclaredAccessibility == Accessibility.Public &&
                    !property.SetMethod.IsInitOnly && !property.IsRequired)
                    memberType = property.Type;
                else if (member is IFieldSymbol field && !field.IsStatic && !field.IsReadOnly &&
                         field.DeclaredAccessibility == Accessibility.Public && !field.IsRequired)
                    memberType = field.Type;
                if (memberType == null) continue;
                var boundAttribute = FindAttribute(member, BoundAttribute);
                var bound = boundAttribute == null ? (int?)null : GetConstructorInt(boundAttribute);
                members.Add(new CodecMember(member.Name, memberType, bound, 0));
            }
            return members;
        }

        private static string ReadMethod(ITypeSymbol type) => type.SpecialType switch
        {
            SpecialType.System_Boolean => "ReadBoolean",
            SpecialType.System_Byte => "ReadByte",
            SpecialType.System_SByte => "ReadSByte",
            SpecialType.System_Int16 => "ReadInt16",
            SpecialType.System_UInt16 => "ReadUInt16",
            SpecialType.System_Char => "ReadChar",
            SpecialType.System_Int32 => "ReadInt32",
            SpecialType.System_UInt32 => "ReadUInt32",
            SpecialType.System_Int64 => "ReadInt64",
            SpecialType.System_UInt64 => "ReadUInt64",
            SpecialType.System_Single => "ReadSingle",
            SpecialType.System_Double => "ReadDouble",
            _ => throw new InvalidOperationException("Unsupported generated primitive " + type.ToDisplayString())
        };

        private static string BuildSchema(
            int wireFormatRevision,
            string contractId,
            IEnumerable<StateModel> states,
            IEnumerable<CommandModel> commands,
            IEnumerable<ObjectModel> objects,
            IEnumerable<EventModel> events,
            IEnumerable<CodecModel> codecs)
        {
            var lines = new List<string>
            {
                "wire-format-revision:" + wireFormatRevision.ToString(CultureInfo.InvariantCulture),
                "contract:" + contractId
            };
            lines.AddRange(states.OrderBy(state => state.Id, StringComparer.Ordinal).Select(state => "state:" + state.Id + ":" + TypeName(state.ValueType)));
            lines.AddRange(commands.OrderBy(command => command.Id, StringComparer.Ordinal).Select(command =>
                "command:" + command.Id + ":" + TypeName(command.RequestType) + ":" + TypeName(command.ResponseType) + ":" + command.Prediction + ":" + command.MaximumPerSecond + ":" + command.MaximumPayloadBytes));
            lines.AddRange(objects.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
                "object:" + item.Id + ":" + TypeName(item.StateType) + ":" + TypeName(item.InputType) + ":" + item.Prediction + ":" + item.MaximumPerSecond + ":" + item.MaximumPayloadBytes));
            lines.AddRange(events.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => "event:" + item.Id + ":" + TypeName(item.PayloadType)));
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var codec in codecs.OrderBy(codec => TypeName(codec.Type), StringComparer.Ordinal))
            {
                AppendSchemaType(lines, codec.Type, visited);
            }

            return string.Join("\n", lines);
        }

        private static void AppendSchemaType(ICollection<string> lines, ITypeSymbol type, ISet<ITypeSymbol> visited)
        {
            if (!visited.Add(type) || type is not INamedTypeSymbol named) return;
            var members = GetSerializableMembers(named);
            lines.Add("codec:" + TypeName(type) + ":" + string.Join(",", members.Select(member =>
                member.Name + "=" + TypeName(member.Type) + "[" + member.Bound + "]")));
            foreach (var member in members.OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                AppendNestedSchemaTypes(lines, member.Type, visited);
            }
        }

        private static void AppendNestedSchemaTypes(ICollection<string> lines, ITypeSymbol type, ISet<ITypeSymbol> visited)
        {
            if (type is IArrayTypeSymbol array)
            {
                AppendNestedSchemaTypes(lines, array.ElementType, visited);
                return;
            }

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                AppendSchemaEnum(lines, enumType, visited);
                return;
            }

            if (type.IsReferenceType && type.SpecialType != SpecialType.System_String)
            {
                AppendSchemaType(lines, type, visited);
            }
        }

        private static void AppendSchemaEnum(
            ICollection<string> lines,
            INamedTypeSymbol type,
            ISet<ITypeSymbol> visited)
        {
            if (!visited.Add(type)) return;
            var underlying = type.EnumUnderlyingType!;
            var members = type.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(member => member.HasConstantValue && member.Name != "value__")
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => member.Name + "=" + FormatEnumConstant(member.ConstantValue));
            lines.Add("enum:" + TypeName(type) + ":" + TypeName(underlying) + ":" + string.Join(",", members));
        }

        private static string FormatEnumConstant(object? value) => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

        private static string Sha256(string value)
        {
            using var algorithm = SHA256.Create();
            return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string EmptyContractSchemaDigestForTesting(int wireFormatRevision, string contractId) =>
            Sha256(BuildSchema(
                wireFormatRevision,
                contractId,
                Array.Empty<StateModel>(),
                Array.Empty<CommandModel>(),
                Array.Empty<ObjectModel>(),
                Array.Empty<EventModel>(),
                Array.Empty<CodecModel>()));

        internal static string EmptyContractLockMarkerForTesting(int wireFormatRevision, string contractId)
        {
            var schemaSha256 = EmptyContractSchemaDigestForTesting(wireFormatRevision, contractId);
            return BuildContractLockMarker(
                wireFormatRevision,
                contractId,
                schemaSha256,
                Array.Empty<StateModel>(),
                Array.Empty<CommandModel>(),
                Array.Empty<ObjectModel>(),
                Array.Empty<EventModel>());
        }

        private static AttributeData? FindAttribute(ISymbol symbol, string metadataName) =>
            symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

        private static string GetConstructorString(AttributeData attribute) =>
            attribute.ConstructorArguments.Length == 0 ? string.Empty : attribute.ConstructorArguments[0].Value as string ?? string.Empty;

        private static int GetConstructorInt(AttributeData attribute) =>
            attribute.ConstructorArguments.Length == 0 ? 0 : Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);

        private static int GetNamedInt(AttributeData attribute, string name, int fallback)
        {
            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == name && pair.Value.Value != null)
                    return Convert.ToInt32(pair.Value.Value, CultureInfo.InvariantCulture);
            return fallback;
        }

        private static IEnumerable<ITypeSymbol> GetAdditionalCodecTypes(AttributeData? attribute)
        {
            if (attribute == null || attribute.ConstructorArguments.Length == 0) yield break;
            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array) yield break;
            foreach (var value in argument.Values)
            {
                if (value.Value is ITypeSymbol type) yield return type;
            }
        }

        private static string TypeName(ITypeSymbol type) => type.ToDisplayString(FullyQualified);
        private static string NonNullableTypeName(ITypeSymbol type) =>
            type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(FullyQualified);
        private static string Namespaced(string contractId, string id) => contractId + "/" + id;
        private static string PublicName(string id)
        {
            var parts = id.Split(new[] { '.', '-', '_', '/', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var value = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
            if (value.Length == 0) value = "Contract";
            if (!char.IsLetter(value[0]) && value[0] != '_') value = "Contract" + value;
            return Sanitize(value);
        }
        private static string CodecName(ITypeSymbol type)
        {
            var display = type.ToDisplayString();
            return "__TopiaForgeCodec_" + Sanitize(display) + "_" + Sha256(display).Substring(0, 8);
        }

        private static string ValueSuffix(string value) => Sanitize(value) + "_" + Sha256(value).Substring(0, 8);
        private static string Sanitize(string value) => new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

        private sealed class StateModel
        {
            public StateModel(IFieldSymbol field, string id, ITypeSymbol valueType) { Field = field; Id = id; ValueType = valueType; }
            public IFieldSymbol Field { get; }
            public string Id { get; }
            public ITypeSymbol ValueType { get; }
        }

        private sealed class CommandModel
        {
            public CommandModel(IMethodSymbol method, string id, ITypeSymbol requestType, ITypeSymbol responseType, int prediction, int maximumPerSecond, int maximumPayloadBytes)
            { Method = method; Id = id; RequestType = requestType; ResponseType = responseType; Prediction = prediction; MaximumPerSecond = maximumPerSecond; MaximumPayloadBytes = maximumPayloadBytes; }
            public IMethodSymbol Method { get; }
            public string Id { get; }
            public ITypeSymbol RequestType { get; }
            public ITypeSymbol ResponseType { get; }
            public int Prediction { get; }
            public int MaximumPerSecond { get; }
            public int MaximumPayloadBytes { get; }
        }

        private sealed class ObjectModel
        {
            public ObjectModel(IMethodSymbol method, string id, ITypeSymbol stateType, ITypeSymbol inputType, int prediction, int maximumPerSecond, int maximumPayloadBytes)
            { Method = method; Id = id; StateType = stateType; InputType = inputType; Prediction = prediction; MaximumPerSecond = maximumPerSecond; MaximumPayloadBytes = maximumPayloadBytes; }
            public IMethodSymbol Method { get; }
            public string Id { get; }
            public ITypeSymbol StateType { get; }
            public ITypeSymbol InputType { get; }
            public int Prediction { get; }
            public int MaximumPerSecond { get; }
            public int MaximumPayloadBytes { get; }
        }

        private sealed class EventModel
        {
            public EventModel(IMethodSymbol method, string id, ITypeSymbol payloadType) { Method = method; Id = id; PayloadType = payloadType; }
            public IMethodSymbol Method { get; }
            public string Id { get; }
            public ITypeSymbol PayloadType { get; }
        }

        private sealed class CodecModel
        {
            public CodecModel(ITypeSymbol type, IReadOnlyList<CodecMember> members, int maximumBytes) { Type = type; Members = members; MaximumBytes = maximumBytes; }
            public ITypeSymbol Type { get; }
            public IReadOnlyList<CodecMember> Members { get; }
            public int MaximumBytes { get; }
        }

        private sealed class CodecMember
        {
            public CodecMember(string name, ITypeSymbol type, int? bound, int maximumBytes) { Name = name; Type = type; Bound = bound; MaximumBytes = maximumBytes; }
            public string Name { get; }
            public ITypeSymbol Type { get; }
            public int? Bound { get; }
            public int MaximumBytes { get; }
        }

        private sealed class InitializerSafetyIssue
        {
            public InitializerSafetyIssue(Location location, string display, bool isNondeterministic)
            {
                Location = location;
                Display = display;
                IsNondeterministic = isNondeterministic;
            }

            public Location Location { get; }
            public string Display { get; }
            public bool IsNondeterministic { get; }
        }
    }
}
