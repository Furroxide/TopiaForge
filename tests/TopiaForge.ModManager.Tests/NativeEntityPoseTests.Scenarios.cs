namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeEntityPoseTests
    {
        private const string Scenarios = """
using System;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.ModManager;
using TopiaForge.RobotKit;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class PoseScenarios {
 private static int assertions;
 private static void Check(bool value,string why) { assertions++;if(!value) throw new InvalidOperationException(why); }
 private static void Refuses(LifetimeReader reader,IEntity entity,string why) {
  Check(!reader.TryGetTransform(entity,out var pose) && pose==TransformState.Identity,why);
 }
 public static void Run(Func<IRobotAgent,bool,IRobotAgent> routed) {
  UnityMainThreadGuard.CaptureCurrentThread();
  var registry=new UnityEntityRegistry();
  var parent=new ModContext();
  var child=new ModContext {World=parent.World,WorldLifetime=parent.WorldLifetime};
  foreach(var context in new[]{parent,child}) foreach(var route in new[]{0,1,2}) {
   var raw=new NativeRobot();var root=(GameObject)raw.Native!;
   IRobotAgent canonical=route==0?raw:routed(raw,route==2);
   var scope=context.CaptureNativeEntityReadScope();
   var robot=WrapperFactory.Wrap(canonical,context.Lifetime,scope);
   IEntityService service=new OwnerEntityService(context.Lifetime,registry);
   var reader=new LifetimeReader(service,context.Lifetime);
   Check(reader.TryGetTransform(robot,out var pose),"owned native robot must satisfy the public read contract");
   Check(pose.Position==new Vec3(2,3,4) && pose.Rotation==new Quat(0,0.6f,0,0.8f)
    && pose.Scale==new Vec3(2,3,4),"position, rotation and local scale come from the actual root transform");
   Check(robot.Id==raw.Id && ReferenceEquals(((IInternalNativeObjectEntity)robot).NativeObject,root),"canonical native identity is preserved through owner/routed wrappers");
   root.transform.position=new Vector3(5,6,7); root.transform.rotation=new Quaternion(0.6f,0,0,0.8f);
   Check(reader.TryGetTransform(robot,out pose) && pose.Position==new Vec3(5,6,7) && pose.Rotation.X==0.6f,
    "reads sample the current native pose, not the public Position or a movement target");
   Check(!service.SetTransform(robot,TransformState.Identity).Succeeded && !service.AcquireMotion(robot).Succeeded
    && !service.Destroy(robot).Succeeded,"pose authority grants no transform, motion or destruction ownership");
   Check(root.transform.position.x==5 && robot.IsAlive,"failed writes leave the robot untouched");
   var foreign=new LifetimeReader(new OwnerEntityService(new FakeModLifetime(),registry),new FakeModLifetime());
   var reads=raw.Reads;Refuses(foreign,robot,"a different owner/context cannot read");
   Check(raw.Reads==reads,"ownership is checked before the native seam");
   Refuses(reader,raw,"a raw native adapter has no read scope");
   Refuses(reader,new NativeRobot(),"a matching id does not attest ownership");
   var other=context==parent?child:parent;
   Refuses(new LifetimeReader(new OwnerEntityService(other.Lifetime,registry),other.Lifetime),robot,"parent and child do not borrow each other's authority");
   robot.Dispose();Refuses(reader,robot,"disposed wrapper cannot read surviving native bytes");
   Check(!raw.IsAlive,"owner disposal retires the canonical native agent");
  }
  var owner=new ModContext();var native=new NativeRobot();var go=(GameObject)native.Native!;
  var wrapper=WrapperFactory.Wrap(native,owner.Lifetime,owner.CaptureNativeEntityReadScope());
  var read=new LifetimeReader(new OwnerEntityService(owner.Lifetime,registry),owner.Lifetime);
  var saved=owner.World;owner.World=new WorldReadiness(saved!.Scene,TransformState.Identity);
  Refuses(read,wrapper,"same native scene with a replacement world invalidates the old scope");owner.World=saved;
  var worldLifetime=owner.WorldLifetime;owner.WorldLifetime=new FakeModLifetime();
  Refuses(read,wrapper,"replacement world lifetime is not the captured world");owner.WorldLifetime=worldLifetime;
  owner.World=null;Refuses(read,wrapper,"missing current world is refused");owner.World=saved;
  SceneManager.World.isLoaded=false;Refuses(read,wrapper,"unloaded native world is refused before managed callbacks");SceneManager.World.isLoaded=true;
  SceneManager.World.handle=8;Refuses(read,wrapper,"another native scene instance is refused");SceneManager.World.handle=7;
  SceneManager.World.name="Other";Refuses(read,wrapper,"another native scene name is refused");SceneManager.World.name="World";
  native.Alive=false;Refuses(read,wrapper,"dead robot is refused");native.Alive=true;
  go.Destroyed=true;Refuses(read,wrapper,"Unity destroyed-object semantics are honored");go.Destroyed=false;
  native.Native=null;Refuses(read,wrapper,"missing native object is refused");
  native.Native=new object();Refuses(read,wrapper,"malformed native object is refused");native.Native=go;
  native.Throw=true;Refuses(read,wrapper,"native adapter exceptions do not escape");native.Throw=false;
  go.Throw=true;Refuses(read,wrapper,"destroyed transform exceptions do not escape");go.Throw=false;
  foreach(var value in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity}) {
   go.transform.position=new Vector3(value,0,0);Refuses(read,wrapper,"nonfinite position is refused");go.transform.position=new Vector3(2,3,4);
   go.transform.localScale=new Vector3(1,value,1);Refuses(read,wrapper,"nonfinite scale is refused");go.transform.localScale=new Vector3(2,3,4);
   go.transform.rotation=new Quaternion(0,value,0,1);Refuses(read,wrapper,"raw nonfinite quaternion is refused before normalization");
  }
  foreach(var q in new[]{new Quaternion(0,0,0,0),new Quaternion(float.MaxValue,0,0,1),new Quaternion(1e-8f,0,0,0),new Quaternion(1e-6f,0,0,0)}) {
   go.transform.rotation=q;Refuses(read,wrapper,"degenerate quaternion cannot silently become identity");
  }
  go.transform.rotation=new Quaternion(0,0,0,1);go.transform.localScale=new Vector3(0,1,1);
  Refuses(read,wrapper,"zero scale cannot construct a transform");go.transform.localScale=new Vector3(1,1,1);
  Check(Task.Run(()=>!read.TryGetTransform(wrapper,out var p) && p==TransformState.Identity).GetAwaiter().GetResult(),"off-thread read refuses without touching Unity");
  registry.disposed=true;Refuses(read,wrapper,"disposed registry refuses");registry.disposed=false;
  var missing=new ModContext {World=null};
  Refuses(new LifetimeReader(new OwnerEntityService(missing.Lifetime,registry),missing.Lifetime),
   WrapperFactory.Wrap(new NativeRobot(),missing.Lifetime,missing.CaptureNativeEntityReadScope()),"unmanaged world cannot mint a scope");
  var captured=owner.CaptureNativeEntityReadScope();owner.World=new WorldReadiness(saved.Scene,TransformState.Identity);
  var race=WrapperFactory.Wrap(new NativeRobot(),owner.Lifetime,captured);
  Refuses(read,race,"replacement during spawn cannot bless the resulting handle");owner.World=saved;
  ((FakeModLifetime)worldLifetime).Dispose();Refuses(read,wrapper,"world stop invalidates without waiting for entity disposal");
  var stopping=new ModContext();var stoppedRobot=WrapperFactory.Wrap(new NativeRobot(),stopping.Lifetime,stopping.CaptureNativeEntityReadScope());
  var stoppedReader=new LifetimeReader(new OwnerEntityService(stopping.Lifetime,registry),stopping.Lifetime);
  ((FakeModLifetime)stopping.Lifetime).Dispose();Refuses(stoppedReader,stoppedRobot,"owner stop invalidates its reads");
  var normal=new UnityEntityRegistry.UnityEntity{Owner=registry};
  Check(registry.TryGetTransform(normal,out _),"existing registry-owned reads remain supported");
  Check(registry.SetTransform(normal,TransformState.Identity).Succeeded,"existing mutation resolution remains supported");
  normal.Owner=new UnityEntityRegistry();Check(!registry.TryGetTransform(normal,out _),"foreign registry entity remains refused");
  Console.WriteLine("Native pose boundary assertions: "+assertions);
 }
}
""";
    }
}
