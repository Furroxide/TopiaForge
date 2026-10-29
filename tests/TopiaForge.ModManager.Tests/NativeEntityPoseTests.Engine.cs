namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeEntityPoseTests
    {
        // Only the engine and orchestration inputs are doubles. The read resolver, world scope,
        // lifetime guard, mutation resolvers and RobotKit ownership wrapper are shipped source.
        private const string Engine = """
using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace UnityEngine {
 public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; } }
 public struct Quaternion { public float x,y,z,w; public Quaternion(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; } }
 public sealed class Transform {
  public Vector3 position=new(2,3,4),localScale=new(2,3,4); public Quaternion rotation=new(0,0.6f,0,0.8f);
  public void SetPositionAndRotation(Vector3 p,Quaternion q) { position=p;rotation=q; }
 }
 public sealed class GameObject {
  public bool Destroyed, Throw; private Transform value=new();
  public Transform transform => Throw ? throw new MissingReferenceException() : value;
  public static bool operator ==(GameObject? a,GameObject? b) => ReferenceEquals(a,b) || (b is null && a?.Destroyed==true);
  public static bool operator !=(GameObject? a,GameObject? b) => !(a==b);
  public override bool Equals(object? o) => ReferenceEquals(this,o); public override int GetHashCode() => base.GetHashCode();
 }
 public sealed class Rigidbody { }
 public sealed class MissingReferenceException : Exception { }
}
namespace UnityEngine.SceneManagement {
 public struct Scene { public int handle; public string name; public bool isLoaded; public bool IsValid() => handle!=0; }
 public static class SceneManager {
  public static Scene World=new(){handle=7,name="World",isLoaded=true};
  public static int sceneCount=>1; public static Scene GetSceneAt(int index)=>World;
 }
}
namespace TopiaForge.ModManager {
 internal interface IUnityOwnedEntity : IEntity,IDisposable { IEntity InnerEntity {get;} }
 internal static class UnityPhysicsBackend {
  internal static Vec3 FromUnity(Vector3 v)=>new(v.x,v.y,v.z);
  internal static Vector3 ToUnity(Vec3 v)=>new(v.X,v.Y,v.Z);
 }
 internal class UnityEntityRegistry {
  internal bool disposed; private object sync=new(); private Dictionary<int,UnityEntityMotion> motions=new();
  private void ThrowIfDisposed() { if(disposed) throw new ObjectDisposedException("registry"); }
  public IReadOnlyList<IEntity> Query(EntityQuery q)=>Array.Empty<IEntity>();
  internal sealed class UnityEntity : IEntity {
   public UnityEntityRegistry Owner=null!; public GameObject GameObject=new(); public Rigidbody? Body; public int InstanceId;
   public bool IsAlive=>GameObject!=null; public string Id=>"same-id"; public string Name=>"Entity"; public Vec3 Position=>Vec3.Zero;
  }
  internal sealed class UnityEntityMotion : IEntityMotion {
   public UnityEntityMotion(UnityEntityRegistry r,UnityEntity e,Rigidbody b) { Entity=e; }
   public IEntity Entity {get;} public bool IsAlive=>true; public void Dispose() { }
   public OperationResult<Vec3> MoveToward(Vec3 v,float a,float b,float c,float d)=>OperationResult<Vec3>.Success(v);
   public OperationResult<Vec3> Throw(Vec3 v,float f)=>OperationResult<Vec3>.Success(v);
  }
  /* registry */
 }
 /* owner service */
 internal sealed class ModContext {
  public IModLifetime Lifetime=new FakeModLifetime(); public IModLifetime WorldLifetime=new FakeModLifetime();
  public WorldReadiness? World=new(new WorldSceneIdentity(7,"World"),TransformState.Identity);
  public bool TryGetViewWorld(out WorldReadiness? world,out IModLifetime? lifetime) { world=World;lifetime=WorldLifetime;return world!=null; }
  /* context scope */
 }
 internal sealed class LifetimeReader {
  private IEntityService inner; private IModLifetime lifetime;
  internal LifetimeReader(IEntityService inner,IModLifetime lifetime) { this.inner=inner;this.lifetime=lifetime; }
  /* lifetime read */
 }
}
namespace TopiaForge.RobotKit {
 internal static class WrapperFactory {
  internal static IRobotAgent Wrap(IRobotAgent agent,IModLifetime lifetime,IInternalNativeEntityReadScope? scope)
   =>new OwnerRobotAgent(agent,lifetime.Track(agent),lifetime,scope);
  /* robot wrapper */
 }
 internal sealed class NativeRobot : IRobotAgent,IInternalNativeObjectEntity {
  internal object? Native=new GameObject(); internal bool Alive=true,Throw; internal int Reads;
  public object? NativeObject {get { Reads++; if(Throw) throw new InvalidOperationException("native detail");return Native; } }
  public string Id=>"same-id"; public string Name=>"Robot"; public bool IsAlive=>Alive;
  public Vec3 Position=>new(99,99,99); public Vec3 HeadPosition=>Position;
  public RobotBrainMode BrainMode=>RobotBrainMode.Dormant; public bool IsMoving=>false; public bool HasReachedTarget=>false;
  public float MoveSpeed=>1; public float TurnSpeed=>1; public float StopDistance=>1; public RobotGait Gait=>RobotGait.Walk;
  private static OperationResult<bool> Ok()=>OperationResult<bool>.Success(true);
  public OperationResult<bool> SetBrainMode(RobotBrainMode x)=>Ok(); public OperationResult<bool> ConfigureMovement(RobotMovementSettings x)=>Ok();
  public OperationResult<bool> MoveTo(Vec3 x)=>Ok(); public OperationResult<bool> Chase(IEntity x)=>Ok(); public OperationResult<bool> Stop()=>Ok();
  public OperationResult<bool> SetTint(RobotColor x)=>Ok(); public OperationResult<bool> SetEmote(string x)=>Ok(); public OperationResult<bool> SetName(string x)=>Ok();
  public OperationResult<bool> SetScale(float x)=>Ok(); public OperationResult<bool> SetInteraction(RobotInteractionOptions x)=>Ok();
  public OperationResult<bool> ApplyDamage(float a,RobotDamageType b,string c)=>Ok(); public OperationResult<bool> Kill(RobotDamageType a,string b)=>Ok();
  public OperationResult<bool> Ragdoll()=>Ok(); public OperationResult<bool> Knockback(Vec3 x)=>Ok();
  public OperationResult<bool> Despawn() {Alive=false;return Ok();} public void Dispose() {Alive=false;}
 }
}
""";
    }
}
