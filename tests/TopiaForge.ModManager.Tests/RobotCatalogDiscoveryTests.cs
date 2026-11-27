using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotCatalogDiscoveryTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit");
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "RobotAgentService.SceneResources.cs"))).GetRoot();
            var names = new[] { "ResolveCachedCatalog", "InvalidateComposedDiscovery" };
            var methods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => names.Contains(m.Identifier.Text)).Select(m => m.ToFullString());
            var fields = syntax.DescendantNodes().OfType<FieldDeclarationSyntax>().Select(f => f.ToFullString());
            var service = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "RobotAgentService.cs"))).GetRoot();
            var sceneReset = service.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "OnSceneChanged").ToFullString();
            var source = Contract.Replace("// PRODUCTION", string.Join("\n", fields.Concat(methods).Append(sceneReset)));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(path).StartsWith("TopiaForge", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("RobotCatalogProbe", new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Catalogue probe compile failure (not a regression red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.RobotAgentService", true)!;
            ((Action)probe.GetMethod("Scenarios")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Robot catalogue discovery production paths passed.");
        }

        private const string Contract = """
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine { public static class Time { public static float unscaledTime; } }
namespace TopiaForge.RobotKit {
 internal sealed class RobotPrefabCandidate {
  internal readonly string Id; internal readonly object? Prefab; private readonly int epoch;
  internal RobotPrefabCandidate(string id,bool prefab=false){Id=id;Prefab=prefab?new object():null;epoch=RobotComposedScene.Epoch;}
  internal bool IsCurrent=>Prefab!=null || epoch==RobotComposedScene.Epoch;
 }
 internal sealed class Resolver {
  internal int Calls;internal IReadOnlyList<RobotPrefabCandidate> Available=Array.Empty<RobotPrefabCandidate>();
  internal IReadOnlyList<RobotPrefabCandidate> ResolveAll(){Calls++;return Available;}
 }
 internal static class RobotComposedScene {
  internal static int Calls,Epoch;internal static bool Present,Absent;
  internal static RobotPrefabCandidate? Discover(Func<object?>? world,out bool notPresent){Calls++;notPresent=Absent;return Present?new RobotPrefabCandidate("municipal"):null;}
 }
 internal sealed class Replica {internal void Clear(){}}
 internal static class LocomotionBridge {internal static void ResetSceneCache(){}}
 public sealed class RobotAgentService {
  private IReadOnlyList<RobotPrefabCandidate>? cachedCatalog;private object? cachedTypes,cachedPathFindSettings,playerController,playerHealth,playerEntity;
  private float nextPrefabScan;private readonly Resolver prefabResolver=new();private readonly Func<object?> world=()=>null;
  private Replica? Replication;private void ClearAgents(){}private void CancelSearches(){}
  // PRODUCTION
  private static RobotAgentService Reset(bool present,bool absent){Time.unscaledTime=0;RobotComposedScene.Calls=0;RobotComposedScene.Epoch++;RobotComposedScene.Present=present;RobotComposedScene.Absent=absent;return new();}
  private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
  public static void Scenarios(){
   var s=Reset(true,false);s.ResolveCachedCatalog();
   for(var i=1;i<200;i++){Time.unscaledTime=i/100f;s.ResolveCachedCatalog();}
   Check(s.prefabResolver.Calls==1 && RobotComposedScene.Calls==1,"catalogue polling never full-scans every frame");
   Time.unscaledTime=2;s.ResolveCachedCatalog();Time.unscaledTime=4;s.ResolveCachedCatalog();
   Check(s.prefabResolver.Calls==3 && RobotComposedScene.Calls==1,"current composition retains two-second empty-prefab retries without recomposition");
   s.prefabResolver.Available=new[]{new RobotPrefabCandidate("robot01",true)};Time.unscaledTime=6;
   var found=s.ResolveCachedCatalog();Check(found!.Count==2 && found[0].Id=="robot01" && found[1].Id=="municipal","late prefabs recover beside current composition with prefab default ordering");
   Time.unscaledTime=100;s.ResolveCachedCatalog();Check(s.prefabResolver.Calls==4,"a populated current catalogue avoids redundant prefab scans");
   foreach(var id in new[]{"municipal","MuNiCiPaL"}){
    s=Reset(true,false);s.ResolveCachedCatalog();s.cachedTypes=new object();
    var native=new RobotPrefabCandidate(id,true);s.prefabResolver.Available=new[]{native};Time.unscaledTime=2;
    var unique=s.ResolveCachedCatalog();
    Check(unique!.Count==1 && ReferenceEquals(unique[0],native) && s.cachedTypes==null,"late same-ID native prefab replaces composition with one case-insensitive catalogue entry");
    Check(ReferenceEquals(s.ResolveCachedCatalog()![0],native),"same-count replacement persists in the cached catalogue");
   }
   s=Reset(false,true);s.ResolveCachedCatalog();Time.unscaledTime=2;s.ResolveCachedCatalog();Time.unscaledTime=4;s.ResolveCachedCatalog();
   Check(RobotComposedScene.Calls==2,"definite absent entry backs off beyond ordinary two-second polling");
   foreach(var time in new[]{6,14,30,60}){Time.unscaledTime=time;s.ResolveCachedCatalog();}
   Check(RobotComposedScene.Calls==6,"absent discovery remains bounded and retryable at the thirty-second cap");
   RobotComposedScene.Present=true;RobotComposedScene.Absent=false;Time.unscaledTime=90;
   Check(s.ResolveCachedCatalog()!.Count==1,"a late entry recovers without any world notification");
   s=Reset(false,true);s.ResolveCachedCatalog();Time.unscaledTime=2;s.ResolveCachedCatalog();
   RobotComposedScene.Epoch++;RobotComposedScene.Present=true;RobotComposedScene.Absent=false;s.InvalidateComposedDiscovery();
   Check(s.ResolveCachedCatalog()!.Count==1 && RobotComposedScene.Calls==3,"world or session publication resets absent-entry backoff immediately");
   RobotComposedScene.Epoch++;s.InvalidateComposedDiscovery();var replacement=s.ResolveCachedCatalog();
   Check(replacement!.Count==1 && replacement[0].IsCurrent,"world replacement cannot retain a retired composed candidate");
   s=Reset(false,true);s.ResolveCachedCatalog();Time.unscaledTime=2;s.ResolveCachedCatalog();s.OnSceneChanged();
   RobotComposedScene.Epoch++;RobotComposedScene.Present=true;RobotComposedScene.Absent=false;
   Check(s.ResolveCachedCatalog()!.Count==1 && RobotComposedScene.Calls==3,"actual scene reset clears negative discovery schedule");
   s=Reset(false,false);s.ResolveCachedCatalog();Time.unscaledTime=2;s.ResolveCachedCatalog();
   Check(RobotComposedScene.Calls==2,"transient refusal retains ordinary two-second retries");
  }
 }
}
""";
    }
}
