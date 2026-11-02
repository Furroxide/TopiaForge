using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "RobotComposedScene.cs")));
            // Native deserialization is a boundary fake; discovery and ReferencesResolve are complete production code.
            var method = syntax.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "DeserializeProject");
            var tree = syntax.GetRoot().ReplaceNode(method, method.WithBody(SyntaxFactory.ParseStatement(
                "{ return Probe.Project(json); }") as BlockSyntax));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(p), "GameCode.dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var sources = new[] { "MiniJson.cs", "RobotComposedSceneShape.cs", "RobotComposedReferences.cs" }
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file))))
                .Append(CSharpSyntaxTree.Create((CSharpSyntaxNode)tree)).Append(CSharpSyntaxTree.ParseText(Contract));
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
            Console.WriteLine("Composed discovery production path: 2597 all-asset classification and refusals passed.");
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
  public GameObject[] GetRootGameObjects()=>new[]{ TopiaForge.RobotKit.Probe.Root };
 }
 public static class SceneManager { public static int sceneCount=>1; public static Scene GetSceneAt(int i)=>new Scene {handle=7}; }
}
namespace UnityEngine {
 public class Object { }
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
 public GameObject BuildEntity(UgcExportProject project,UgcExportEntity entity,Transform parent)=>new();
 public static UgcRuntimeAssetOwner GetOrAddAssetOwner(Transform parent)=>new();
}
public class UgcExportProject {public IDictionary scenes=new Hashtable();public IDictionary assets=new Hashtable();public IDictionary localAssets=new Hashtable();}
public class UgcExportScene {public IDictionary entities=new Hashtable();}
public class UgcExportEntity {public UgcExportEntityComponents components=new();}
public class UgcExportEntityComponents {public IDictionary humanoidBodyParts=new Hashtable();public UgcAgentComponent agent=new();}
public class UgcAgentComponent {public string voiceset="voice"; public string personality="ordinary prompt text";public string[] about=Array.Empty<string>();}
public class WrongScene : UgcExportScene { }
public class WrongEntity : UgcExportEntity { }
public class WrongComponents : UgcExportEntityComponents { }
public class WrongAgent : UgcAgentComponent { }
public static class UgcCatalogLoreLoader {
 // Signature-only 2597 boundary: deliberately no CollectReferencedLoreUris member.
 private static IReadOnlyList<string> CollectReferencedAssetUris(UgcExportProject project,UgcPrefabSceneMap map)=>TopiaForge.RobotKit.Probe.Assets;
}
namespace TopiaForge.RobotKit {
 internal static class GameReflection {
  internal static object? GetFieldValue(object o,string n)=>o.GetType().GetField(n)?.GetValue(o);
  internal static bool HasComponent(GameObject o,string n)=>true;
 }
 internal sealed class RobotComposedCurrentness {
  internal RobotComposedCurrentness(Func<WorldSessionSnapshot?> w,WorldSessionSnapshot s,Component h,object c,object r,Transform m){}
  internal bool IsCurrent()=>true;internal bool IsVerified=>true;
 }
 internal sealed class RobotComposedLifetime {internal Func<bool>? Current;internal GameObject? Actor; internal void Initialize(Type t,MethodInfo m){}internal void Dispose(){}}
 internal sealed class RobotPrefabCandidate {internal RobotPrefabCandidate(string id,string name,Func<Transform,GameObject> c,Func<bool> current){}}
 internal static class RobotKitDiagnostics {internal static RobotComposedRefusal? Last;internal static void ReportComposedDiscovery(WorldSessionSnapshot? s,RobotComposedRefusal r){Last=r;}}
 public static class Probe {
  public static GameObject Root=new(); public static Component[] Hosts=Array.Empty<Component>();
  public static IReadOnlyList<string> Assets=new[]{"part","voice"};
  private static readonly WorldSessionSnapshot State=new();
  private static int WrongType, InjectCatalog;
  private const string Json="{\"id\":\"test\",\"entities\":{\"actor\":{\"components\":{\"transform\":{},\"agent\":{\"personality\":\"ordinary prompt text\",\"voiceset\":\"voice\"},\"humanoid-body-parts\":{\"Head\":\"part\",\"PlasticMaterial\":\"symbolic\"}}}}}";
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
   Check(entity.components.agent.personality=="ordinary prompt text","plain prompt survives the complete envelope unchanged");
   scene.entities.Add("actor",entity);p.scenes.Add("test",scene);return p;
  }
  private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
  private static UgcImportHostSceneController Reset() {
   Root=new GameObject {scene=new Scene {handle=7}};
   var host=new UgcImportHostSceneController {gameObject=Root};host.SceneRoot.ActiveImportRoot=Root.transform;
   host.RuntimeAssetConfig.PrefabSceneMap.Entries=new[]{new UgcPrefabSceneMap.Entry {sceneJson=Json}};
   Hosts=new Component[]{host};Assets=new[]{"part","voice"};WrongType=InjectCatalog=0;RobotKitDiagnostics.Last=null;return host;
  }
  public static void Run() {
   Reset();Check(RobotComposedScene.Discover(()=>State)!=null,"2597 all-asset result with resolved parts/voice and unmodified prompt must discover");
   foreach(var type in new[]{1,2,3,4}) {
    Reset();WrongType=type;
    Check(RobotComposedScene.Discover(()=>State)==null
     && RobotKitDiagnostics.Last==(type<=2 ? RobotComposedRefusal.EntityShape : RobotComposedRefusal.AssetReferences),
     "lookalike native DTO types refuse before use");
   }
   foreach(var catalog in new[]{1,2}) {
    Reset();InjectCatalog=catalog;
    Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferences,
     "unexpected deserialized native asset catalogue refuses");
   }
   Reset();
   Assets=new[]{"part","voice","unresolved-lore"};Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferences,"unknown fetch-dependent reference refuses");
   Assets=new[]{"part","voice","symbolic"};Check(RobotComposedScene.Discover(()=>State)==null,"material symbol is not automatically a resolved asset URI");
   Assets=Array.Empty<string>();Check(RobotComposedScene.Discover(()=>State)!=null,"empty all-asset result remains eligible");
   Assets=null!;Check(RobotComposedScene.Discover(()=>State)==null,"null collection refuses");
   Assets=Enumerable.Repeat("part",257).ToArray();Check(RobotComposedScene.Discover(()=>State)==null,"asset budget refuses");
   var badPart=Reset();badPart.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson=Json.Replace("\"Head\":\"part\"","\"Head\":\"missing\"");
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferences,"unresolved body part refuses");
   var badVoice=Reset();badVoice.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson=Json.Replace("\"voiceset\":\"voice\"","\"voiceset\":\"missing\"");
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AssetReferences,"unresolved voice refuses");
   var host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries=Enumerable.Repeat(host.RuntimeAssetConfig.PrefabSceneMap.Entries[0],2).ToArray();
   Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.AmbiguousIdentity,"duplicate identity refuses");
   Reset();Hosts=Array.Empty<Component>();Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.HostCount,"host refusal classified");
   host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries=new UgcPrefabSceneMap.Entry[257];Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.EntryBudget,"entry budget classified");
   host=Reset();host.RuntimeAssetConfig.PrefabSceneMap.Entries[0].sceneJson="{}";Check(RobotComposedScene.Discover(()=>State)==null && RobotKitDiagnostics.Last==RobotComposedRefusal.SceneShape,"identified malformed scene classified");
  }
 }
}
""";
    }
}
