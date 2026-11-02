using System;
using System.IO;
using System.Linq;
using TopiaForge.GameCompat;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotComposedBindingTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var manifest = BindingManifest.Parse(File.ReadAllText(Path.Combine(root, "bindings",
                "io.github.furroxide.topiaforge.robotkit.gamebindings.json")));
            var baseline = SurfaceSnapshot.Parse(File.ReadAllText(Path.Combine(root, "baselines", "gamecode.surface.baseline.json")));
            var expected = new[] {
                "robotkit.composed.type.ugcscenebuilder",
                "robotkit.composed.type.ugcruntimeassetowner",
                "robotkit.composed.type.ugcimporthostscenecontroller",
                "robotkit.composed.type.ugcruntimeassetconfig",
                "robotkit.composed.type.ugcsceneroot",
                "robotkit.composed.type.ugcprefabscenemap",
                "robotkit.composed.type.ugcprefabscenemap.entry",
                "robotkit.composed.type.ugcrobotpartmap",
                "robotkit.composed.type.ugcvoicemap",
                "robotkit.composed.type.ugccatalogloreloader",
                "robotkit.composed.type.ugcexportproject",
                "robotkit.composed.type.ugcexportscene",
                "robotkit.composed.type.ugcexportentity",
                "robotkit.composed.type.ugcexportentitycomponents",
                "robotkit.composed.type.ugcagentcomponent",
                "robotkit.composed.type.composerparttype",
                "robotkit.composed.type.overtonevoiceasset",
                "robotkit.composed.ugcscenebuilder.plasticmaterialfield",
                "robotkit.composed.ugcscenebuilder.metalmaterialfield",
                "robotkit.composed.ugcprefabscenemap.entry.scenejson",
                "robotkit.composed.ugcprefabscenemap.entry.scenepath",
                "robotkit.composed.ugcexportproject.assets",
                "robotkit.composed.ugcexportproject.localassets",
                "robotkit.composed.ugcexportproject.scenes",
                "robotkit.composed.ugcexportscene.entities",
                "robotkit.composed.ugcexportentity.components",
                "robotkit.composed.ugcexportentitycomponents.humanoidbodyparts",
                "robotkit.composed.ugcexportentitycomponents.agent",
                "robotkit.composed.ugcagentcomponent.voiceset",
                "robotkit.composed.ugcimporthostscenecontroller.runtimeassetconfig",
                "robotkit.composed.ugcimporthostscenecontroller.sceneroot",
                "robotkit.composed.ugcsceneroot.activeimportroot",
                "robotkit.composed.ugcruntimeassetconfig.prefabscenemap",
                "robotkit.composed.ugcruntimeassetconfig.robotpartmap",
                "robotkit.composed.ugcruntimeassetconfig.voicemap",
                "robotkit.composed.ugcprefabscenemap.entries",
                "robotkit.composed.ugcscenebuilder.buildentity",
                "robotkit.composed.ugcscenebuilder.getoraddassetowner",
                "robotkit.composed.ugcruntimeassetowner.destroyownedassets",
                "robotkit.composed.ugcrobotpartmap.tryresolve",
                "robotkit.composed.ugcvoicemap.tryresolve",
                "robotkit.composed.ugccatalogloreloader.collectreferencedasseturis",
                "robotkit.composed.ugcscenebuilder.ctor"
            };
            foreach (var id in expected)
            {
                var binding = manifest.Bindings.SingleOrDefault(b => b.Id == id);
                Check(binding != null && binding.Criticality == Criticality.Critical, "missing Critical composition binding: " + id);
                var clone = SurfaceSnapshot.Parse(baseline.ToCanonicalJson());
                var type = clone.FindType(binding!.TypeKey)!;
                Check(type != null && type.Status == SurfaceStatus.Resolved, "missing resolved composition baseline: " + id);
                switch (binding.Kind)
                {
                    case BindingKind.Type: clone.Types.Remove(binding.TypeKey); break;
                    case BindingKind.Method:
                        foreach (var member in type!.Methods.Where(m => m.Name == binding.Member).ToArray()) type.Methods.Remove(member);
                        break;
                    case BindingKind.Field:
                        foreach (var member in type!.Fields.Where(m => m.Name == binding.Member).ToArray()) type.Fields.Remove(member);
                        break;
                    case BindingKind.Property:
                        foreach (var member in type!.Properties.Where(m => m.Name == binding.Member).ToArray()) type.Properties.Remove(member);
                        break;
                    case BindingKind.Constructor: type!.Constructors.Clear(); break;
                    default: throw new InvalidOperationException("unexpected composed contract kind");
                }
                var broken = SurfaceDiffer.ResolveManifests(new[] { manifest }, clone);
                Check(broken.Findings.Any(f => f.BindingId == id && f.Severity == Severity.Error),
                    "native dependency deletion did not fail the Critical binding: " + id);
            }
            var arrival = manifest.Bindings.Single(b => b.Id == "robotkit.property.actionsuccess.shouldignore");
            var missingArrival = SurfaceSnapshot.Parse(baseline.ToCanonicalJson());
            var arrivalType = missingArrival.FindType(arrival.TypeKey)!;
            foreach (var property in arrivalType.Properties.Where(value => value.Name == arrival.Member).ToArray())
                arrivalType.Properties.Remove(property);
            Check(SurfaceDiffer.ResolveManifests(new[] { manifest }, missingArrival).Findings
                .Any(finding => finding.BindingId == arrival.Id && finding.Severity == Severity.Error),
                "missing native ShouldIgnore must fail the Critical compatibility gate");
            Check(!manifest.Bindings.Any(b => b.Member == "CollectReferencedLoreUris"), "removed lore API must not remain a dependency");
            Console.WriteLine("Composed Critical metadata contracts: " + expected.Length + " dependency deletions detected.");
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
