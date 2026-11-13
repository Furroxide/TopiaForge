using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotComposedDiscoveryTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            SyntaxTree Read(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file), Encoding.UTF8));
            MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name);

            var syntax = Read("RobotComposedScene.cs").GetRoot();
            // Native deserialization is a boundary fake; discovery and ReferencesResolve are complete production code.
            var method = Method(syntax, "DeserializeProject");
            var tree = syntax.ReplaceNode(method, method.WithBody(SyntaxFactory.ParseStatement(
                "{ return Probe.Project(json); }") as BlockSyntax));

            var candidate = Read("RobotPrefabResolver.cs").GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.Text == "RobotPrefabCandidate");
            var sceneResources = Read("RobotAgentService.SceneResources.cs").GetRoot();
            var cache = Method(sceneResources, "ResolveCachedCatalog").ToFullString()
                + Method(sceneResources, "InvalidateComposedDiscovery").ToFullString();
            var probeSource = Contract.Replace("/* candidate */", candidate.ToFullString())
                .Replace("/* cache */", cache);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(p), "GameCode.dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var sources = new[] { "MiniJson.cs", "RobotComposedSceneShape.cs", "RobotComposedReferences.cs", "RobotComposedCurrentness.cs" }
                .Select(Read).Append(CSharpSyntaxTree.Create((CSharpSyntaxNode)tree)).Append(CSharpSyntaxTree.ParseText(probeSource));
            var compilation = CSharpCompilation.Create("GameCode", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Discovery probe compile failure (not a regression red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "GameCode"
                && a.GetType("TopiaForge.RobotKit.Probe") == null))
                throw new InvalidOperationException("Discovery regression requires a GameCode-free process.");
            var assembly = Assembly.Load(bytes.ToArray());
            var probe = assembly.GetType("TopiaForge.RobotKit.Probe", true)!;
            ResolveEventHandler resolve = (_, args) => new AssemblyName(args.Name).Name == "GameCode" ? assembly : null;
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try { ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))(); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
            Console.WriteLine("Composed discovery production path: distinct catalog dependencies, body-only fallback, fixed refusals and catalog recovery passed.");
        }

        private const string Contract = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using TopiaForge.Mods;
