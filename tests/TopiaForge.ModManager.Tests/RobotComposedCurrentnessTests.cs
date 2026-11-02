using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotComposedCurrentnessTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            string Method(string file, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name).ToFullString();
            // Compile the complete production guard, the actual scene dispatch and actor Update.
            // Only Unity/native objects and immutable observation data are stubbed.
            var source = Contract.Replace("/* scene current */", Method("RobotComposedScene.cs", "IsCurrent"))
                .Replace("/* update */", Method("RobotComposedLifetime.cs", "Update"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ComposedCurrentnessProbe", new[] {
                CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "RobotComposedCurrentness.cs"))),
                CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.Probe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Composed production currentness: transient recovery, cached getters, replacement retirement passed.");
        }

        private const string Contract = """
using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using TopiaForge.Mods;
namespace UnityEngine {
 public class Object {
  public bool Destroyed;
  public static bool operator ==(Object? a,Object? b) => ReferenceEquals(a,b)
   || ReferenceEquals(a,null) && b!.Destroyed || ReferenceEquals(b,null) && a!.Destroyed;
  public static bool operator !=(Object? a,Object? b) => !(a==b);
  public override bool Equals(object? o) => ReferenceEquals(this,o);
  public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
 }
 public class Scene { public int handle=7; }
 public class GameObject : Object { public Scene scene=new(); }
 public class Component : Object { public GameObject gameObject=new(); }
 public class Transform : Component { }
}
namespace TopiaForge.Mods {
 public enum WorldSessionPhase { StartingMode, Running, Stopping }
 public class Session { public string SessionId="session"; }
 public class Map { public bool IsAlive=true; }
 public class Identity { public int InstanceId=7; }
 public class WorldReadiness { public Identity Scene=new(); public Map Map=new(); }
 public class WorldSessionSnapshot {
  public WorldSessionPhase Phase=WorldSessionPhase.Running;
  public Session Session=new(); public WorldReadiness World=new();
 }
}
namespace TopiaForge.RobotKit {
 internal static class RobotKitDiagnostics { internal static void ReportOnce(string s,Exception e) { } }
 public class Config { }
 public class Root {
  public Transform? Value; public int Reads;
  public Transform? ActiveImportRoot { get { Reads++; return Value; } }
 }
 public class Host : Component {
  public Config? Config; public Root? Root; public int ConfigReads,RootReads;
  public Config? RuntimeAssetConfig { get { ConfigReads++; return Config; } }
  public Root? SceneRoot { get { RootReads++; return Root; } }
 }
 public class WrongHost : Component { public object RuntimeAssetConfig => new(); public Root SceneRoot => new(); }
 internal class RobotComposedScene {
  internal RobotComposedCurrentness currentness=null!;
  /* scene current */
  internal bool Current() => IsCurrent();
 }
 internal class RobotComposedLifetime {
  internal GameObject? Actor=new(); internal Func<bool>? Current; internal bool Disposed;
  internal void Dispose() { Disposed=true; }
  /* update */
  internal void Frame() => Update();
 }
 public static class Probe {
  private static void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
  private sealed class Rig {
   internal WorldSessionSnapshot State=new(); internal Config Config=new(); internal Root Root=new(); internal Transform Map=new();
   internal Host Host=new(); internal RobotComposedScene Scene=new(); internal RobotComposedLifetime Actor=new();
   internal Rig(WorldSessionPhase phase=WorldSessionPhase.Running) {
    State.Phase=phase;
    Host.Config=Config; Host.Root=Root; Root.Value=Map;
    Scene.currentness=new RobotComposedCurrentness(()=>State,State,Host,Config,Root,Map);
    Actor.Current=Scene.Current;
   }
   internal void Publish() { State=new WorldSessionSnapshot { World=State.World,Session=State.Session }; }
  }
  public static void Run() {
   var rebound=new Rig(WorldSessionPhase.StartingMode); var firstMap=rebound.State.World.Map; rebound.Publish();
   rebound.State.World=new WorldReadiness { Scene=rebound.State.World.Scene,Map=new Map() };
   rebound.Actor.Frame(); Check(!rebound.Actor.Disposed,"context readiness wrapper rebinding retains same live world");
   Check(firstMap.IsAlive && !ReferenceEquals(firstMap,rebound.State.World.Map)
    && rebound.State.Phase==WorldSessionPhase.Running && rebound.Scene.currentness.IsVerified,
    "StartingMode to Running accepts a distinct live map wrapper in the same scene/session");
   rebound.Publish(); rebound.State.World=new WorldReadiness { Scene=rebound.State.World.Scene,Map=new Map() };
   rebound.Actor.Frame(); Check(!rebound.Actor.Disposed && rebound.Scene.currentness.IsVerified
    && rebound.Host.ConfigReads==2 && rebound.Host.RootReads==2 && rebound.Root.Reads==2,
    "further publication revalidates identities once without requiring map wrapper equality");
   var ended=new Rig(); var originalMap=ended.State.World.Map; ended.Publish();
   ended.State.World=new WorldReadiness { Scene=ended.State.World.Scene }; originalMap.IsAlive=false;
   ended.Actor.Frame(); Check(ended.Actor.Disposed,"new live map cannot adopt actor after original map lifetime ends");
   var stable=new Rig(); stable.Publish(); stable.Actor.Frame();
   Check(!stable.Actor.Disposed && stable.Host.ConfigReads==1 && stable.Host.RootReads==1 && stable.Root.Reads==1,"new snapshot checks identities once");
   for(int i=0;i<200;i++) stable.Actor.Frame();
   long before=GC.GetAllocatedBytesForCurrentThread();
   for(int i=0;i<10000;i++) stable.Actor.Frame();
   long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
   Check(allocated==0,"stable production frame allocation must be zero");
   Check(stable.Host.ConfigReads==1 && stable.Host.RootReads==1 && stable.Root.Reads==1,"stable frames must not repeat native getters/reflection");
   foreach(int missing in new[]{0,1,2}) {
    var r=new Rig(); r.Publish();
    if(missing==0) r.Host.Config=null; if(missing==1) r.Host.Root=null; if(missing==2) r.Root.Value=null;
    r.Actor.Frame(); Check(!r.Actor.Disposed,"transient null must not retire actor");
    Check(!r.Scene.currentness.IsVerified,"incomplete snapshot must not authorize construction");
    r.Host.Config=r.Config; r.Host.Root=r.Root; r.Root.Value=r.Map;
    r.Actor.Frame(); Check(!r.Actor.Disposed && r.Scene.currentness.IsVerified,"same snapshot recovers without time threshold");
    int reads=r.Host.ConfigReads; r.Actor.Frame(); Check(r.Host.ConfigReads==reads,"recovered snapshot is cached");
   }
   foreach(int replacement in new[]{0,1,2,3,4,5,6,7,8}) {
    var r=new Rig(); r.Publish();
    if(replacement==0) r.Host.Config=new Config();
    if(replacement==1) r.Host.Root=new Root();
    if(replacement==2) r.Root.Value=new Transform();
    if(replacement==3) r.State.World=new WorldReadiness();
    if(replacement==4) r.State.Phase=WorldSessionPhase.Stopping;
    if(replacement==5) r.State.Session=new Session { SessionId="replacement" };
    if(replacement==6) r.State.World.Scene.InstanceId=8;
    if(replacement==7) r.Map.Destroyed=true;
    if(replacement==8) r.Host.Destroyed=true;
    r.Actor.Frame(); Check(r.Actor.Disposed,"real replacement/lifetime change retires actor");
    Check(!r.Scene.currentness.IsVerified,"retired candidate cannot authorize construction");
   }
   var deadMap=new Rig(); deadMap.State.World.Map.IsAlive=false; deadMap.Actor.Frame();
   Check(deadMap.Actor.Disposed,"dead map checked even on stable snapshot");
   var mixed=new Rig(); mixed.Publish(); mixed.Host.Config=null; mixed.Host.Root=new Root(); mixed.Actor.Frame();
   Check(mixed.Actor.Disposed,"null must not hide a definite replacement");
   var wrong=new Rig(); bool refused=false;
   try { _=new RobotComposedCurrentness(()=>wrong.State,wrong.State,new WrongHost(),wrong.Config,wrong.Root,wrong.Map); }
   catch(InvalidOperationException) { refused=true; }
   Check(refused,"wrong runtime getter type refused at binding");
  }
 }
}
""";
    }
}
