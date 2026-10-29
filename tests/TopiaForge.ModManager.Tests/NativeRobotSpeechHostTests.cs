using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeRobotSpeechHostTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            // Compile the shipped host, provider and internal contract; replace only the native/patch boundary.
            // Task substitutes for UniTask here, so this proves routing/lifetime behavior, not Harmony or Unity execution.
            var host = Read("src/TopiaForge.ModManager/NativeRobotSpeechHost.cs")
                .Replace("using Cysharp.Threading.Tasks;", "").Replace("UniTask", "Task");
            var speech = CSharpSyntaxTree.ParseText(Read("mods/TopiaForge.RobotKit/RobotAgent.Speech.cs")).GetRoot();
            var bindingMethods = string.Join("\n", speech.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text is "BindSpeechPlayback" or "OwnsSpeechAssociation" or "PlaySpeechAsync").Select(m => m.ToFullString()));
            var boundary = Boundary.Replace("/* binding methods */", bindingMethods);
            var sources = new[] { host, Read("mods/TopiaForge.RobotKit/NativeRobotSpeech.cs"),
                Read("src/TopiaForge.Mods.Abstractions/Internal/IInternalNativeSpeechHost.cs"), boundary, Scenarios };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("NativeSpeechHostingProbe", sources.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("Probe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Native speech hosting: 12 production-source boundary groups passed (native execution unproved).");
        }

        private const string Boundary = """
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.ModManager;
public sealed class FMODAudioClipAsset { }
public sealed class HintAsset { }
public sealed class HintGroup { public static HintGroup FromAssets(IEnumerable<HintAsset> _) => new(); }
public sealed class TaskGraph { public TaskGraph(string _) { } }
public sealed class AgentHead {
 public enum SayResult { Ok, TooFar, InvalidTarget }
 public string CurrentSpeakerName => "Speaker";
 public int NativeCalls,Overrides; public bool Throw;
 public IDisposable OverrideSpeech(string speaker,object asset){Overrides++;return new Override();}
 private sealed class Override:IDisposable {public void Dispose(){}} public Action<CancellationToken>? Reenter;
 public TaskCompletionSource<bool>? Pending; public CancellationToken LastToken;
 public Task<SayResult> Say(string text, HintGroup hints, TaskGraph graph, CancellationToken ct, FMODAudioClipAsset? synth, bool subtitles) {
  var args=new object?[]{this,text,ct,synth,subtitles,null};
  if(!OwnerHarmonyLease.Invoke(false,args)) return (Task<SayResult>)args[5]!;
  NativeCalls++; return Task.FromResult(SayResult.Ok);
 }
 public Task Say(string name,string text,HintGroup hints,TaskGraph graph,CancellationToken ct,FMODAudioClipAsset? synth,bool subtitles) {
  var args=new object?[]{this,text,ct,synth,subtitles,null};
  if(!OwnerHarmonyLease.Invoke(true,args)) return (Task)args[5]!;
  NativeCalls++; LastToken=ct;
  if(Throw) throw new InvalidOperationException("native failure");
  Reenter?.Invoke(ct);
  if(Pending==null) return Task.CompletedTask;
  ct.Register(()=>Pending.TrySetCanceled(ct)); return Pending.Task;
 }
}
namespace TopiaForge.Mods {
 public enum ModErrorCode { Unavailable, Cancelled }
 public sealed class OperationResult<T> {
  public bool Succeeded; public T? Value;
  public static OperationResult<T> Success(T value)=>new(){Succeeded=true,Value=value};
  public static OperationResult<T> Failure(ModErrorCode code,string message)=>new();
  public bool TryGetValue(out T value) { value=Value!; return Succeeded; }
 }
 public interface IModLifetime { CancellationToken StoppingToken {get;} IDisposable Track(IDisposable value); }
 public interface IModContext { IModLifetime Lifetime {get;} Logger Logger {get;} }
 public sealed class Logger { public int Warnings; public void Warn(string _) { Warnings++; } }
 public sealed class Lifetime : IModLifetime, IDisposable {
  private readonly List<IDisposable> items=new(); private readonly CancellationTokenSource stop=new(); private bool disposed;
  public CancellationToken StoppingToken=>stop.Token;
  public IDisposable Track(IDisposable value) { if(stop.IsCancellationRequested)value.Dispose(); else items.Add(value); return value; }
  public void BeginStop() { stop.Cancel(); }
  public IDisposable Last=>items[items.Count-1];
  public void Dispose() {
   if(disposed)return; disposed=true; var failures=new List<Exception>();
   try{stop.Cancel();}catch(Exception e){failures.Add(e);}
   var snapshot=items.ToArray(); items.Clear();
   for(int i=snapshot.Length-1;i>=0;i--)try{snapshot[i].Dispose();}catch(Exception e){failures.Add(e);}
   if(failures.Count!=0)throw new AggregateException(failures);
  }
 }
 internal sealed class Context : IModContext,IInternalNativeSpeechContext {
  public Lifetime Life=new(); public IModLifetime Lifetime=>Life; public Logger Logger {get;}=new();
  public IInternalNativeSpeechHost? NativeSpeech {get;set;}
 }
}
namespace TopiaForge.ModManager {
 internal static class UnityMainThreadGuard { internal static void AssertCurrent() { } }
 internal sealed class OwnerHarmonyLease : IDisposable {
  private static readonly Dictionary<bool,(OwnerHarmonyLease Owner,MethodInfo Prefix)> hooks=new();
  public static int FailPatch,PatchCalls,Unpatches; public static bool FailUnpatch; private bool disposed;
  internal static OwnerHarmonyLease Create(string owner,string purpose,IModLifetime lifetime) {
   var lease=new OwnerHarmonyLease(); lifetime.Track(lease); return lease;
  }
  internal void Patch(MethodBase original,MethodInfo? prefix) {
   PatchCalls++; if(FailPatch==PatchCalls)throw new InvalidOperationException("patch failed");
   bool named=original.GetParameters().Length==7;
   if(original.Name!="Say" || prefix==null || original.GetParameters().Length is not (6 or 7))throw new Exception("wrong binding");
   hooks.Add(named,(this,prefix));
  }
  public void Dispose() {
   if(disposed)return; if(FailUnpatch)throw new InvalidOperationException("unpatch failed"); disposed=true; Unpatches++;
   foreach(var k in new[]{false,true})if(hooks.TryGetValue(k,out var v)&&ReferenceEquals(v.Owner,this))hooks.Remove(k);
  }
  public static int Count=>hooks.Count;
  public static bool Invoke(bool named,object?[] args)=>!hooks.TryGetValue(named,out var v)||(bool)v.Prefix.Invoke(null,args)!;
 }
}
namespace TopiaForge.RobotKit {
 internal sealed class RobotAgent {
  internal int Routes; internal bool Throw;
  internal bool IsAlive=true, speechReleased;
  internal AgentHead? speechHead;
  private object? speechAssociation;
  private Func<bool>? speechAssociationCurrent;
  private CancellationToken speechOwnerStopping;
  internal CancellationToken SpeechStoppingToken=>speechOwnerStopping;
  internal IInternalNativeSpeechPlayback? speechPlayback;
  private readonly CancellationTokenSource speechLifetime=new();
  internal readonly SemaphoreSlim speechSerial=new(1,1);
  public bool SpeechVisible=>IsAlive;
  /* binding methods */
  internal TaskCompletionSource<OperationResult<bool>>? Pending;
  internal Task<OperationResult<bool>> RouteSpeech(string text,CancellationToken token) {
   Routes++; if(Throw)throw new InvalidOperationException("route failure");
   return Pending?.Task??Task.FromResult(OperationResult<bool>.Success(true));
  }
 }
 internal static class NativeRobotVoice {
  internal static int Clears,Resolves;
  internal static object Resolve(string voice){Resolves++;return new object();}
  internal static void Clear(){Clears++;}
 }
}
""";

        private const string Scenarios = """
using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.ModManager;
using TopiaForge.RobotKit;
public static class Probe {
 const string Id="io.github.furroxide.topiaforge.robotkit";
 static void Check(bool ok,string why) { if(!ok)throw new InvalidOperationException(why); }
 static Task<AgentHead.SayResult> Simple(AgentHead head,string line="line",bool unsupported=false)=>
  head.Say(line,new HintGroup(),new TaskGraph("test"),default,null,unsupported);
 static Task Named(AgentHead head,string line="line",CancellationToken ct=default,bool unsupported=false)=>
  head.Say("speaker",line,new HintGroup(),new TaskGraph("test"),ct,unsupported?new FMODAudioClipAsset():null,false);
 static Context New() { var c=new Context(); c.NativeSpeech=NativeRobotSpeechHost.ForProvider(Id,c.Life); return c; }
 static readonly System.Collections.Generic.Dictionary<AgentHead,RobotAgent> Agents=new();
 static RobotAgent Add(AgentHead h) { var a=new RobotAgent(){speechHead=h}; NativeRobotSpeech.Register(h,a); Agents[h]=a; a.BindSpeechPlayback(new object(),()=>true); return a; }
 static Task Play(AgentHead h,string text,CancellationToken token)=>Agents.TryGetValue(h,out var a)&&a.speechPlayback!=null
  ? a.speechPlayback.PlayAsync(text,token):Task.FromException(new InvalidOperationException("No speech association."));
 static void Cancelled(Task t) { try { t.GetAwaiter().GetResult(); throw new Exception("cancellation lost"); } catch(OperationCanceledException){} }
 public static void Run() {
  // 1: identity boundary, missing host and stopped lifetime never manufacture availability.
  Check(NativeRobotSpeechHost.ForProvider("example.consumer",new Lifetime())==null,"consumer gained native host");
  var missing=new Context(); NativeRobotSpeech.Install(missing);
  Check(!NativeRobotSpeech.IsInstalled && missing.Logger.Warnings==1,"missing host must be unavailable");
  var stopped=New(); stopped.Life.Dispose(); NativeRobotSpeech.Install(stopped);
  Check(!NativeRobotSpeech.IsInstalled && OwnerHarmonyLease.Count==0,"stopped lifetime installed");
  try { Play(new AgentHead(),"line",default).GetAwaiter().GetResult(); throw new Exception("false playback success"); }
  catch(InvalidOperationException){}
  // 2: first or second patch failure removes any earlier patch, allowing a clean retry.
  foreach(int fail in new[]{1,2}) {
   var c=New(); OwnerHarmonyLease.PatchCalls=0; OwnerHarmonyLease.FailPatch=fail; NativeRobotSpeech.Install(c);
   Check(!NativeRobotSpeech.IsInstalled && OwnerHarmonyLease.Count==0 && c.Logger.Warnings==1,"partial install leaked"); c.Life.Dispose();
  }
  OwnerHarmonyLease.FailPatch=0;
  var first=New(); NativeRobotSpeech.Install(first); Check(NativeRobotSpeech.IsInstalled&&OwnerHarmonyLease.Count==2,"both overloads required");
  // 3: owned direct calls await the consuming route; unowned calls keep their native overload and unsupported forms.
  var head=new AgentHead(); var agent=Add(head); var outside=new AgentHead();
  Simple(outside,unsupported:true).GetAwaiter().GetResult(); Named(outside,unsupported:true).GetAwaiter().GetResult();
  Check(outside.NativeCalls==2,"unowned native speech changed");
  agent.Pending=new(); var simple=Simple(head); Check(!simple.IsCompleted && agent.Routes==1 && head.NativeCalls==0,"simple route not awaited");
  agent.Pending.SetResult(OperationResult<bool>.Success(false)); Check(simple.Result==AgentHead.SayResult.TooFar,"result mapping lost");
  agent.Pending=new(); var named=Named(head); Check(!named.IsCompleted&&agent.Routes==2,"named route not awaited");
  agent.Pending.SetResult(OperationResult<bool>.Success(true)); named.GetAwaiter().GetResult(); agent.Pending=null;
  Check(Simple(head,unsupported:true).Result==AgentHead.SayResult.InvalidTarget,"unsupported simple must fail closed");
  Named(head,unsupported:true).GetAwaiter().GetResult(); Check(head.NativeCalls==0&&agent.Routes==2,"unsupported named leaked");
  agent.Throw=true; Check(Simple(head).Result==AgentHead.SayResult.InvalidTarget,"synchronous router failure escaped"); agent.Throw=false;
  // 4: authorized playback bypasses exactly the initial call and awaits actual native completion.
  head.Pending=new(); int routed=agent.Routes; var play=Play(head,"authorized",default);
  Check(!play.IsCompleted&&head.NativeCalls==1&&agent.Routes==routed,"playback did not await/bypassed routing incorrectly");
  head.Pending.SetResult(true); play.GetAwaiter().GetResult(); head.Pending=null;
  // 5: nested calls on this head and another head cannot inherit the playback bypass.
  var otherHead=new AgentHead(); var otherAgent=Add(otherHead);
  head.Reenter=ct=>{Named(head,"authorized",ct).GetAwaiter().GetResult(); Named(otherHead,"authorized",ct).GetAwaiter().GetResult();};
  Play(head,"authorized",default).GetAwaiter().GetResult(); head.Reenter=null;
  Check(head.NativeCalls==2&&agent.Routes==routed+1&&otherAgent.Routes==1&&otherHead.NativeCalls==0,"reentrant speech bypassed ownership");
  head.Throw=true;
  try {Play(head,"throws",default).GetAwaiter().GetResult(); throw new Exception("native failure swallowed");}catch(InvalidOperationException){}
  head.Throw=false; Named(head,"throws").GetAwaiter().GetResult(); Check(agent.Routes==routed+2,"exception leaked bypass");
  // 6: caller cancellation and lifetime teardown reach pending native work without fabricated success.
  head.Pending=new(); using(var cancel=new CancellationTokenSource()) {
   var pending=Play(head,"cancel",cancel.Token); cancel.Cancel(); Cancelled(pending); Check(head.LastToken.IsCancellationRequested,"caller token lost");
  }
  head.Pending=new(); var ending=Play(head,"ending",default); first.Life.Dispose(); Cancelled(ending);
  Check(!NativeRobotSpeech.IsInstalled&&OwnerHarmonyLease.Count==0&&NativeRobotVoice.Clears==1,"provider teardown leaked bindings");
  // 7: reinstall after teardown sees no old associations; repeated old teardown cannot erase the new provider.
  var second=New(); NativeRobotSpeech.Install(second); var freshHead=new AgentHead(); var fresh=Add(freshHead);
  first.Life.Dispose(); Check(NativeRobotSpeech.IsInstalled&&OwnerHarmonyLease.Count==2,"stale lifetime removed new provider");
  Simple(head).GetAwaiter().GetResult(); Check(agent.Routes==routed+2,"old association survived teardown");
  Simple(freshHead).GetAwaiter().GetResult(); Check(fresh.Routes==1,"new association lost"); second.Life.Dispose();
  // 8: duplicate installation cannot replace callbacks or patches; explicit old lease disposal is generation-safe.
  var aLife=new Lifetime(); var bLife=new Lifetime(); var router=new Router();
  var a=NativeRobotSpeechHost.ForProvider(Id,aLife)!.Install(router);
  try {NativeRobotSpeechHost.ForProvider(Id,bLife)!.Install(new Router());throw new Exception("duplicate accepted");}catch(InvalidOperationException){}
  Check(a.IsAvailable&&OwnerHarmonyLease.Count==2,"duplicate displaced original"); a.Dispose();
  var b=NativeRobotSpeechHost.ForProvider(Id,bLife)!.Install(new Router()); a.Dispose();
  Check(b.IsAvailable&&OwnerHarmonyLease.Count==2,"old lease removed replacement"); b.Dispose();
  // 9: BeginStop cancels active playback and rejects new playback before resource disposal.
  var stopContext=New(); NativeRobotSpeech.Install(stopContext); var stopHead=new AgentHead(){Pending=new()}; var stopAgent=Add(stopHead);
  var stopPlay=Play(stopHead,"stop",default); stopContext.Life.BeginStop(); Cancelled(stopPlay);
  Check(!NativeRobotSpeech.IsInstalled && Simple(stopHead).Result==AgentHead.SayResult.InvalidTarget && stopAgent.Routes==0,"BeginStop admitted routed speech");
  try { Play(stopHead,"late",default).GetAwaiter().GetResult(); throw new Exception("late playback accepted"); }catch(InvalidOperationException){}
  stopContext.Life.Dispose();
  // 10: failed unpatch reserves the host; it is retryable both during teardown and partial installation.
  var retry=New(); NativeRobotSpeech.Install(retry); var retryHead=new AgentHead(); var retryAgent=Add(retryHead);
  OwnerHarmonyLease.FailUnpatch=true;
  try {retry.Life.Last.Dispose();throw new Exception("unpatch failure swallowed");}catch(InvalidOperationException){}
  Check(!NativeRobotSpeech.IsInstalled&&OwnerHarmonyLease.Count==2&&Simple(retryHead).Result==AgentHead.SayResult.InvalidTarget,"unpatch failure leaked owned speech");
  try {NativeRobotSpeechHost.ForProvider(Id,new Lifetime())!.Install(new Router());throw new Exception("replacement accepted while patches remain");}catch(InvalidOperationException){}
  OwnerHarmonyLease.FailUnpatch=false; retry.Life.Dispose(); Check(OwnerHarmonyLease.Count==0,"unpatch retry failed");
  var rollbackLife=new Lifetime(); OwnerHarmonyLease.PatchCalls=0; OwnerHarmonyLease.FailPatch=2; OwnerHarmonyLease.FailUnpatch=true;
  try {NativeRobotSpeechHost.ForProvider(Id,rollbackLife)!.Install(new Router());throw new Exception("rollback failure swallowed");}catch(InvalidOperationException){}
  Check(OwnerHarmonyLease.Count==1,"partial install failure fixture did not retain patch");
  try {NativeRobotSpeechHost.ForProvider(Id,new Lifetime())!.Install(new Router());throw new Exception("replacement accepted during rollback");}catch(InvalidOperationException){}
  OwnerHarmonyLease.FailPatch=0; OwnerHarmonyLease.FailUnpatch=false; rollbackLife.Dispose(); Check(OwnerHarmonyLease.Count==0,"rollback retry failed");
  // 11: cancellation callback failures still unpatch and release provider associations; owner disposal is idempotent.
  var throwing=New(); NativeRobotSpeech.Install(throwing); var throwHead=new AgentHead(); Add(throwHead);
  Play(throwHead,"callback",default).GetAwaiter().GetResult();
  // Hold a native operation open so its linked token still observes teardown.
  throwHead.Pending=new(); throwHead.Reenter=ct=>ct.Register(()=>throw new InvalidOperationException("cancel callback"));
  var throwPlay=Play(throwHead,"callback",default); throwHead.Reenter=null;
  try {throwing.Life.Dispose();throw new Exception("callback failure swallowed");}catch(AggregateException){}
  Check(!NativeRobotSpeech.IsInstalled&&OwnerHarmonyLease.Count==0,"callback error retained provider after unpatch");
  throwing.Life.Dispose(); Cancelled(throwPlay);
  // 12: explicit per-head release preserves other robots, including late callbacks after withdrawal.
  var final=New(); NativeRobotSpeech.Install(final); var x=new AgentHead(); var y=new AgentHead(); var ax=Add(x); var ay=Add(y);
  NativeRobotSpeech.Release(x,ax); Simple(x).GetAwaiter().GetResult(); Simple(y).GetAwaiter().GetResult();
  Check(x.NativeCalls==1&&ax.Routes==0&&ay.Routes==1&&y.NativeCalls==0,"head release escaped its association"); final.Life.Dispose();
 }
 sealed class Router:IInternalNativeSpeechRouter {
  public bool TryRoute(object h,string t,CancellationToken ct,bool supported,out Task<OperationResult<bool>>? result){result=null;return false;}
  public bool IsCurrent(object h,object association)=>false;
  public CancellationToken AssociationStopping(object h,object association)=>default;
  public void OnStopped() { }
 }
}
""";
    }
}
