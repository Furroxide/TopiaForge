using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeSpeechOwnershipTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            var host = Read("src/TopiaForge.ModManager/NativeRobotSpeechHost.cs")
                .Replace("using Cysharp.Threading.Tasks;", "").Replace("UniTask", "Task");
            var boundary = (string)typeof(NativeRobotSpeechHostTests).GetField("Boundary", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            var speech = CSharpSyntaxTree.ParseText(Read("mods/TopiaForge.RobotKit/RobotAgent.Speech.cs")).GetRoot();
            var methods = string.Join("\n", speech.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text is "BindSpeechPlayback" or "OwnsSpeechAssociation" or "PlaySpeechAsync").Select(m => m.ToFullString()));
            boundary = boundary.Replace("/* binding methods */", methods);
            var sources = new[] { host, Read("mods/TopiaForge.RobotKit/NativeRobotSpeech.cs"),
                Read("src/TopiaForge.Mods.Abstractions/Internal/IInternalNativeSpeechHost.cs"), boundary, Scenarios };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("NativeSpeechOwnershipProbe", sources.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("OwnershipProbe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
        }

        private const string Scenarios = """
using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.ModManager;
using TopiaForge.RobotKit;
using System.Reflection;
public static class OwnershipProbe {
 public static void Run() {
  using var life=new Lifetime();
  var lease=NativeRobotSpeechHost.ForProvider("io.github.furroxide.topiaforge.robotkit",life)!.Install(new ForeignRouter());
  var head=new AgentHead(); bool rejected=false;
  // Exact review reproduction against the old root API; its replacement must refuse registration instead.
  var direct=lease.GetType().GetMethod("PlayAsync");
  try {
   if(direct!=null) ((Task)direct.Invoke(lease,new object[]{head,"line",CancellationToken.None})!).GetAwaiter().GetResult();
   else lease.GetType().GetMethod("Register")!.Invoke(lease,new object[]{head,new object()});
  } catch(TargetInvocationException error) when(error.InnerException is InvalidOperationException) { rejected=true; }
    catch(InvalidOperationException) { rejected=true; }
  Console.WriteLine("False-router arbitrary-head reproduction: native calls="+head.NativeCalls+", rejected="+rejected);
  if(head.NativeCalls!=0 || !rejected)throw new InvalidOperationException("An unregistered head received native playback authority.");
  life.Dispose();
  BoundPlayback();
 }
 static Context New() {var c=new Context();c.NativeSpeech=NativeRobotSpeechHost.ForProvider("io.github.furroxide.topiaforge.robotkit",c.Life);NativeRobotSpeech.Install(c);return c;}
 static IInternalNativeSpeechLease Root() {
  var p=typeof(NativeRobotSpeech).GetField("current",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
  return (IInternalNativeSpeechLease)typeof(NativeRobotSpeech).GetField("lease",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(p)!;
 }
 static RobotAgent Agent(AgentHead h) {var a=new RobotAgent(){speechHead=h};NativeRobotSpeech.Register(h,a);return a;}
 static void Refuse(string name,AgentHead h,Action action) {
  var before=h.NativeCalls;bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}
  if(!rejected||h.NativeCalls!=before)throw new Exception(name+" entered native speech or was accepted");
  Console.WriteLine(name+": native calls during refusal=0");
 }
 static void BoundPlayback() {
  var context=New();var root=Root();var head=new AgentHead();var agent=Agent(head);
  var association=new object();
  Refuse("registered head without canonical association",head,()=>root.Register(head,association));
  agent.BindSpeechPlayback(association,()=>true);var valid=agent.speechPlayback!;
  valid.PlayAsync("valid",default).GetAwaiter().GetResult();if(head.NativeCalls!=1)throw new Exception("valid playback missing");
  var foreignHead=new AgentHead();var foreign=Agent(foreignHead);var foreignOwner=new object();foreign.BindSpeechPlayback(foreignOwner,()=>true);
  Refuse("cross-context association",head,()=>root.Register(head,foreignOwner));
  Refuse("foreign entity registration",head,()=>NativeRobotSpeech.BindPlayback(head,foreign,association));
  var alive=true;agent.BindSpeechPlayback(new object(),()=>alive);var rebound=agent.speechPlayback!;
  Refuse("retired consuming context",head,()=>valid.PlayAsync("stale",default).GetAwaiter().GetResult());
  alive=false;Refuse("withdrawn canonical state",head,()=>rebound.PlayAsync("withdrawn",default).GetAwaiter().GetResult());
  alive=true;NativeRobotSpeech.Release(head,agent);
  Refuse("released registered head",head,()=>rebound.PlayAsync("released",default).GetAwaiter().GetResult());
  var replacement=Agent(head);replacement.BindSpeechPlayback(new object(),()=>true);var replacementPlayback=replacement.speechPlayback!;
  NativeRobotSpeech.Release(head,agent);
  Refuse("retired entity on reused head",head,()=>rebound.PlayAsync("reused",default).GetAwaiter().GetResult());
  replacementPlayback.PlayAsync("replacement",default).GetAwaiter().GetResult();if(head.NativeCalls!=2)throw new Exception("stale release removed replacement");
  head.Pending=new();var pending=replacementPlayback.PlayAsync("pending",default);replacement.BindSpeechPlayback(new object(),()=>true);
  try{pending.GetAwaiter().GetResult();throw new Exception("rebinding did not cancel native work");}catch(OperationCanceledException){}
  head.Pending=null;
  var queuedHead=new AgentHead();var queued=Agent(queuedHead);queued.BindSpeechPlayback(new object(),()=>true);
  queued.speechSerial.Wait();var resolves=NativeRobotVoice.Resolves;
  var waiting=queued.PlaySpeechAsync("voice","queued",default);
  if(waiting.IsCompleted)throw new Exception("queued speech did not wait");
  queued.BindSpeechPlayback(new object(),()=>true);queued.speechSerial.Release();
  if(waiting.GetAwaiter().GetResult().Succeeded || queuedHead.NativeCalls!=0 || queuedHead.Overrides!=0 || NativeRobotVoice.Resolves!=resolves)
   throw new Exception("retired queued speech touched native voice state");
  Console.WriteLine("retired queued context: native calls=0, voice overrides=0, voice resolutions=0");
  var stoppedHead=new AgentHead();var stoppedAgent=Agent(stoppedHead);using var consumingOwner=new CancellationTokenSource();
  stoppedAgent.BindSpeechPlayback(new object(),()=>true,consumingOwner.Token);var retained=stoppedAgent.speechPlayback!;
  stoppedHead.Pending=new();var speaking=retained.PlayAsync("owner-active",CancellationToken.None);
  consumingOwner.Cancel();
  try{speaking.GetAwaiter().GetResult();throw new Exception("consumer stopping did not cancel native playback");}catch(OperationCanceledException){}
  stoppedHead.Pending=null;
  Refuse("stopped consuming owner with no caller token",stoppedHead,()=>retained.PlayAsync("owner-stopped",CancellationToken.None).GetAwaiter().GetResult());
  context.Life.Dispose();var next=New();
  Refuse("retired provider installation",head,()=>replacement.speechPlayback!.PlayAsync("old-provider",default).GetAwaiter().GetResult());
  next.Life.Dispose();
  Console.WriteLine("Native speech ownership: registered playback, 9 zero-entry refusal cases and queued retirement passed.");
 }
 sealed class ForeignRouter:IInternalNativeSpeechRouter {
  public bool TryRoute(object h,string t,CancellationToken ct,bool supported,out Task<OperationResult<bool>>? result){result=null;return false;}
  public bool IsCurrent(object head,object association)=>false;
  public CancellationToken AssociationStopping(object head,object association)=>default;
  public void OnStopped() { }
 }
}
""";
    }
}
