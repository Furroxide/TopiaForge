namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldWidgetAdapterTests
    {
        private const string Scenarios = """
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.SceneManagement;
            public static class WidgetScenarios
            {
                private static void Check(bool condition, string message)
                { if (!condition) throw new InvalidOperationException(message); }
                private static OwnerUiService Start(out UnityEntityRegistry entities)
                {
                    UnityMainThreadGuard.CaptureCurrentThread();
                    SceneManager.Active = new Scene { Data = new SceneData() };
                    Physics.Results = Array.Empty<RaycastHit>(); Physics.Calls = 0;
                    entities = new UnityEntityRegistry();
                    var service = new OwnerUiService(); service.AttachWorldWidgets(entities); return service;
                }
                private static TopiaForgeHudLayer Root(OwnerUiService service) => service.host.Roots.Last();
                private static TopiaForgeNametag LastTag(OwnerUiService service) => Root(service).Layer!.Tags.Last();
                private static IUiWorldLabel Add(OwnerUiService service, UiWorldLabelRequest? request = null)
                {
                    var result = service.Labels.Add(request ?? UiWorldLabelRequest.At(new Vec3(0, 0, 10), "Marker"));
                    Check(result.Succeeded, "Label creation must succeed: " + result.ErrorMessage); return result.Value!;
                }
                private static void Tick(OwnerUiService service) => Root(service).Go.Components.Single().Frame("LateUpdate");
                public static void ClockAndCountdown()
                {
                    Time.realtimeSinceStartupAsDouble = 100;
                    var service = Start(out _);
                    Check(service.ClockTime == 0, "Clock begins before the first frame.");
                    var view = service.Render(new UiCountdown(service.ClockTime + 3, "Remaining", UiTone.Warning));
                    var driver = view.Go.Components.Single(); var text = view.Text!;
                    Check(text.Seconds == 3 && text.Prefix == "Remaining " && text.Plain && view.IsDynamic && text.Color == TopiaForgeTone.Warning,
                        "Countdown renders immediately through a dynamic plain-text themed label.");
                    Time.realtimeSinceStartupAsDouble += 1.2; driver.Frame("Update");
                    Check(text.Seconds == 2, "Production countdown advances from real time without an author callback.");
                    // No scaled time or gameplay-frame advance: a paused world still consumes wall time.
                    Time.realtimeSinceStartupAsDouble += 5; driver.Frame("Update");
                    Check(text.Seconds == 0, "Paused/expired countdown reaches zero.");
                    var writes = text.Writes; driver.Frame("Update");
                    Check(text.Writes == writes, "Settled countdown does not write text again.");
                    view.Go.Destroy(); Time.realtimeSinceStartupAsDouble += 10;
                    var reads = Time.Reads; driver.Frame("Update");
                    Check(text.Writes == writes && Time.Reads == reads, "Destroyed countdown driver releases its targets.");
                    var next = Start(out _); Check(next.ClockTime == 0, "A new scope has its own clock origin.");
                    var progress = service.Render(new UiProgress(0.25f, "Tasks", UiTone.Success));
                    Check(progress.Progress!.Fraction == 0.25f && progress.Progress.Color == TopiaForgeTone.Success && progress.Text!.Plain,
                        "Progress sends fraction, tone and plain caption to the kit.");
                }
                public static void OwnershipAndPool()
                {
                    for (var reload = 0; reload < 10; reload++)
                    {
                        var service = Start(out _); var other = Start(out _);
                        var handles = new IUiWorldLabel[64];
                        for (var i = 0; i < handles.Length; i++) handles[i] = Add(service);
                        Check(service.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Overflow")).ErrorCode == ModErrorCode.Unavailable,
                            "Production pool rejects the 65th label.");
                        handles[0].Dispose(); handles[0].Dispose();
                        var replacement = Add(service); var tag = LastTag(service);
                        handles[0].SetText("Stale"); handles[0].SetPosition(Vec3.Zero);
                        Check(!handles[0].IsAlive && tag.Text == "Marker" && replacement.IsAlive && other.host.Roots.Count == 0,
                            "Stale handles and another scope cannot affect the replacement.");
                        service.lifetime.Dispose();
                        Check(handles.All(item => !item.IsAlive) && !replacement.IsAlive && Root(service).Go.Destroyed && service.host.Subscribers == 0,
                            "Scope teardown releases labels, root and loss subscription.");
                        Check(service.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Late")).ErrorCode == ModErrorCode.Cancelled,
                            "Stopped scope rejects new labels.");
                    }
                }
                public static void ScenesAndTargets()
                {
                    var service = Start(out var entities);
                    var active = SceneManager.Active; var additive = new Scene { Data = new SceneData() };
                    var entity = new FakeEntity("target", "Target", new Vec3(1, 2, 10));
                    entities.Objects[entity] = new GameObject { scene = additive };
                    var label = Add(service, UiWorldLabelRequest.For(entity, "Target")); var tag = LastTag(service);
                    entity.Position = new Vec3(2, 3, 10);
                    Check(tag.Anchor.TryGetPosition(out var point) && point == entity.Position && tag.Offset == 0.35f,
                        "Native binding follows an entity with an offset.");
                    active.Data.Loaded = false; Tick(service);
                    Check(label.IsAlive, "Unloading another scene does not retire an additive-scene entity label.");
                    label.SetPosition(new Vec3(4, 5, 6)); entity.Destroy();
                    Check(tag.Anchor.TryGetPosition(out point) && point == new Vec3(4, 5, 6) && tag.Offset == 0,
                        "Detached label has a fixed point and no entity offset.");
                    additive.Data.Loaded = false; Tick(service);
                    Check(!label.IsAlive && !tag.Active, "Owning scene unload releases the view.");
                    service = Start(out _);
                    entity = new FakeEntity("gone", "Gone", Vec3.Zero);
                    label = Add(service, UiWorldLabelRequest.For(entity, "Gone")); tag = LastTag(service);
                    entity.Destroy(); Tick(service);
                    Check(!label.IsAlive && !tag.Active, "LateUpdate reclaims a despawned target without a camera.");
                    label = Add(service, UiWorldLabelRequest.For(new ThrowingPosition(), "Broken")); tag = LastTag(service);
                    Check(!tag.Anchor.TryGetPosition(out _) && !label.IsAlive && !tag.Active,
                        "Throwing position releases the view without leaking its exception.");
                }
                public static void Occlusion()
                {
                    var service = Start(out var entities);
                    var entity = new FakeEntity("target", "Target", new Vec3(0, 0, 10));
                    var native = new GameObject(); entities.Objects[entity] = native;
                    Add(service, UiWorldLabelRequest.For(entity, "Target", maximumDistance: 17));
                    var policy = (ITopiaForgeNametagPresentation)LastTag(service).Anchor;
                    Check(policy.Falloff.Maximum == 17 && policy.CanShow(Vec3.Zero, entity.Position), "Own range and clear ray are accepted.");
                    Physics.Results = new[] { new RaycastHit { collider = new Collider { transform = new Transform { Parent = native.transform } } } };
                    Check(policy.CanShow(Vec3.Zero, entity.Position), "Own child colliders do not occlude their label.");
                    Physics.Results = new[] { new RaycastHit { collider = new Collider() } };
                    Check(!policy.CanShow(Vec3.Zero, entity.Position), "Other solid geometry occludes the label.");
                    Physics.Results = Enumerable.Repeat(new RaycastHit { collider = new Collider { transform = native.transform } }, 32).ToArray();
                    Check(!policy.CanShow(Vec3.Zero, entity.Position), "A full ray buffer fails closed.");
                    Add(service, UiWorldLabelRequest.At(entity.Position, "Opt out", occlusion: false));
                    var optedOut = (ITopiaForgeNametagPresentation)LastTag(service).Anchor;
                    var calls = Physics.Calls;
                    Check(optedOut.CanShow(Vec3.Zero, entity.Position) && Physics.Calls == calls, "Opt-out does no raycast.");
                    service.gameplay.IsGameplay = false;
                    Check(!optedOut.CanShow(Vec3.Zero, entity.Position), "Menu hides even labels with occlusion disabled.");
                }
                public static void CanvasRecovery()
                {
                    var service = Start(out _); var handle = Add(service);
                    service.host.Lose(Root(service));
                    Check(service.host.Roots.Count == 2 && Root(service).Layer!.Tags.Count == 1 && handle.IsAlive,
                        "Canvas loss rebinds each live label once.");
                    handle.SetText("Recovered"); Check(LastTag(service).Text == "Recovered", "Existing handle targets recovered view.");
                    SceneManager.Active.Data.Loaded = false;
                    service.host.Lose(Root(service)); Check(!handle.IsAlive, "Scene loss during recovery cannot revive a label.");
                    service.lifetime.Dispose();
                    var count = service.host.Roots.Count; service.host.Lose(Root(service));
                    Check(service.host.Roots.Count == count, "Stopped owner never recreates a canvas.");
                }
                public static void FailedLayerCreation()
                {
                    var failures = new System.Collections.Generic.List<string>();
                    foreach (var failPool in new[] { true, false })
                    {
                        var service = Start(out _);
                        TopiaForgeHudLayer.FailNextNametags = failPool; GameObject.FailNextComponent = !failPool;
                        var result = service.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Retry"));
                        Check(result.ErrorCode == ModErrorCode.External, "Layer creation failure returns an actionable error.");
                        var retainedCanvas = service.host.Roots.Any(root => !root.Go.Destroyed);
                        var label = Add(service);
                        var hasDriver = Root(service).Go.Components.Count == 1;
                        SceneManager.Active.Data.Loaded = false;
                        foreach (var driver in Root(service).Go.Components) driver.Frame("LateUpdate");
                        var retired = !label.IsAlive;
                        service.lifetime.Dispose();
                        if (retainedCanvas || !hasDriver || !retired)
                            failures.Add((failPool ? "Pool" : "Driver") + " failure: retained canvas=" + retainedCanvas
                                + ", retry driver=" + hasDriver + ", scene retirement=" + retired);
                    }
                    Check(failures.Count == 0, "Failed layer creation must not retain a live canvas without a cleanup driver. " + string.Join("; ", failures));
                }
                public static void RestingAllocations()
                {
                    var service = Start(out _); var label = Add(service); var tag = LastTag(service);
                    var labels = (UnityWorldLabels)service.Labels;
                    var policy = (ITopiaForgeNametagPresentation)tag.Anchor;
                    for (var i = 0; i < 512; i++) { labels.Tick(); tag.Anchor.TryGetPosition(out var point); policy.CanShow(Vec3.Zero, point); label.SetText("Marker"); }
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) { labels.Tick(); tag.Anchor.TryGetPosition(out var point); policy.CanShow(Vec3.Zero, point); label.SetText("Marker"); }
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "Settled adapter label ticks allocate zero bytes.");
                    var view = service.Render(new UiCountdown(service.ClockTime + 10));
                    var update = (Action)view.Go.Components[0].GetType().GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .CreateDelegate(typeof(Action), view.Go.Components[0]);
                    for (var i = 0; i < 512; i++) update();
                    before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) update();
                    Check(GC.GetAllocatedBytesForCurrentThread() == before && view.Text!.Writes == 1, "Settled production driver allocates zero bytes and writes once.");
                }
                public static void ThreadOwnership()
                {
                    var service = Start(out _); var label = Add(service); var failures = 0;
                    Task.Run(() =>
                    {
                        foreach (var action in new Action[] { () => label.SetText("Wrong"), () => label.SetPosition(Vec3.Zero), label.Dispose, () => _ = service.ClockTime, () => _ = service.Labels })
                        { try { action(); } catch (InvalidOperationException) { failures++; } }
                    }).GetAwaiter().GetResult();
                    Check(failures == 5 && label.IsAlive && LastTag(service).Text == "Marker", "Foreign-thread calls fail before mutating native state.");
                    service.lifetime.Dispose();
                }
                private sealed class ThrowingPosition : IEntity
                {
                    public string Id => "broken"; public string Name => "Broken"; public bool IsAlive => true;
                    public Vec3 Position => throw new InvalidOperationException("Private entity details");
                }
            }
            """;
    }
}
