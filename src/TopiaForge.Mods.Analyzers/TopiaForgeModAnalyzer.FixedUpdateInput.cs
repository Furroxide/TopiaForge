using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor InputEdgeInFixedUpdate = new DiagnosticDescriptor(
            "TF1009",
            "Input edge read in FixedUpdate",
            "'{0}' reads an input edge in a FixedUpdate handler. FixedUpdate runs zero or more times a frame, before the frame's input is sampled, so it misses or repeats presses, and in multiplayer it never counts as the player's own action; read the edge in an Update or LateUpdate handler and set a field there that FixedUpdate reads.",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "WasPressed and WasReleased hold for the one frame the input service sampled them in, and only a mod's Update and LateUpdate carry the player's input as the player's own action.",
            helpLinkUri: DocsRoot + "TF1009",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>
        /// Registers TF1009 for one compilation that references the SDK's input and event contracts: every
        /// <c>WasPressed</c> or <c>WasReleased</c> read in a handler passed to <c>SubscribeFixedUpdate</c>, whether a lambda,
        /// a method of this compilation wherever it is declared, or either kept in a local first, and in the local functions
        /// such a handler runs. Code the handler only hands to the SDK to run later, an event subscription or a scheduled
        /// callback, runs on the frame's own clock and is not the handler's.
        /// </summary>
        private static void RegisterFixedUpdateInput(CompilationStartAnalysisContext context)
        {
            var events = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IModEvents");
            var input = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IInputAction");
            if (events == null || input == null)
            {
                return;
            }

            // Every member of these subscribes or schedules the delegate it is given; none runs it at once.
            var deferring = new[] { "TopiaForge.Mods.IModScheduler", "TopiaForge.Mods.ISceneLoadEventSource", "TopiaForge.Mods.ISceneLifecycleEventSource" }
                .Select(name => context.Compilation.GetTypeByMetadataName(name))
                .Where(type => type != null)
                .Select(type => type!)
                .Concat(new[] { events })
                .ToArray();
            var extensions = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.ModEventExtensions");
            var scan = new FixedUpdateInputScan(events, input, deferring, extensions);
            context.RegisterOperationAction(scan.AnalyzeSubscription, OperationKind.Invocation);
            context.RegisterOperationAction(scan.AnalyzeEdgeRead, OperationKind.PropertyReference);
            context.RegisterCompilationEndAction(scan.Report);
        }

        /// <summary>One compilation's FixedUpdate handlers and the input edges each method reads, matched at its end.</summary>
        private sealed class FixedUpdateInputScan
        {
            private readonly INamedTypeSymbol events;
            private readonly INamedTypeSymbol input;
            private readonly INamedTypeSymbol[] deferring;
            private readonly INamedTypeSymbol? extensions;
            private readonly ConcurrentDictionary<ISymbol, bool> handlers = new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
            private readonly ConcurrentBag<(ISymbol Method, Location Location, string Text)> methodReads = new ConcurrentBag<(ISymbol, Location, string)>();
            private readonly ConcurrentDictionary<Location, string> lambdaReads = new ConcurrentDictionary<Location, string>();

            /// <param name="events">The SDK's event contract, whose <c>SubscribeFixedUpdate</c> names a handler.</param>
            /// <param name="input">The SDK's input action contract, whose edges the rule reports.</param>
            /// <param name="deferring">Types whose every member subscribes or schedules the delegate it is given.</param>
            /// <param name="extensions">The SDK's own extensions on <paramref name="events"/>, which subscribe too.</param>
            internal FixedUpdateInputScan(INamedTypeSymbol events, INamedTypeSymbol input, INamedTypeSymbol[] deferring, INamedTypeSymbol? extensions)
            {
                this.events = events;
                this.input = input;
                this.deferring = deferring;
                this.extensions = extensions;
            }

            internal void AnalyzeSubscription(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                if (!IsFixedUpdateSubscription(invocation.TargetMethod))
                {
                    return;
                }

                foreach (var argument in invocation.Arguments)
                {
                    switch (HandlerOf(argument.Value)?.Target)
                    {
                        case IAnonymousFunctionOperation handler:
                            foreach (var read in handler.Body.Descendants().OfType<IPropertyReferenceOperation>())
                            {
                                if (IsEdgeRead(read) && !RunsLater(read, handler))
                                {
                                    lambdaReads.TryAdd(read.Syntax.GetLocation(), Text(read));
                                }
                            }

                            break;
                        case IMethodReferenceOperation method:
                            handlers.TryAdd(method.Method.OriginalDefinition, true);
                            break;
                    }
                }
            }

            internal void AnalyzeEdgeRead(OperationAnalysisContext context)
            {
                var read = (IPropertyReferenceOperation)context.Operation;
                if (!IsEdgeRead(read))
                {
                    return;
                }

                // The read runs whenever the innermost local function around it runs, and as part of each function around
                // that one, up to the member, until one of them is only handed to the SDK to run later. A lambda's body
                // belongs to the function around it, unless the lambda is handed on that way.
                var location = read.Syntax.GetLocation();
                var text = Text(read);
                for (var parent = read.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is IAnonymousFunctionOperation && HandedToTheSdkForLater(parent))
                    {
                        return;
                    }

                    if (parent is ILocalFunctionOperation local)
                    {
                        methodReads.Add((local.Symbol.OriginalDefinition, location, text));
                        if (OnlyHandedOnForLater(local))
                        {
                            return;
                        }
                    }
                }

                methodReads.Add((context.ContainingSymbol.OriginalDefinition, location, text));
            }

            internal void Report(CompilationAnalysisContext context)
            {
                var reported = new ConcurrentDictionary<Location, bool>();
                foreach (var pair in lambdaReads)
                {
                    if (reported.TryAdd(pair.Key, true))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InputEdgeInFixedUpdate, pair.Key, pair.Value));
                    }
                }

                foreach (var (method, location, text) in methodReads)
                {
                    if (handlers.ContainsKey(method) && reported.TryAdd(location, true))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InputEdgeInFixedUpdate, location, text));
                    }
                }
            }

            /// <summary>
            /// The delegate an argument hands over: made right there, through any conversion, or kept in a local that the
            /// same body declares with one.
            /// </summary>
            private static IDelegateCreationOperation? HandlerOf(IOperation value)
            {
                value = Unwrap(value);
                if (value is ILocalReferenceOperation local)
                {
                    var declarator = Root(value).Descendants()
                        .OfType<IVariableDeclaratorOperation>()
                        .FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Symbol, local.Local));
                    var initial = declarator?.GetVariableInitializer()?.Value;
                    if (initial != null)
                    {
                        value = Unwrap(initial);
                    }
                }

                return value as IDelegateCreationOperation;
            }

            private static IOperation Unwrap(IOperation value)
            {
                while (value is IConversionOperation conversion)
                {
                    value = conversion.Operand;
                }

                return value;
            }

            /// <summary>The value as the code around it uses it: through any conversions around it.</summary>
            private static IOperation Outermost(IOperation value)
            {
                while (value.Parent is IConversionOperation)
                {
                    value = value.Parent;
                }

                return value;
            }

            private static IOperation Root(IOperation operation)
            {
                while (operation.Parent != null)
                {
                    operation = operation.Parent;
                }

                return operation;
            }

            /// <summary>
            /// Whether <paramref name="read"/> runs only after the handler returns: it sits in a lambda or a local function
            /// that the handler only hands to the SDK to run later, as an event subscription or a scheduled callback. Such
            /// reads are not the handler's: an Update or scene subscription, or a scheduled callback, runs on the frame's own
            /// clock, and a FixedUpdate subscription is a handler that its own subscription reports. A local function the
            /// handler calls runs as part of it. The walk stops at <paramref name="handler"/>.
            /// </summary>
            private bool RunsLater(IOperation read, IOperation handler)
            {
                for (var node = read.Parent; node != null && !ReferenceEquals(node, handler); node = node.Parent)
                {
                    if (node is IAnonymousFunctionOperation && HandedToTheSdkForLater(node))
                    {
                        return true;
                    }

                    if (node is ILocalFunctionOperation local && OnlyHandedOnForLater(local))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Whether the delegate made from <paramref name="function"/>, a lambda or a method group, is only handed to the
            /// SDK to run later: straight to a member that subscribes or schedules it, or kept in a local whose every use is.
            /// </summary>
            private bool HandedToTheSdkForLater(IOperation function)
            {
                if (!(function.Parent is IDelegateCreationOperation creation))
                {
                    return false;
                }

                var value = Outermost(creation);
                if (IsDeferredArgument(value))
                {
                    return true;
                }

                var local = KeptIn(value);
                return local != null && OnlyDeferredUses(local, value);
            }

            /// <summary>Whether every use of a local function hands it to the SDK to run later; one that is called runs at once.</summary>
            private bool OnlyHandedOnForLater(ILocalFunctionOperation local)
            {
                var uses = 0;
                foreach (var operation in Root(local).Descendants())
                {
                    switch (operation)
                    {
                        case IInvocationOperation call when Same(call.TargetMethod, local.Symbol):
                            return false;
                        case IMethodReferenceOperation reference when Same(reference.Method, local.Symbol):
                            if (!HandedToTheSdkForLater(reference))
                            {
                                return false;
                            }

                            uses++;
                            break;
                    }
                }

                return uses > 0;
            }

            /// <summary>Whether every read of <paramref name="local"/> in its body hands it to the SDK to run later.</summary>
            private bool OnlyDeferredUses(ILocalSymbol local, IOperation from)
            {
                var uses = 0;
                foreach (var reference in Root(from).Descendants().OfType<ILocalReferenceOperation>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(reference.Local, local)
                        || (reference.Parent is ISimpleAssignmentOperation write && ReferenceEquals(write.Target, reference)))
                    {
                        continue;
                    }

                    if (!IsDeferredArgument(Outermost(reference)))
                    {
                        return false;
                    }

                    uses++;
                }

                return uses > 0;
            }

            /// <summary>The local a delegate is kept in: the one its declaration, or a plain assignment, stores it in.</summary>
            private static ILocalSymbol? KeptIn(IOperation value)
            {
                switch (value.Parent)
                {
                    case IVariableInitializerOperation initializer when initializer.Parent is IVariableDeclaratorOperation declarator:
                        return declarator.Symbol;
                    case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Value, value) && assignment.Target is ILocalReferenceOperation target:
                        return target.Local;
                    default:
                        return null;
                }
            }

            /// <summary>Whether <paramref name="value"/> is an argument to an SDK member that subscribes or schedules it.</summary>
            private bool IsDeferredArgument(IOperation value)
            {
                if (!(value.Parent is IArgumentOperation argument) || !(argument.Parent is IInvocationOperation call))
                {
                    return false;
                }

                var owner = call.TargetMethod.ContainingType;
                return deferring.Any(type => Is(owner, type)) || (extensions != null && SymbolEqualityComparer.Default.Equals(owner, extensions));
            }

            private static bool Same(IMethodSymbol method, IMethodSymbol local) =>
                SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, local.OriginalDefinition);

            private bool IsFixedUpdateSubscription(IMethodSymbol method) =>
                method.Name == "SubscribeFixedUpdate" && Is(method.ContainingType, events);

            /// <summary>Whether <paramref name="read"/> reads an SDK input edge; setting a mod's own property is no read.</summary>
            private bool IsEdgeRead(IPropertyReferenceOperation read)
            {
                var property = read.Property;
                if (property.Name != "WasPressed" && property.Name != "WasReleased")
                {
                    return false;
                }

                if (read.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, read))
                {
                    return false;
                }

                return Is(property.ContainingType, input);
            }

            private static bool Is(INamedTypeSymbol? type, INamedTypeSymbol contract) =>
                type != null && (SymbolEqualityComparer.Default.Equals(type, contract)
                    || type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, contract)));

            /// <summary>The read as the author wrote it: inside a conditional access, with its receiver.</summary>
            private static string Text(IOperation read)
            {
                var text = read.Syntax.ToString();
                if (text.StartsWith(".", StringComparison.Ordinal))
                {
                    var access = read.Syntax.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
                    if (access != null)
                    {
                        text = access.ToString();
                    }
                }

                return text;
            }
        }
    }
}
