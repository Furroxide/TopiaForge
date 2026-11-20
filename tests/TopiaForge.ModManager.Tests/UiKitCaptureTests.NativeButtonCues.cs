using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeButtonCueTransitionsStayIndependent()
        {
            var path = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeCueButton.cs");
            var sources = new[] { File.ReadAllText(path), ButtonCueNativeContract };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(reference => MetadataReference.CreateFromFile(reference));
            var probe = CSharpCompilation.Create("KitNativeButtonCueProbe",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var result = probe.Emit(image);
            Check(result.Success, "The native cue driver compiles against the Button contract: " + string.Join(", ", result.Diagnostics));
            var scenarios = Assembly.Load(image.ToArray()).GetType("NativeButtonCueScenarios", throwOnError: true)!;
            ((Action)scenarios.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
        }

        private const string ButtonCueNativeContract = """
            using System;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine.EventSystems;

            namespace TopiaForge.Mods
            {
                [Flags]
                internal enum UiButtonCue { None = 0, Selection = 1, Press = 2 }
            }

            namespace UnityEngine.EventSystems
            {
                public sealed class BaseEventData { }
            }

            namespace UnityEngine.UI
            {
                public class Button
                {
                    protected enum SelectionState { Normal, Highlighted, Pressed, Selected, Disabled }

                    private bool selected;
                    private bool pressed;
                    private bool enabled = true;

                    public bool IsInteractable() => enabled;
                    public virtual void OnSelect(BaseEventData data) { selected = true; Refresh(); }
                    public virtual void OnDeselect(BaseEventData data) { selected = false; Refresh(); }
                    protected virtual void OnDisable() { selected = false; pressed = false; }
                    protected virtual void DoStateTransition(SelectionState state, bool instant) { }
                    public void Disable() { enabled = false; OnDisable(); }
                    public void Enable() { enabled = true; Refresh(); }
                    public void PointerDown() { pressed = true; Refresh(); }
                    public void PointerUp() { pressed = false; Refresh(); }
                    public void Submit() => DoStateTransition(SelectionState.Pressed, false);
                    public void Refresh() => DoStateTransition(!enabled ? SelectionState.Disabled
                        : pressed ? SelectionState.Pressed : selected ? SelectionState.Selected : SelectionState.Normal, false);
                }
            }

            public static class NativeButtonCueScenarios
            {
                public static void Run()
                {
                    var button = new TopiaForgeCueButton();
                    var input = new BaseEventData();
                    var shown = UiButtonCue.None;
                    var paints = 0;
                    button.ObserveCues(cues => { shown = cues; paints++; });
                    var both = UiButtonCue.Selection | UiButtonCue.Press;
                    button.ConfigureCues(both);
                    Require(shown == UiButtonCue.None && paints == 0, "Rest leaves both marks hidden.");
                    button.OnSelect(input);
                    Require(shown == UiButtonCue.Selection, "Keyboard selection displays the leading bar.");
                    button.Submit();
                    Require(shown == both, "Submit shows the inner outline and retains the selected bar.");
                    button.Refresh();
                    Require(shown == UiButtonCue.Selection, "A settled submit retires only the press mark.");
                    button.PointerDown();
                    Require(shown == both, "Pointer press while selected displays both marks.");
                    button.OnDeselect(input);
                    Require(shown == UiButtonCue.Press, "Moving focus while held clears only the selected bar.");
                    button.PointerUp();
                    Require(shown == UiButtonCue.None, "Release hides the press mark.");
                    button.OnSelect(input);
                    button.PointerDown();
                    button.ConfigureCues(UiButtonCue.Press);
                    Require(shown == UiButtonCue.Press, "A palette change removes a superseded selection mark mid-press.");
                    button.Disable();
                    Require(shown == UiButtonCue.None && !button.IsSelected, "Disabling clears both states.");
                    button.Enable();
                    for (var i = 0; i < 5000; i++) button.Refresh();
                    paints = 0;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++) button.Refresh();
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Require(allocated == 0 && paints == 0, "A settled native button allocates no bytes or repaint calls.");
                    Console.WriteLine("Native button cue transitions allocated 0 B across 4,096 unchanged states.");
                }

                private static void Require(bool condition, string detail)
                {
                    if (!condition) throw new InvalidOperationException(detail);
                }
            }
            """;
    }
}
