using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotNativeDriverTests
    {
        internal static void Run()
        {
            // Execute the shipped intent methods and frame driver against controlled native boundaries.
            // This includes cancellation/retarget wiring, not only a parallel copy of the arrival rule.
            var path = Path.Combine(Program.FindRepoRoot(), "mods/TopiaForge.RobotKit/RobotAgent.cs");
            var names = new[] { "ConfigureMovement", "MoveTo", "Chase", "Stop", "Step", "CancelWalk" };
            var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => names.Contains(m.Identifier.Text))
                .Select(m => m.ToFullString()).ToArray();
            if (methods.Length != names.Length) throw new InvalidOperationException("Production movement methods are missing.");
            var source = Contract.Replace("/* production */", string.Join("\n", methods));
            var completion = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "mods/TopiaForge.RobotKit/NativeWalkCompletion.cs"));
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("RobotNativeDriverProbe",
                new[] { source, completion }.Select(s => CSharpSyntaxTree.ParseText(s)), refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.RobotAgent", true)!;
            ((Action)probe.GetMethod("Scenarios")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Robot native driver: 8 production-path scenarios passed.");
        }

        private const string Contract = """
using System;
using System.Threading;
using UnityEngine;
using TopiaForge.Mods;
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c) { x=a;y=b;z=c; }
  public float sqrMagnitude => x*x+y*y+z*z; public float magnitude => MathF.Sqrt(sqrMagnitude);
  public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
 }
 public class Transform { public Vector3 position; }
 public class GameObject { public Transform transform = new(); }
 public static class Time { public static float time; }
 public static class Mathf { public static float Max(float a,float b) => Math.Max(a,b); }
}
namespace TopiaForge.Mods {
 public struct Vec3 { public float X,Y,Z; public Vec3(float x,float y,float z) { X=x;Y=y;Z=z; } }
 public interface IEntity { }
 public enum RobotBrainMode { Dormant, Autonomous }
 public enum RobotGait { Walk, Run }
 public enum ModErrorCode { InvalidArgument }
 public class RobotMovementSettings { public float MoveSpeed,TurnSpeed,StopDistance; public RobotGait Gait; }
 public class OperationResult<T> {
  public static OperationResult<T> Success(T v) => new();
  public static OperationResult<T> Failure(ModErrorCode c,string s) => new();
 }
}
namespace TopiaForge.Mods.Internal { public interface IInternalNativeObjectEntity { object NativeObject { get; } } }
public class ActionSuccess { public bool ShouldIgnore => false; }
namespace TopiaForge.RobotKit {
 public interface INativeEntityAdapter { GameObject NativeGameObject { get; } }
 public static class RobotKitDiagnostics { public static void ReportOnce(string s,Exception e) { } }
 public static class RobotKitLog { public static string ExceptionType(Exception e) => e.GetType().Name; }
 public class Logger { public void Debug(string s) { } }
 public static class GameReflection { public static bool HasKilledComponent(GameObject g) => false; }
 internal static class LocomotionBridge {
  public class Awaiter { public bool IsCompleted { get; set; } public int Reads;
   public ActionSuccess GetResult() { if (++Reads!=1) throw new Exception("double consume"); return new(); } }
  public static int Starts; public static Awaiter Last = new();
  public static object ResolveHead(GameObject g) => g;
  public static bool IsInControl(GameObject g) => true;
  public static object BeginWalk(object h,GameObject? o,Vector3 p,float d,RobotGait g,CancellationToken c)
   { Starts++; return Last=new(); }
  public static NativeWalkCompletion.Result PollWalk(object a,bool c) => NativeWalkCompletion.Poll(a,c);
 }
 public class RobotAgent {
  private enum Intent { None,MoveTo,Chase }
  private Intent intent; private Vector3 moveToPosition; private GameObject? chaseTarget;
  private object? pendingWalkAwaiter,head,replicaAnimation; private CancellationTokenSource? walkCts;
  private bool headResolved,isMoving,hasReachedTarget,despawned; private float nextWalkAttemptTime;
  private float moveSpeed,turnSpeed,stopDistance=0.75f; private RobotGait gait;
  private RobotBrainMode brainMode; private GameObject go=new(); private Transform transform => go.transform;
  private Logger logger=new(); private const float WalkRetryCooldown=0.25f;
  private bool IsAlive => !despawned;
  private void ApplySpeeds() { } private void AnimateReplica() { } private void ReleaseWalk() { }
  private RobotGait ToBridgeGait(RobotGait g) => g;
  private OperationResult<bool> ReplicaRefusal() => new(); private OperationResult<bool> Unavailable(string s) => new();
  /* production */
  private static void Check(bool ok,string s) { if (!ok) throw new Exception(s); }
  private static RobotAgent Walking() {
   Time.time=0; LocomotionBridge.Starts=0; var a=new RobotAgent(); a.MoveTo(new Vec3(5,0,0)); a.Step(); return a;
  }
  public static void Scenarios() {
   foreach(var endpoint in new[]{0.771f,0.793f,0.805f}) {
    var a=Walking(); a.transform.position=new Vector3(5-endpoint,0,0);
    LocomotionBridge.Last.IsCompleted=true; a.Step();
    Check(a.hasReachedTarget && !a.isMoving,"native completion must satisfy the real driver");
    for(int i=0;i<30;i++) { Time.time++; a.MoveTo(new Vec3(5,0,0)); a.Step(); }
    Check(LocomotionBridge.Starts==1 && LocomotionBridge.Last.Reads==1,"completed walk must not restart or reconsume");
   }
   { var a=Walking(); var old=LocomotionBridge.Last; a.MoveTo(new Vec3(6,0,0)); old.IsCompleted=true; a.Step();
     Check(!a.hasReachedTarget && old.Reads==1,"retarget must reject old success"); Time.time=1; a.Step();
     Check(LocomotionBridge.Starts==2,"new target must start after draining"); }
   { var a=Walking(); a.Stop(); LocomotionBridge.Last.IsCompleted=true; a.Step(); Time.time=1; a.Step();
     Check(!a.hasReachedTarget && !a.isMoving && LocomotionBridge.Starts==1,"stop must not complete or restart"); }
   { var a=Walking(); a.ConfigureMovement(new RobotMovementSettings { StopDistance=0.1f });
     LocomotionBridge.Last.IsCompleted=true; a.Step(); Check(!a.hasReachedTarget,"old tuning cannot complete new walk"); }
   { var a=Walking(); a.ConfigureMovement(new RobotMovementSettings { StopDistance=0.75f });
     a.transform.position=new Vector3(4.2f,0,0); LocomotionBridge.Last.IsCompleted=true; a.Step();
     Check(a.hasReachedTarget && LocomotionBridge.Starts==1,"unchanged tuning preserves the in-flight walk"); }
   { var a=Walking(); a.transform.position=new Vector3(4.2f,0,0); LocomotionBridge.Last.IsCompleted=true; a.Step();
     a.transform.position=new Vector3(3,0,0); Time.time=1; a.Step();
     Check(!a.hasReachedTarget && LocomotionBridge.Starts==2,"displacement outside release boundary restarts pursuit"); }
  }
 }
}
""";
    }
}
