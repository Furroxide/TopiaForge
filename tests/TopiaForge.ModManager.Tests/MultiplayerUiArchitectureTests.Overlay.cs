using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The connecting overlay's bindings, read from its source as C# syntax: it is a kit view, which the offline tests
    /// cannot run, so these checks hold it to what the presenters' tests prove of the screen it shows, and no comment or
    /// change of layout can fool them.
    /// </summary>
    internal static partial class MultiplayerUiArchitectureTests
    {
        private static void TheOverlayKeepsLongStepsReadable(string root)
        {
            var open = OverlayMethod(root, "Open").ToString();
            That(open.Contains("scroll.Content.VerticalPhaseStepper(ToArray(screen.StepLabels))", StringComparison.Ordinal),
                "seven full step names and progress details use full-width rows, not narrow columns");
            That(open.Contains("scroll.FixedHeight(ScrollHeight)", StringComparison.Ordinal)
                && open.Contains("body = scroll.Content.Column", StringComparison.Ordinal)
                && open.Contains("actions = opened.Content.Row", StringComparison.Ordinal),
                "long progress and failures scroll within a bounded view while cancellation stays outside it");
        }

        /// <summary>
        /// By the kit's own sizes, the seven rail rows take 304 units, so a 360-unit scroll view left the status line at its
        /// edge and a failure's advice, and most of the password field, below it. The view holds the rail, the status line
        /// and either the password row or a failure's heading with two lines of advice, each line of text counted at one and
        /// a half times its size, which no face of the kit needs; and the whole dialog still fits a 3440x1440 screen at the
        /// largest UI scale, inside the kit's safe margins.
        /// </summary>
        private static void TheOverlayShowsItsWordsWithoutScrolling(string root)
        {
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            var rail = File.ReadAllText(Path.Combine(kit, "Widgets", "TopiaForgePhaseStepper.Layout.cs"));
            var profile = File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeAccessibilityProfile.cs"));
            var overlay = File.ReadAllText(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer", "ConnectingOverlay.cs"));
            static float Read(string source, string pattern)
            {
                var match = Regex.Match(source, pattern, RegexOptions.CultureInvariant);
                That(match.Success, "the source still declares " + pattern);
                return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            static float Line(int size) => size * 1.5f;
            const float Gap = (float)TopiaForgeGap.Sm;
            var height = Read(overlay, @"const float ScrollHeight = ([0-9.]+)f;");
            var rows = ConnectingSteps.Count * Read(rail, @"RowHeight = ([0-9.]+)f;") + (ConnectingSteps.Count - 1) * Read(rail, @"RowSpacing = ([0-9.]+)f;");
            var failure = Line(TopiaForgeTokens.HeadingSize) + Gap + 2 * Line(TopiaForgeTokens.BodySize);
            var password = TopiaForgeTokens.ControlHeight + (float)TopiaForgeGap.Xs + Line(TopiaForgeTokens.CaptionSize);
            var needed = rows + Gap + Line(TopiaForgeTokens.BodySize) + Gap + Math.Max(failure, password);
            That(height >= needed,
                "the rail, the status line and a failure's advice or the password field show without scrolling (" + needed + " of " + height + ")");

            var largest = Read(profile, @"ClampFinite\(uiScale, [0-9.]+f, ([0-9.]+)f");
            var screen = (float)(Math.Sqrt(1440.0 / 3440.0) * Math.Sqrt(TopiaForgeTokens.ReferenceWidth * TopiaForgeTokens.ReferenceHeight) / largest);
            var dialog = 2 * (float)TopiaForgeGap.Xl + Line(TopiaForgeTokens.TitleSize) + 2 * (float)TopiaForgeGap.Md + height
                + TopiaForgeTokens.ControlHeight;
            That(dialog + 2 * TopiaForgeTokens.SafeMargin <= screen,
                "and the dialog fits an ultrawide screen at UI scale " + largest + " (" + dialog + " of " + screen + ")");
        }

        /// <summary>
        /// Seen in the game: a RETRY that failed at once kept the earlier failure's heading and advice under the new failure's
        /// status line, because the failure body was built only when the overlay's layout changed, and a failure that
        /// replaces another in one frame keeps the failure layout. The heading, the words and the host's words are bound
        /// on every frame instead, through the kit's dirty-checked setters, outside any condition.
        /// </summary>
        private static void TheOverlayShowsTheCurrentFailure(string root)
        {
            var bind = OverlayMethod(root, "Bind").Body!.Statements.Select(statement => statement.ToString()).ToArray();
            foreach (var bound in new[]
            {
                "failureTitle?.SetText(screen.FailureTitle);",
                "failureMessage?.SetText(screen.FailureMessage);",
                "hostSays?.SetText(screen.HostSays);"
            })
            {
                That(bind.Contains(bound, StringComparer.Ordinal), "the connecting overlay binds the current failure on every frame: " + bound);
            }
        }

        /// <summary>
        /// Seen in the game: a follow that left the player at the menu, as a level the game could not load does, showed a CANCEL
        /// that cancelled nothing. The overlay's layout follows whether Cancel would stop anything, and the layout for when it
        /// would not offers CLOSE, which dismisses the view, in CANCEL's place. An open overlay builds its body again whenever
        /// its layout changes, so the layout a view reaches while it shows is the one it offers.
        /// </summary>
        private static void TheOverlayOffersCancelOnlyWhileItStopsSomething(string root)
        {
            var closed = OverlayMethod(root, "Open").DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Single(assignment => assignment.Left.ToString() == "opened.Closed");
            var closeCalls = closed.Right.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(call => call.Expression.ToString()).ToArray();
            That(closeCalls.Contains("screen.Dismiss") && !closeCalls.Contains("screen.Cancel"),
                "Escape and the title-bar close use Dismiss, so only the first join is cancelled");
            var shape = OverlayMethod(root, "Shape");
            That(shape.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access => access.ToString() == "screen.CanCancel"),
                "the overlay's layout follows whether Cancel would stop anything: " + shape);
            var expression = shape.ExpressionBody!.Expression.ToString()
                .Replace("screen.HasFailed", "failed", StringComparison.Ordinal)
                .Replace("screen.NeedsPassword", "password", StringComparison.Ordinal)
                .Replace("screen.CanCancel", "cancel", StringComparison.Ordinal);
            var source = "public static class OverlayShape { public static int Get(bool failed, bool password, bool cancel) => " + expression + "; }";
            var compilation = CSharpCompilation.Create("OverlayShapeCheck", new[] { CSharpSyntaxTree.ParseText(source) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            That(emitted.Success, "the overlay's actual shape expression compiles independently: " + string.Join("; ", emitted.Diagnostics));
            var evaluate = (Func<bool, bool, bool, int>)Assembly.Load(bytes.ToArray()).GetType("OverlayShape")!.GetMethod("Get")!
                .CreateDelegate(typeof(Func<bool, bool, bool, int>));
            foreach (var cancel in new[] { false, true })
            {
                That(evaluate(true, false, cancel) == 2 && evaluate(true, true, cancel) == 2, "a failure always keeps its actions");
                That(evaluate(false, true, cancel) == 1, "a password prompt keeps its entry field");
            }
            That(evaluate(false, false, true) == 0, "an active join or follow offers CANCEL");
            That(evaluate(false, false, false) == 3, "a stranded or successful settled view offers CLOSE");
            var build = OverlayMethod(root, "BuildBody");
            var running = build.Body!.Statements.OfType<IfStatementSyntax>().Single(statement => statement.Condition.ToString() == "shownShape != 2");
            var buttons = running.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Button")
                .Select(call => string.Join(", ", call.ArgumentList.Arguments.Take(2).Select(argument => argument.ToString())))
                .ToArray();
            That(buttons.Contains("\"CLOSE\", screen.Dismiss") && buttons.Contains("\"CANCEL\", screen.Cancel"),
                "the running layouts offer CANCEL, and CLOSE once nothing runs (" + string.Join("; ", buttons) + ")");
            var choice = running.Statement.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(statement => statement.Condition.ToString() == "shownShape == 3");
            That(choice.Statement.ToString().Contains("Button(\"CLOSE\", screen.Dismiss", StringComparison.Ordinal)
                && choice.Else!.Statement.ToString().Contains("Button(\"CANCEL\", screen.Cancel", StringComparison.Ordinal),
                "only the settled shape binds CLOSE to Dismiss; active shapes bind CANCEL to Cancel");
            var tick = OverlayMethod(root, "Tick").Body!.Statements.Select(statement => statement.ToString()).ToArray();
            That(tick.Contains("if (Shape() != shownShape) BuildBody();", StringComparer.Ordinal),
                "an open overlay builds its body again whenever its layout changes, so a join that settles or fails while it shows gets CLOSE or its failure view");
        }

        /// <summary>The connecting overlay's one method named <paramref name="name"/>, parsed from its source.</summary>
        private static MethodDeclarationSyntax OverlayMethod(string root, string name)
        {
            var path = Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer", "ConnectingOverlay.cs");
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
            var error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            That(error == null, "the connecting overlay's source reads as C# (" + error + ")");
            var methods = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == "ConnectingOverlay")
                .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
                .Where(method => method.Identifier.ValueText == name)
                .ToArray();
            That(methods.Length == 1, "the connecting overlay declares " + name + " once (" + methods.Length + ")");
            return methods[0];
        }
    }
}
