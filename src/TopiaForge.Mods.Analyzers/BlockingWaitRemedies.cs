using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Chooses what TF1008 tells an author to do instead of waiting, by what the blocked task is.</summary>
    internal static class BlockingWaitRemedies
    {
        private const string ConfirmationType = "TopiaForge.Mods.MultiplayerCommandConfirmation<T>";
        private const string SessionType = "TopiaForge.Mods.IMultiplayerSession";
        private const string TaskType = "System.Threading.Tasks.Task<TResult>";
        private const string SubmitPrefix = "Submit";
        private const string AsyncSuffix = "Async";

        /// <summary>
        /// Returns the remedy for a wait on a multiplayer command's task, or null for any other task, which polls instead.
        /// <paramref name="answeredSubmit"/> names the generated method that takes an answer callback, when the wait is on
        /// the task of its twin, so the code fix can call it.
        /// </summary>
        internal static string? Find(
            MemberAccessExpressionSyntax wait,
            SemanticModel model,
            CancellationToken cancellationToken,
            out string? answeredSubmit)
        {
            answeredSubmit = null;
            var task = WaitedTask(wait);
            if (task == null || !IsCommandConfirmationTask(model.GetTypeInfo(task, cancellationToken).Type))
            {
                return null;
            }

            if (!(task is InvocationExpressionSyntax invocation)
                || !(model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method))
            {
                return null;
            }

            var answered = AnsweredTwin(method);
            if (answered != null)
            {
                answeredSubmit = answered;
                return "call '" + answered + "' instead: it hands the host's answer to a callback on the main thread, "
                    + "and never once the mod has stopped";
            }

            if (method.Name == SubmitPrefix + AsyncSuffix && method.ContainingType?.ToDisplayString() == SessionType)
            {
                return "submit through your contract's generated Submit method instead, which hands the host's answer to "
                    + "a callback on the main thread";
            }

            return null;
        }

        /// <summary>
        /// Gets the expression whose task a wait blocks on: <c>task.Result</c>, <c>task.Wait()</c> or
        /// <c>task.GetAwaiter().GetResult()</c>, with or without <c>ConfigureAwait</c>.
        /// </summary>
        internal static ExpressionSyntax? WaitedTask(MemberAccessExpressionSyntax wait)
        {
            if (wait.Name.Identifier.ValueText != "GetResult")
            {
                return Unwrap(wait.Expression);
            }

            if (!(Unwrap(wait.Expression) is InvocationExpressionSyntax awaiter)
                || !(awaiter.Expression is MemberAccessExpressionSyntax getAwaiter)
                || getAwaiter.Name.Identifier.ValueText != "GetAwaiter")
            {
                return null;
            }

            var receiver = Unwrap(getAwaiter.Expression);
            if (receiver is InvocationExpressionSyntax configured
                && configured.Expression is MemberAccessExpressionSyntax configure
                && configure.Name.Identifier.ValueText == "ConfigureAwait")
            {
                return Unwrap(configure.Expression);
            }

            return receiver;
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        /// <summary>
        /// Gets the name of the generated method that submits the same command with an answer callback, when
        /// <paramref name="method"/> is a generated <c>Submit…Async</c> proxy that has one.
        /// </summary>
        private static string? AnsweredTwin(IMethodSymbol method)
        {
            var name = method.Name;
            if (!name.StartsWith(SubmitPrefix, StringComparison.Ordinal)
                || !name.EndsWith(AsyncSuffix, StringComparison.Ordinal)
                || name.Length <= SubmitPrefix.Length + AsyncSuffix.Length
                || !(method.ReturnType is INamedTypeSymbol task)
                || task.TypeArguments.Length != 1
                || method.ContainingType == null)
            {
                return null;
            }

            var answered = name.Substring(0, name.Length - AsyncSuffix.Length);
            var confirmation = task.TypeArguments[0];
            foreach (var candidate in method.ContainingType.GetMembers(answered).OfType<IMethodSymbol>())
            {
                if (candidate.ReturnsVoid
                    && candidate.Parameters.Length >= 2
                    && candidate.Parameters[1].Type is INamedTypeSymbol callback
                    && callback.ConstructedFrom.ToDisplayString() == "System.Action<T>"
                    && SymbolEqualityComparer.Default.Equals(callback.TypeArguments[0], confirmation))
                {
                    return answered;
                }
            }

            return null;
        }

        private static bool IsCommandConfirmationTask(ITypeSymbol? type) =>
            type is INamedTypeSymbol task
            && task.TypeArguments.Length == 1
            && task.ConstructedFrom.ToDisplayString() == TaskType
            && task.TypeArguments[0] is INamedTypeSymbol confirmation
            && confirmation.ConstructedFrom.ToDisplayString() == ConfirmationType;
    }
}
