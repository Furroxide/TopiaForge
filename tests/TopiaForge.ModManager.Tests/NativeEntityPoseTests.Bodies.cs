namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeEntityPoseTests
    {
        private const string BodyScenarios = """
using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.ModManager;
using TopiaForge.RobotKit;
using UnityEngine;
using UnityEngine.SceneManagement;
internal static class BodyPoseScenarios {
 private static int assertions;
 private static void Check(bool value,string why) { assertions++; if(!value) throw new InvalidOperationException("Body pose: "+why); }
 private static void Refuses(IEntityService reader,IEntity body,string why) {
  Check(!reader.TryGetTransform(body,out var pose) && pose==TransformState.Identity,why);
 }
 public static void Run(Func<IModLifetime,IRuntimeReplication> seam) {
  UnityMainThreadGuard.CaptureCurrentThread();
  var owner=new ModContext();var registry=new UnityEntityRegistry();
  var root=new GameObject();
  var request=new RobotBodyRequest(new AvatarAppearance(new RgbaColor(1,0,0),new RgbaColor(0,1,0)),new Vec3(99,99,99));
  using var provider=new RobotBodyService(r=>OperationResult<IRobotBody>.Success(new NativeBody(root,r)),owner);
  var service=provider.Bind(owner.Lifetime,seam(owner.Lifetime),owner);
  var made=service.Spawn(request);Check(made.Succeeded,"actual scoped service returns a lease");var body=made.Value!;
  IEntityService read=new OwnerEntityService(owner.Lifetime,registry);
  Check(read.TryGetTransform(body,out var pose),"returned body supplies complete transform through public entity service");
  Check(pose.Position==new Vec3(2,3,4) && pose.Rotation==new Quat(0,0.6f,0,0.8f) && pose.Scale==new Vec3(2,3,4),"position rotation and scale sample native root");
  Check(!read.SetTransform(body,TransformState.Identity).Succeeded && !read.AcquireMotion(body).Succeeded && !read.Destroy(body).Succeeded,"read bridge grants no placement or destruction authority");
  var other=new ModContext {World=owner.World,WorldLifetime=owner.WorldLifetime};
  Refuses(new OwnerEntityService(other.Lifetime,registry),body,"foreign context in same world cannot read");
  var scope=((IInternalOwnedNativeEntity)body).NativeReadScope!;
  Check(scope.IsCurrentWorld(other),"view scopes may share the same actual world");
  Check(!scope.IsCurrentWorld(new ForeignReadContext()),"a read-context adapter cannot attest runtime world identity");
  var saved=owner.World;owner.World=new WorldReadiness(saved!.Scene,TransformState.Identity);
  Refuses(read,body,"same scene replacement refuses reads");Check(!scope.IsCurrentWorld(other),"old view scope refuses replacement before callbacks");owner.World=saved;
  other.World=new WorldReadiness(saved.Scene,TransformState.Identity);
  Check(!scope.IsCurrentWorld(other),"foreign readiness with matching scene refuses views");
  SceneManager.World.isLoaded=false;Refuses(read,body,"native unload refuses before callbacks");SceneManager.World.isLoaded=true;
  body.Dispose();Refuses(read,body,"explicit retirement refuses reads");
  var second=service.Spawn(request).Value!;
  Check(read.TryGetTransform(second,out _),"new lease recovers read access");
  provider.EndWorld();Refuses(read,second,"provider world retirement refuses old lease");
  var world=new ModContext();
  using var replacement=new RobotBodyService(r=>OperationResult<IRobotBody>.Success(new NativeBody(new GameObject(),r)),world);
  var next=replacement.Bind(world.Lifetime,seam(world.Lifetime),world).Spawn(request).Value!;
  IEntityService nextRead=new OwnerEntityService(world.Lifetime,registry);
  Check(nextRead.TryGetTransform(next,out _),"replacement provider has fresh authority");
  var replica=replacement.CreateReplica(RobotBodyWire.Describe(request)).Value!;
  Check(nextRead.TryGetTransform(replica,out _),"provider replica captures its own world and context authority");
  replacement.ReleaseReplica(replica);Refuses(nextRead,replica,"released replica cannot read surviving native bytes");
  var recovered=replacement.CreateReplica(RobotBodyWire.Describe(request)).Value!;
  Check(nextRead.TryGetTransform(recovered,out _),"recovered provider instance has fresh read authority");
  ((FakeModLifetime)world.WorldLifetime).Dispose();Refuses(nextRead,next,"world lifetime retirement refuses surviving native root");
  Refuses(nextRead,recovered,"world retirement invalidates recovered replica too");
  var race=new ModContext();
  using var racing=new RobotBodyService(r=> { race.World=new WorldReadiness(race.World!.Scene,TransformState.Identity);return OperationResult<IRobotBody>.Success(new NativeBody(new GameObject(),r)); });
  var raced=racing.Bind(race.Lifetime,seam(race.Lifetime),race).Spawn(request).Value!;
  Refuses(new OwnerEntityService(race.Lifetime,registry),raced,"read scope was captured before construction changed the world");
  Console.WriteLine("Persistent body native pose assertions: "+assertions);
 }
 private sealed class ForeignReadContext : IInternalNativeEntityReadContext {
  public IInternalNativeEntityReadScope? CaptureNativeEntityReadScope() => throw new InvalidOperationException("must not mint authority");
 }
 private sealed class NativeBody : IRobotBody,IInternalNativeObjectEntity {
  private readonly GameObject root;private readonly RobotBodyRequest request;private bool ended;
  internal NativeBody(GameObject root,RobotBodyRequest request) {this.root=root;this.request=request;}
  public string Id=>"body-pose";public string Name=>"Body";public bool IsAlive=>!ended;
  public Vec3 Position=>request.Position;public Quat Rotation=>request.Rotation;public AvatarAppearance Appearance=>request.Appearance;
  public object NativeObject=>root;public void Dispose() {ended=true;}
 }
}
""";
    }
}