namespace UnityEngine.SceneManagement {
 public struct Scene {
  public int handle; public bool isLoaded => true; public bool IsValid()=>handle!=0;
  public GameObject[] GetRootGameObjects() {
   TopiaForge.RobotKit.Probe.SceneScans++;
   return new[]{ TopiaForge.RobotKit.Probe.Root };
  }
 }
 public static class SceneManager { public static int sceneCount=>1; public static Scene GetSceneAt(int i)=>new Scene {handle=7}; }
}
namespace UnityEngine {
 public class Object {
  public static T Instantiate<T>(T original,Transform parent,bool worldPositionStays) where T:Object => original;
 }
 public static class Time {
  public static double unscaledTimeAsDouble;
  public static float unscaledTime => (float)unscaledTimeAsDouble;
 }
 public class Component : Object { public GameObject gameObject=null!; public Transform transform=>gameObject.transform; }
 public class Transform : Component { public void SetParent(Transform t,bool b){} public bool IsChildOf(Transform t)=>true; }
 public class GameObject : Object {
  public Scene scene; public Transform transform; public bool activeInHierarchy;
  public GameObject(string n="") { transform=new Transform {gameObject=this}; }
  public void SetActive(bool b){} public T AddComponent<T>() where T:new()=>new T();
  public Component[] GetComponentsInChildren(Type t,bool b)=>TopiaForge.RobotKit.Probe.Hosts;
 }
}
namespace TopiaForge.Mods {
 public enum WorldSessionPhase { StartingMode, Running }
 public class Session { public string SessionId="private-session"; }
 public class Map { public bool IsAlive=true; }
 public class Identity { public int InstanceId=7; }
 public class Readiness { public Identity Scene=new(); public Map Map=new(); }
 public class WorldSessionSnapshot { public WorldSessionPhase Phase=WorldSessionPhase.Running; public Session Session=new(); public Readiness World=new(); }
}
public enum ComposerPartType { Head }
public class OvertoneVoiceAsset : UnityEngine.Object { }
public class UgcPrefabSceneMap { public Entry[] Entries {get;set;}=Array.Empty<Entry>(); public class Entry {public string scenePath="MuniciPal/test";public string sceneJson="";} }
public class UgcRobotPartMap {
 public bool TryResolve(string uri,out ComposerPartType part,out GameObject prefab) { part=ComposerPartType.Head; prefab=new GameObject();return uri=="part"; }
}
public class UgcVoiceMap {public bool TryResolve(string uri,out OvertoneVoiceAsset asset){ asset=new(); return uri=="voice";}}
public class UgcRuntimeAssetConfig { public UgcPrefabSceneMap PrefabSceneMap {get;}=new();public UgcRobotPartMap RobotPartMap {get;}=new();public UgcVoiceMap VoiceMap {get;}=new(); }
public class UgcSceneRoot {public Transform ActiveImportRoot {get;set;}=null!;}
public class UgcImportHostSceneController : Component {public UgcRuntimeAssetConfig RuntimeAssetConfig {get;}=new();public UgcSceneRoot SceneRoot {get;}=new();}
public class UgcRuntimeAssetOwner : Component {public void DestroyOwnedAssets(){}}
public class UgcSceneBuilder {
 private const string PlasticMaterialField="PlasticMaterial";private const string MetalMaterialField="MetalMaterial";
 public UgcSceneBuilder(UgcRuntimeAssetConfig config){}
 public GameObject BuildEntity(UgcExportProject project,UgcExportEntity entity,Transform parent){TopiaForge.RobotKit.Probe.Built=entity;return new();}
 public static UgcRuntimeAssetOwner GetOrAddAssetOwner(Transform parent)=>new() {gameObject=parent.gameObject};
}
public class UgcExportProject {public IDictionary scenes=new Hashtable();public IDictionary assets=new Hashtable();public IDictionary localAssets=new Hashtable();}
public class UgcExportScene {public IDictionary entities=new Hashtable();}
public class UgcExportEntity {public UgcExportEntityComponents components=new();}
public class UgcExportEntityComponents {public IDictionary humanoidBodyParts=new Hashtable();public UgcAgentComponent agent=new();}
public class UgcAgentComponent {public string voiceset="voice"; public string personality="";public string[] about=Array.Empty<string>();}
public class WrongScene : UgcExportScene { }
public class WrongEntity : UgcExportEntity { }
public class WrongComponents : UgcExportEntityComponents { }
public class WrongAgent : UgcAgentComponent { }
public static class UgcCatalogLoreLoader {
 // Clean-room boundary for the only three-component shape production admits. It has no
 // poi/aoi/prefab-instance components, so only agent catalog dependencies can be returned.
 // Deliberately no removed CollectReferencedLoreUris member.
 private static IReadOnlyList<string> CollectReferencedAssetUris(UgcExportProject project,UgcPrefabSceneMap map)=>TopiaForge.RobotKit.Probe.Collect(project);
}
namespace TopiaForge.RobotKit {
 internal static class GameReflection {
  internal static object? GetFieldValue(object o,string n)=>o.GetType().GetField(n)?.GetValue(o);
  internal static bool HasComponent(GameObject o,string n)=>true;
 }
 internal sealed class RobotComposedLifetime {internal Func<bool>? Current;internal GameObject? Actor; internal void Initialize(Type t,MethodInfo m){}internal void Dispose(){}}
 /* candidate */
 internal sealed class RobotPrefabResolver {
  internal IReadOnlyList<RobotPrefabCandidate> ResolveAll()=>new[]{new RobotPrefabCandidate("native","Native",new GameObject(),1)};
 }
 internal sealed class RobotAgentService {
  private readonly Func<WorldSessionSnapshot?> world;
  private readonly RobotPrefabResolver prefabResolver=new();
  private IReadOnlyList<RobotPrefabCandidate>? cachedCatalog;
  private object? cachedTypes;
  private float nextPrefabScan, nextComposedScan, composedRetryDelay=2f;
  internal RobotAgentService(Func<WorldSessionSnapshot?> world) { this.world=world; }
  internal IReadOnlyList<RobotPrefabCandidate>? Catalog()=>ResolveCachedCatalog();
  /* cache */
 }
 internal static class RobotKitDiagnostics {
  internal static RobotComposedRefusal? Last;
  internal static RobotComposedRetirement? Retirement;
  internal static void ReportComposedDiscovery(WorldSessionSnapshot? s,RobotComposedRefusal r) { Last=r; }
  internal static void ReportComposedRetirement(WorldSessionSnapshot s,RobotComposedRetirement r) { Retirement=r; }
 }
 public static class Probe {
  public static GameObject Root=new(); public static Component[] Hosts=Array.Empty<Component>();
  public static int SceneScans;
  public static IReadOnlyList<string> Assets=new[]{"part","voice"};
  private static bool OverrideAssets;
  private static string? LastPersonality;
  private static string[] LastCollected=Array.Empty<string>();
  private static readonly List<string[]> Collected=new();
  private static int CollectCalls;
  public static UgcExportEntity? Built;
  private static readonly WorldSessionSnapshot State=new();
  private static int WrongType, InjectCatalog;
  private const string Json="{\"id\":\"test\",\"entities\":{\"actor\":{\"components\":{\"transform\":{},\"agent\":{\"personality\":\"\",\"voiceset\":\"voice\"},\"humanoid-body-parts\":{\"Head\":\"part\",\"PlasticMaterial\":\"symbolic\"}}}}}";
  public static IReadOnlyList<string> Collect(UgcExportProject project) {
   CollectCalls++;if(OverrideAssets)return Assets;
   var references=new List<string>();
   foreach(UgcExportScene scene in project.scenes.Values)
    foreach(UgcExportEntity entity in scene.entities.Values) {
     var agent=entity.components.agent;
     references.AddRange(agent.about.Where(value=>!string.IsNullOrWhiteSpace(value)));
     if(!string.IsNullOrWhiteSpace(agent.personality))references.Add(agent.personality);
    }
   LastCollected=references.ToArray();Collected.Add(LastCollected);return LastCollected;
  }
  public static object Project(string json) {
   // Deserialize real production envelope data into signature stand-ins without invoking GameCode.
   var data=(Dictionary<string,object?>)MiniJson.Deserialize(json)!;
   var scenes=(Dictionary<string,object?>)data["scenes"]!;
   var rawScene=(Dictionary<string,object?>)scenes["test"]!;
   var records=(Dictionary<string,object?>)rawScene["entities"]!;
   var rawEntity=(Dictionary<string,object?>)records["actor"]!;
   var components=(Dictionary<string,object?>)rawEntity["components"]!;
   var parts=(Dictionary<string,object?>)components["humanoid-body-parts"]!;
   var agent=(Dictionary<string,object?>)components["agent"]!;
   var p=new UgcExportProject();
   var scene=WrongType==1 ? new WrongScene() : new UgcExportScene();
   var entity=WrongType==2 ? new WrongEntity() : new UgcExportEntity();
   if(WrongType==3)entity.components=new WrongComponents();
   if(WrongType==4)entity.components.agent=new WrongAgent();
   // Preserve the actual JSON/native alias boundary and simulate unexpected native defaults.
   foreach(var asset in (Dictionary<string,object?>)data["assets"]!)p.assets.Add(asset.Key,asset.Value);
   foreach(var asset in (Dictionary<string,object?>)data["local-assets"]!)p.localAssets.Add(asset.Key,asset.Value);
   if(InjectCatalog==1)p.assets.Add("unexpected","value");
   if(InjectCatalog==2)p.localAssets.Add("unexpected","value");
   foreach(var part in parts)entity.components.humanoidBodyParts.Add(part.Key,part.Value);
   entity.components.agent.personality=(string)agent["personality"]!;
   entity.components.agent.voiceset=(string)agent["voiceset"]!;
   entity.components.agent.about=agent.TryGetValue("about",out var about)
    ? ((List<object?>)about!).Cast<string>().ToArray() : Array.Empty<string>();
   LastPersonality=entity.components.agent.personality;
   scene.entities.Add("actor",entity);p.scenes.Add("test",scene);return p;
  }
  private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
  private static UgcImportHostSceneController Reset() {
   Root=new GameObject {scene=new Scene {handle=7}};
   var host=new UgcImportHostSceneController {gameObject=Root};host.SceneRoot.ActiveImportRoot=Root.transform;
   host.RuntimeAssetConfig.PrefabSceneMap.Entries=new[]{new UgcPrefabSceneMap.Entry {sceneJson=Json}};
   Hosts=new Component[]{host};Assets=new[]{"part","voice"};OverrideAssets=false;
   LastCollected=Array.Empty<string>();Collected.Clear();CollectCalls=0;Built=null;LastPersonality=null;WrongType=InjectCatalog=SceneScans=0;
   RobotKitDiagnostics.Last=null;RobotKitDiagnostics.Retirement=null;return host;
  }
  private static RobotPrefabCandidate? DiscoverWith(string personality,string about="",string part="part",string voice="voice") {
   var host=Reset();
   host.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson=Json.Replace("\"personality\":\"\"","\"personality\":\""+personality+"\""+about)
    .Replace("\"Head\":\"part\"","\"Head\":\""+part+"\"").Replace("\"voiceset\":\"voice\"","\"voiceset\":\""+voice+"\"");
   return RobotComposedScene.Discover(()=>State);
  }
  // The production candidate builds through the production factory, so the built entity is what discovery kept.
  private static bool BuiltBodyOnly(RobotPrefabCandidate? candidate) {
   if(candidate?.BodyOnly!=true)return false;
   candidate.Create(new GameObject().transform);
   return Built!=null && Built.components.agent.personality=="" && Built.components.agent.about.Length==0
    && Built.components.agent.voiceset=="voice" && Built.components.humanoidBodyParts.Count==2;
  }
  public static void Run() {
   Reset();var full=RobotComposedScene.Discover(()=>State);
   Check(full!=null && !full.BodyOnly && LastCollected.Length==0 && RobotKitDiagnostics.Last==null,
    "resolved body/voice with no catalog dependencies discovers without pretending those maps contain lore");
   full!.Create(new GameObject().transform);Check(Built!=null && Built.components.agent.voiceset=="voice","a complete character builds its own entity");
   foreach(var reference in new[]{"personality-reference","part","voice"}) {
    var candidate=DiscoverWith(reference);
    Check(Collected.Count==2 && Collected[0].SequenceEqual(new[]{reference}) && Collected[1].Length==0 && LastPersonality=="",
     "nonblank personality remains an unprepared catalog dependency even when its string equals a resolved part or voice");
    Check(RobotKitDiagnostics.Last==RobotComposedRefusal.CharacterUnprepared && BuiltBodyOnly(candidate),
     "an unprepared personality admits only the body, built without the character, under a fixed reason");
   }
   var aboutOnly=DiscoverWith("",",\"about\":[\"part\",\"about-reference\"]");
   Check(Collected.Count==2 && Collected[0].SequenceEqual(new[]{"part","about-reference"}),"about dependencies are not attested by body-part resolution");
   Check(RobotKitDiagnostics.Last==RobotComposedRefusal.CharacterUnprepared && BuiltBodyOnly(aboutOnly),"unprepared lore admits only the body");
   Check(DiscoverWith("lore",part:"missing")==null && RobotKitDiagnostics.Last==RobotComposedRefusal.BodyPartUnavailable && Built==null,
    "a missing body part refuses even when the character is also unprepared");
   Check(DiscoverWith("lore",voice:"missing")==null && RobotKitDiagnostics.Last==RobotComposedRefusal.VoiceUnavailable && Built==null,
    "a missing voice refuses even when the character is also unprepared");
   var tooMany=",\"about\":["+string.Join(",",Enumerable.Repeat("\"lore\"",257))+"]";
   Check(DiscoverWith("",tooMany)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferenceSetInvalid && Collected.Count==1,
    "an invalid reference set refuses without the body-only fallback");
   foreach(var type in new[]{1,2,3,4}) {
    Reset();WrongType=type;
    Check(RobotComposedScene.Discover(()=>State)==null
     && RobotKitDiagnostics.Last==(type<=2 ? RobotComposedRefusal.EntityShape : RobotComposedRefusal.EntityReferencesShape),
     "lookalike native DTO types refuse before use");
   }
   foreach(var catalog in new[]{1,2}) {
    Reset();InjectCatalog=catalog;
    Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.ProjectCatalogShape,
     "unexpected deserialized native asset catalogue refuses");
   }
   Reset();
   OverrideAssets=true;
   Assets=new[]{"part","voice","unresolved-lore"};Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.CatalogAssetsUnavailable,"unknown fetch-dependent reference refuses with a fixed catalog reason");
   Check(CollectCalls==2,"a reference that survives the character's removal refuses after the body-only recheck");
   Assets=new[]{"part","voice","symbolic"};Check(RobotComposedScene.Discover(()=>State)==null,"material symbol is not automatically a resolved asset URI");
   Assets=Array.Empty<string>();Check(RobotComposedScene.Discover(()=>State)!=null,"empty catalog result remains eligible");
   Assets=null!;Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferenceSetInvalid,"null collection refuses");
   Assets=new[]{""};Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferenceSetInvalid,"empty reference refuses");
   Assets=Enumerable.Repeat("part",257).ToArray();Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferenceSetInvalid,"asset budget refuses");
   var badPart=Reset();badPart.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson=Json.Replace("\"Head\":\"part\"","\"Head\":\"missing\"");
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.BodyPartUnavailable,"unresolved body part refuses");
   var badVoice=Reset();badVoice.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson=Json.Replace("\"voiceset\":\"voice\"","\"voiceset\":\"missing\"");
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.VoiceUnavailable,"unresolved voice refuses");
   var host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries=Enumerable.Repeat(host.RuntimeAssetConfig.PrefabSceneMap.Entries[0],2).ToArray();
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AmbiguousIdentity,"duplicate identity refuses");
   Reset();Hosts=Array.Empty<Component>();Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.HostCount,"host refusal classified");
   host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries=new UgcPrefabSceneMap.Entry[257];Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.EntryBudget,"entry budget classified");
   host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson="{}";Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.SceneShape,"identified malformed scene classified");
   host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries=Array.Empty<UgcPrefabSceneMap.Entry>();
   Check(RobotComposedScene.Discover(()=>State,out var absent)==null && absent,"only a completed no-entry scan admits absence backoff");
   Reset();Hosts=Array.Empty<Component>();
   Check(RobotComposedScene.Discover(()=>State,out absent)==null && !absent,"transient host refusal never admits absence backoff");
   Reset();Check(RobotComposedScene.Discover(()=>State,out absent)!=null && !absent,"successful discovery is not negative cached");
   CatalogRecovery();
  }

  private static void CatalogRecovery() {
   var host=Reset();
   var state=new WorldSessionSnapshot { Phase=WorldSessionPhase.StartingMode };
   var service=new RobotAgentService(()=>state);
   Time.unscaledTimeAsDouble=100;
   var catalog=service.Catalog()!;
   Check(catalog.Count==2 && catalog[0].Id=="native" && catalog[1].Id=="municipal" && SceneScans==1,
    "initial catalog keeps the native default ahead of the composed candidate");
   var original=catalog[1];
   state=new WorldSessionSnapshot { World=state.World,Session=state.Session };
   host.SceneRoot.ActiveImportRoot=null!;
   Check(ReferenceEquals(service.Catalog(),catalog),"incomplete identity preserves the candidate during grace");
   bool refused=false;
   try { _=original.Create(Root.transform); }
   catch(RobotComposedContextUnverifiedException) { refused=true; }
   Check(refused && RobotKitDiagnostics.Last==RobotComposedRefusal.ContextUnverified,
    "construction during grace reports the stable composed context refusal");

   Time.unscaledTimeAsDouble=101.999;
   Check(ReferenceEquals(service.Catalog(),catalog) && SceneScans==1,"grace uses the cached candidate without rediscovery");
   Time.unscaledTimeAsDouble=102;
   RobotKitDiagnostics.Last=null;
   var retired=service.Catalog()!;
   Check(retired.Count==1 && ReferenceEquals(retired[0],catalog[0]) && SceneScans==2,
    "deadline evicts the composed candidate and immediately retries discovery while retaining the native default");
   Check(RobotKitDiagnostics.Retirement==RobotComposedRetirement.VerificationTimedOut
    && RobotKitDiagnostics.Last==RobotComposedRefusal.ContextUnverified,
    "retirement and persistent null rediscovery each report their reason");

   host.SceneRoot.ActiveImportRoot=Root.transform;
   Time.unscaledTimeAsDouble=103.999;
   Check(ReferenceEquals(service.Catalog(),retired) && SceneScans==2,"failed rediscovery honors the existing retry throttle");
   Time.unscaledTimeAsDouble=104;
   var recovered=service.Catalog()!;
   Check(recovered.Count==2 && ReferenceEquals(recovered[0],catalog[0]) && recovered[1].Id=="municipal"
    && !ReferenceEquals(recovered[1],original) && recovered[1].IsCurrent && !original.IsCurrent && SceneScans==3,
    "a later scan rediscovers a fresh verified candidate and cannot revive the retired one");
  }
 }
}
""";
    }
}
