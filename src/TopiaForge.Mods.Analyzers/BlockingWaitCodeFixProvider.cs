using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>
    /// Fixes TF1008 on a generated multiplayer command: the blocking wait becomes a call to the command's answered
    /// <c>Submit…</c> method, and the statements that used the confirmation move into its answer callback.
    /// </summary>
    /// <remarks>
    /// Only the IDE loads this type; the command-line compiler instantiates the analyzers alone, which never touch the
    /// workspace assemblies it references.
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BlockingWaitCodeFixProvider))]
    [Shared]
    public sealed class BlockingWaitCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("TF1008");

        /// <inheritdoc/>
        public override FixAllProvider? GetFixAllProvider() => null;

        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            foreach (var diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.TryGetValue(TopiaForgeModAnalyzer.AnsweredSubmitProperty, out var answered)
                    || string.IsNullOrEmpty(answered))
                {
                    continue;
                }

                var wait = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                    .FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                var rewrite = wait == null ? null : AnsweredSubmitRewrite.Plan(wait, answered!);
                if (rewrite == null)
                {
                    continue;
                }

                var document = context.Document;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Call " + answered + " with an answer callback",
                        _ => Task.FromResult(document.WithSyntaxRoot(rewrite.Apply(root))),
                        equivalenceKey: "TF1008." + answered),
                    diagnostic);
            }
        }
    }

    /// <summary>
    /// One planned rewrite of a blocking wait on a generated command. It is planned only when the result still compiles
    /// and keeps its meaning: the wait is a whole statement, or initializes the one local of a statement whose followers
    /// can run later inside a callback.
    /// </summary>
    internal sealed class AnsweredSubmitRewrite
    {
        private readonly SyntaxNode replaced;
        private readonly IReadOnlyList<StatementSyntax> moved;
        private readonly StatementSyntax replacement;

        private AnsweredSubmitRewrite(SyntaxNode replaced, IReadOnlyList<StatementSyntax> moved, StatementSyntax replacement)
        {
            this.replaced = replaced;
            this.moved = moved;
            this.replacement = replacement;
        }

        internal static AnsweredSubmitRewrite? Plan(MemberAccessExpressionSyntax wait, string answered)
        {
            if (!(BlockingWaitRemedies.WaitedTask(wait) is InvocationExpressionSyntax submit))
            {
                return null;
            }

            var blocking = wait.Name.Identifier.ValueText == "Result" ? (ExpressionSyntax)wait : wait.Parent as InvocationExpressionSyntax;
            if (blocking == null || !TrySplitArguments(submit.ArgumentList, out var request, out var token))
            {
                return null;
            }

            var callee = Rename(submit.Expression, answered);
            if (callee == null)
            {
                return null;
            }

            // A whole statement: `Submit…Async(request).Wait();` or `_ = Submit…Async(request).Result;`.
            var statement = blocking.Parent as ExpressionStatementSyntax
                ?? (blocking.Parent is AssignmentExpressionSyntax assignment
                    && assignment.Left is IdentifierNameSyntax discard
                    && discard.Identifier.ValueText == "_"
                    ? assignment.Parent as ExpressionStatementSyntax
                    : null);
            if (statement != null)
            {
                return new AnsweredSubmitRewrite(
                    statement,
                    new StatementSyntax[0],
                    Call(callee, request!, null, token).WithTriviaFrom(statement));
            }

            // `var confirmation = Submit…Async(request).Result;` followed by the code that reads it.
            if (!(blocking.Parent is EqualsValueClauseSyntax initializer)
                || !(initializer.Parent is VariableDeclaratorSyntax declarator)
                || !(declarator.Parent is VariableDeclarationSyntax declaration)
                || declaration.Variables.Count != 1
                || !(declaration.Parent is LocalDeclarationStatementSyntax local)
                || local.IsConst
                || local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                || !(local.Parent is BlockSyntax block)
                || declaration.Type is RefTypeSyntax)
            {
                return null;
            }

            var following = block.Statements.Skip(block.Statements.IndexOf(local) + 1).ToArray();
            if (InStruct(local) || HoldsAScope(block, local) || !CanRunLater(following, local))
            {
                return null;
            }

            var callback = SyntaxFactory.SimpleLambdaExpression(
                SyntaxFactory.Parameter(declarator.Identifier.WithoutTrivia()),
                SyntaxFactory.Block(following));
            return new AnsweredSubmitRewrite(local, following, Call(callee, request!, callback, token).WithTriviaFrom(local));
        }

        internal SyntaxNode Apply(SyntaxNode root)
        {
            var annotated = replacement.WithAdditionalAnnotations(Formatter.Annotation);
            if (moved.Count == 0)
            {
                return root.ReplaceNode(replaced, annotated);
            }

            var block = (BlockSyntax)replaced.Parent!;
            var kept = block.Statements.TakeWhile(statement => statement != replaced).ToList();
            kept.Add(annotated);
            return root.ReplaceNode(block, block.WithStatements(SyntaxFactory.List(kept)).WithAdditionalAnnotations(Formatter.Annotation));
        }

        private static StatementSyntax Call(
            ExpressionSyntax callee,
            ArgumentSyntax request,
            LambdaExpressionSyntax? callback,
            ArgumentSyntax? token)
        {
            var arguments = new List<ArgumentSyntax> { request.WithNameColon(null).WithoutTrivia() };
            if (callback != null)
            {
                arguments.Add(SyntaxFactory.Argument(callback));
            }

            if (token != null)
            {
                arguments.Add(SyntaxFactory.Argument(token.Expression.WithoutTrivia())
                    .WithNameColon(SyntaxFactory.NameColon("cancellationToken")));
            }

            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(callee, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments))));
        }

        /// <summary>Gets the request and the optional cancellation token, positional or named; null for anything else.</summary>
        private static bool TrySplitArguments(ArgumentListSyntax list, out ArgumentSyntax? request, out ArgumentSyntax? token)
        {
            request = null;
            token = null;
            for (var index = 0; index < list.Arguments.Count; index++)
            {
                var argument = list.Arguments[index];
                if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
                {
                    return false;
                }

                var name = argument.NameColon?.Name.Identifier.ValueText;
                if (name == "request" || (name == null && index == 0))
                {
                    request = argument;
                }
                else if (name == "cancellationToken" || (name == null && index == 1))
                {
                    token = argument;
                }
                else
                {
                    return false;
                }
            }

            return request != null;
        }

        private static ExpressionSyntax? Rename(ExpressionSyntax expression, string answered)
        {
            switch (expression)
            {
                case IdentifierNameSyntax name:
                    return name.WithIdentifier(SyntaxFactory.Identifier(answered).WithTriviaFrom(name.Identifier));
                case MemberAccessExpressionSyntax access when access.Name is IdentifierNameSyntax member:
                    return access.WithName(member.WithIdentifier(SyntaxFactory.Identifier(answered).WithTriviaFrom(member.Identifier)));
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether statements that ran right after the wait can run later, inside a callback: none may leave the method
        /// or an enclosing loop, await, declare a local function, or use a ref local or a by-reference parameter.
        /// </summary>
        private static bool CanRunLater(IReadOnlyList<StatementSyntax> following, LocalDeclarationStatementSyntax local)
        {
            var byReference = ByReferenceNames(local);
            foreach (var statement in following)
            {
                if (statement is LocalFunctionStatementSyntax)
                {
                    return false;
                }

                foreach (var node in statement.DescendantNodesAndSelf(descendIntoChildren: child => !IsNestedFunction(child)))
                {
                    switch (node)
                    {
                        case ReturnStatementSyntax _:
                        case YieldStatementSyntax _:
                        case GotoStatementSyntax _:
                        case AwaitExpressionSyntax _:
                        case LocalFunctionStatementSyntax _:
                            return false;
                        case BreakStatementSyntax _:
                        case ContinueStatementSyntax _:
                            if (!EnclosedWithin(node, statement))
                            {
                                return false;
                            }

                            break;
                        case IdentifierNameSyntax name when byReference.Contains(name.Identifier.ValueText):
                            return false;
                    }
                }
            }

            return true;
        }

        private static bool IsNestedFunction(SyntaxNode node) =>
            node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax;

        /// <summary>Whether a break or continue targets a loop or switch inside the moved statement, not one around it.</summary>
        private static bool EnclosedWithin(SyntaxNode jump, StatementSyntax movedStatement)
        {
            for (var current = jump.Parent; current != null && current != movedStatement.Parent; current = current.Parent)
            {
                if (current is ForStatementSyntax || current is ForEachStatementSyntax || current is WhileStatementSyntax
                    || current is DoStatementSyntax || current is ForEachVariableStatementSyntax
                    || (jump is BreakStatementSyntax && current is SwitchStatementSyntax))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Gets the names a callback cannot capture: ref locals and by-reference parameters around the wait.</summary>
        private static HashSet<string> ByReferenceNames(SyntaxNode from)
        {
            var names = new HashSet<string>();
            foreach (var ancestor in from.Ancestors())
            {
                var parameters = ancestor switch
                {
                    BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
                    LocalFunctionStatementSyntax function => function.ParameterList.Parameters,
                    ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
                    _ => default
                };
                foreach (var parameter in parameters)
                {
                    if (parameter.Modifiers.Any(modifier =>
                            modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.OutKeyword) || modifier.IsKind(SyntaxKind.InKeyword)))
                    {
                        names.Add(parameter.Identifier.ValueText);
                    }
                }

                if (ancestor is BlockSyntax block)
                {
                    foreach (var declaration in block.Statements.OfType<LocalDeclarationStatementSyntax>())
                    {
                        if (declaration.Declaration.Type is RefTypeSyntax)
                        {
                            foreach (var variable in declaration.Declaration.Variables) names.Add(variable.Identifier.ValueText);
                        }
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Whether code that runs later would leave a scope the wait sits in: a try, using, lock or fixed statement around
        /// it, or a using declaration before it in its block. The callback would run after that scope ended.
        /// </summary>
        private static bool HoldsAScope(BlockSyntax block, LocalDeclarationStatementSyntax local)
        {
            foreach (var statement in block.Statements.TakeWhile(statement => statement != local))
            {
                if (statement is LocalDeclarationStatementSyntax declaration && declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                {
                    return true;
                }
            }

            foreach (var ancestor in block.Ancestors())
            {
                if (IsNestedFunction(ancestor) || ancestor is MemberDeclarationSyntax || ancestor is AccessorDeclarationSyntax)
                {
                    return false;
                }

                if (ancestor is TryStatementSyntax || ancestor is CatchClauseSyntax || ancestor is FinallyClauseSyntax
                    || ancestor is UsingStatementSyntax || ancestor is LockStatementSyntax || ancestor is FixedStatementSyntax)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether the wait sits in a struct, whose members a callback cannot capture: no fix is offered there.</summary>
        private static bool InStruct(SyntaxNode node)
        {
            var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return type is StructDeclarationSyntax
                || (type is RecordDeclarationSyntax record && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));
        }
    }
}
