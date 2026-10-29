using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotSpeechNativeLifetimeTests
    {
        internal static void Run()
        {
            // Execute the shipped registration, cleanup, Despawn and service Tick, with only the native boundary
            // replaced. A destroyed head deliberately compares null while its managed reference remains alive.
            var root = Path.Combine(Program.FindRepoRoot(), "mods/TopiaForge.RobotKit");
            CompilationUnitSyntax Read(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file))).GetCompilationUnitRoot();
            string Methods(CompilationUnitSyntax syntax, params string[] names) => string.Join("\n", names.Select(name =>
                syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name).ToFullString()));
            var speech = Read("RobotAgent.Speech.cs");
            var agent = Read("RobotAgent.cs");
            var native = Read("NativeRobotSpeech.cs");
            var source = Contract.Replace("/* speech fields */", string.Join("\n", speech.DescendantNodes().OfType<FieldDeclarationSyntax>()))
                .Replace("/* agent methods */", Methods(speech, "RegisterSpeech", "ReleaseSpeech", "BindSpeech") + Methods(agent, "Despawn"))
                .Replace("/* alive */", agent.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.Text == "IsAlive").ToFullString())
                .Replace("/* registry */", native.DescendantNodes().OfType<FieldDeclarationSyntax>()
                    .Single(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "agents")).ToFullString() + Methods(native, "Register", "Release"))
                .Replace("/* tick */", Methods(Read("RobotAgentService.cs"), "Tick"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("RobotSpeechLifetimeProbe", new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.RobotAgentService", true)!;
            ((Action)probe.GetMethod("Scenarios")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Robot speech native lifetime: 4 production-path scenarios passed.");
        }

        private const string Contract = """
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
namespace UnityEngine {
 public class Object {
  public bool Destroyed; public static int DestroyCalls;
  public static bool operator ==(Object? a,Object? b) => ReferenceEquals(a,b)
   || ReferenceEquals(a,null) && b!.Destroyed || ReferenceEquals(b,null) && a!.Destroyed;
  public static bool operator !=(Object? a,Object? b) => !(a==b);
  public override bool Equals(object? other) => ReferenceEquals(this,other);
  public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
  public static void Destroy(Object o) { DestroyCalls++; o.Destroyed=true; }
 }
 public class GameObject : Object {
  public AgentHead Head=new(); public bool Killed;
  public T GetComponentInChildren<T>(bool inactive) where T:class => (Head as T)!;
 }
}
public class AgentHead : UnityEngine.Object { }
namespace TopiaForge.Mods {
 public class OperationResult<T> { public static OperationResult<T> Success(T value) => new(); }
}
namespace TopiaForge.Mods.Internal { internal interface IInternalNativeSpeechPlayback : IDisposable { } }
namespace TopiaForge.RobotKit {
 internal static class NativeRobotSpeech {
  /* registry */
  internal static bool Contains(AgentHead head) => agents.ContainsKey(head);
  internal static int Count => agents.Count;
 }
 internal static class GameReflection { internal static bool HasKilledComponent(GameObject go) => go.Killed; }
 internal static class RobotKitLog { internal static string ExceptionType(Exception ex) => ex.GetType().Name; }
 internal sealed class Logger { internal void Debug(string text) { } }
 internal sealed class Replication { internal int Forgets; internal void Forget(string id) { Forgets++; } }
 internal sealed class Search { internal bool IsComplete; internal void Step() { } internal void Cancel() { } }
 internal sealed class RobotAgent {
  internal readonly GameObject go=new(); internal string Id=Guid.NewGuid().ToString(); private bool despawned;
  internal int Steps;
  /* speech fields */
  /* alive */
  /* agent methods */
  internal void Step() { Steps++; }
  private void ReleaseWalk() { }
  private void ResetInteractionOverrides() { }
  internal CancellationToken SpeechToken => speechLifetime.Token;
  internal bool SpeechDetached => speechRoute is null && ReferenceEquals(speechHead,null);
 }
 internal sealed class RobotAgentService {
  private bool disposed,activeDirty; private readonly List<RobotAgent> agents=new();
  private readonly Dictionary<string,RobotAgent> agentsById=new(); private readonly List<Search> searches=new();
  private readonly Logger logger=new(); private readonly Replication Replication=new();
  /* tick */
  private void Add(RobotAgent a) { agents.Add(a); agentsById.Add(a.Id,a); }
  private static void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
  public static void Scenarios() {
   // Both native death with a retained corpse and outright destruction must release speech during prune.
   foreach(var destroyBody in new[]{false,true}) {
    var service=new RobotAgentService(); var dead=new RobotAgent(); var live=new RobotAgent();
    dead.RegisterSpeech(); live.RegisterSpeech(); var head=dead.go.Head; var token=dead.SpeechToken;
    int cancellations=0; using var callback=token.Register(()=>cancellations++);
    dead.BindSpeech((_,_)=>Task.FromResult(new OperationResult<bool>()));
    service.Add(dead); service.Add(live); head.Destroyed=true; dead.go.Killed=true; dead.go.Destroyed=destroyBody;
    Check(head==null && !ReferenceEquals(head,null) && NativeRobotSpeech.Contains(head!),"destroyed head must still be registered before prune");
    UnityEngine.Object.DestroyCalls=0; service.Tick(0);
    Check(!NativeRobotSpeech.Contains(head!) && NativeRobotSpeech.Count==1 && dead.SpeechDetached,"prune releases the real static registry and route");
    Check(token.IsCancellationRequested && cancellations==1,"prune cancels native speech once");
    Check(service.agents.Count==1 && service.agentsById.Count==1 && service.Replication.Forgets==1 && service.activeDirty,"prune retains service bookkeeping");
    Check(UnityEngine.Object.DestroyCalls==0 && dead.go.Destroyed==destroyBody && live.Steps==1,"prune preserves native corpse and live agents");
    service.Tick(0); dead.ReleaseSpeech(); dead.ReleaseSpeech(); dead.Despawn(); dead.Despawn();
    Check(cancellations==1 && service.Replication.Forgets==1 && dead.SpeechDetached,"repeated cleanup and later scope despawn are idempotent");
    live.Despawn(); Check(NativeRobotSpeech.Count==0,"surviving registrations clean up independently");
   }
   // Explicit despawn can also encounter a destroyed head before the service has pruned it.
   { var a=new RobotAgent(); a.RegisterSpeech(); var head=a.go.Head; var token=a.SpeechToken; head.Destroyed=true;
     a.Despawn(); a.ReleaseSpeech(); a.Despawn();
     Check(!NativeRobotSpeech.Contains(head!) && token.IsCancellationRequested && a.SpeechDetached,"explicit despawn removes a managed dead head"); }
   { var a=new RobotAgent(); var token=a.SpeechToken; a.ReleaseSpeech(); a.ReleaseSpeech(); a.Despawn();
     Check(token.IsCancellationRequested && NativeRobotSpeech.Count==0 && a.SpeechDetached,"missing head cleanup is idempotent too"); }
  }
 }
}
""";
    }

    internal sealed partial class FirstPartyRobot : IRobotNativeSpeech
    {
        internal int SpeechPlays;
        internal bool Visible = true, MissingVoice;
        internal string Voice = new string('a', 64);
        internal readonly List<CancellationToken> SpeechTokens = new List<CancellationToken>();
        internal Func<string, CancellationToken, Task<OperationResult<bool>>>? Route;
        internal object? PlaybackAssociation;
        internal Func<bool>? PlaybackCurrent;
        internal CancellationToken PlaybackStopping;
        public void BindSpeechPlayback(object association, Func<bool> isCurrent, CancellationToken ownerStopping = default)
        { PlaybackAssociation = association; PlaybackCurrent = isCurrent; PlaybackStopping = ownerStopping; }
        public bool SpeechVisible => IsAlive && Visible;
        public OperationResult<string> VoiceIdentity() => MissingVoice
            ? OperationResult<string>.Failure(ModErrorCode.Unavailable, "No voice binding.") : OperationResult<string>.Success(Voice);
        public Task<OperationResult<bool>> PlaySpeechAsync(string voice, string text, CancellationToken token)
        {
            if (MissingVoice || voice != Voice) return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "No matching voice."));
            SpeechPlays++; SpeechTokens.Add(token);
            var completion = new TaskCompletionSource<OperationResult<bool>>();
            token.Register(() => completion.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Stopped.")));
            return completion.Task;
        }
        public void BindSpeech(Func<string, CancellationToken, Task<OperationResult<bool>>> route) => Route = route;
    }

    internal sealed class RobotSpeechRig : IDisposable
    {
        internal readonly WorldRig World = new WorldRig();
        internal readonly SpeechPeer Host;
        internal readonly List<SpeechPeer> Guests = new List<SpeechPeer>();
        internal readonly AuthorityMod Owner;
        internal readonly IRobotAgent Robot;
        internal FirstPartyRobot Canonical => Host.Robots.Agents.Values.Single();
        internal RobotSpeechRig(int players = 1)
        {
            World.UseScene(scene => scene.AddScene("Yard"));
            Host = new SpeechPeer(World, World.Host);
            World.OpenToMultiplayer();
            World.Host.World.Proxies.IsSharedMod = _ => true;
            Owner = new AuthorityMod(World.Host, ModExecutionContext.GamemodeServer);
            Robot = Host.Robots.Provider.Spawn(Owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero, brainMode: RobotBrainMode.Autonomous)).Value!;
            for (var i = 0; i < players; i++) Add("listener" + i);
            World.Frames(30);
        }
        internal SpeechPeer Add(string id)
        {
            var peer = new SpeechPeer(World, World.AddPlayer(id)); Guests.Add(peer);
            World.JoinAndSynchronize(peer.Peer); World.Frames(30); return peer;
        }
        internal void Move(SpeechPeer peer, float x)
        {
            var position = new NetVector3(x, 0, 0);
            peer.Backend.Feet = position;
            if (!peer.Peer.Control.IsHost && Host.Presence.TryGetPeer(peer.Peer.Control.Id, out var known))
                known!.Plausibility!.ExpectPlacement(NetQuantization.QuantizePosition(position), World.Clock.Now);
            World.Frames(10);
        }
        internal void Later() { World.Clock.Advance(1_100_000); World.Frames(5); }
        internal List<(ParticipantId To, byte[] Payload)> Log => Host.Channel.Log!;
        internal byte[] LastLine => Log.Last(p => p.Payload[1] == RobotSpeechWire.Line).Payload;
        public void Dispose()
        {
            Owner.Lifetime.Dispose();
            World.Host.World.Speech.Reset();
            foreach (var guest in Guests) guest.Peer.World.Speech.Reset();
        }
        internal sealed class SpeechPeer
        {
            internal readonly WorldPeer Peer;
            internal readonly FakePresenceBackend Backend = new FakePresenceBackend();
            internal readonly MultiplayerPresence Presence;
            internal readonly FirstPartyRobots Robots;
            internal string? CurrentWorld = "world-one";
            internal int Scene;
            internal FirstPartyRobot Body => Robots.Agents.Values.Last();
            internal LoopbackChannel Channel => Peer.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            internal SpeechPeer(WorldRig rig, WorldPeer peer)
            {
                Peer = peer; peer.Runtime.Disconnect();
                Presence = MultiplayerPresence.Install(peer.Runtime, peer.Replication, Backend, rig.Clock.Read);
                Presence.RunningWorld = () => CurrentWorld;
                Presence.SceneVersion = () => Scene;
                Presence.RunningSpawn = () => TransformState.Identity;
                peer.Replication.Players = MultiplayerPlayers.Install(peer.Runtime, Presence);
                peer.Runtime.Connect(); Robots = new FirstPartyRobots(peer);
                Channel.Log = new List<(ParticipantId, byte[])>();
            }
        }
    }
}
