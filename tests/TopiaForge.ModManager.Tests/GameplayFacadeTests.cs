using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TopiaForge.GravityGun;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class GameplayFacadeTests
    {
        public static void Run()
        {
            TestMathAndRayContracts();
            TestInputDefinitionContracts();
            TestSafeContractSurface();
            TestSchedulerLifetimeCancellation();
            TestGravityGunUsesOnlySafeFacades();
            TestGravityGunSdkOnlySource();
            Console.WriteLine("All gameplay facade tests passed.");
        }

        private static void TestMathAndRayContracts()
        {
            var vector = new Vec3(3f, 0f, 4f);
            Assert(Math.Abs(vector.Length - 5f) < 0.0001f, "Vec3 exposes its length");
            Assert(Vec3.ClampLength(vector, 2f).Equals(new Vec3(1.2f, 0f, 1.6f)),
                "Vec3.ClampLength preserves direction");
            Assert(Vec3.Dot(new Vec3(1f, 2f, 3f), new Vec3(2f, 3f, 4f)) == 20f,
                "Vec3.Dot returns the scalar product");

            var ray = new Ray(new Vec3(1f, 2f, 3f), new Vec3(0f, 0f, 10f));
            Assert(ray.Direction.Equals(new Vec3(0f, 0f, 1f)), "Ray normalizes direction");
            Assert(ray.GetPoint(5f).Equals(new Vec3(1f, 2f, 8f)), "Ray returns points along the ray");
        }

        private static void TestInputDefinitionContracts()
        {
            var definition = new InputActionDefinition(
                "activate",
                "Activate",
                new[] { InputBinding.Key("F"), InputBinding.MouseButton(InputMouseButton.Secondary) });
            Assert(definition.Name == "activate" && definition.DefaultBindings.Count == 2,
                "input definitions preserve stable names and default bindings");
            Assert(definition.DefaultBindings[1].Control == nameof(InputMouseButton.Secondary),
                "input bindings expose SDK-stable control names instead of native ordinals");
            Assert(typeof(InputBinding).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(method => method.Name == nameof(InputBinding.MouseButton)
                                     || method.Name == nameof(InputBinding.GamepadButton))
                    .SelectMany(method => method.GetParameters())
                    .All(parameter => parameter.ParameterType.IsEnum),
                "mouse and gamepad factories must expose SDK enums rather than native integer ordinals");
            Assert(definition.SuppressWhileUiFocused,
                "gameplay input is UI-focus-suppressed by default");
        }

        private static void TestGravityGunUsesOnlySafeFacades()
        {
            var lifetime = new FakeLifetime();
            var events = new FakeEvents(lifetime);
            var input = new FakeInputService();
            var player = new FakePlayerService
            {
                Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)))
            };
            var entity = new FakeEntity();
            var motion = new FakeMotion(entity);
            var entities = new FakeEntityService(motion);
            var physics = new FakePhysicsService(new PhysicsHit(entity, new Vec3(0f, 0f, 4f), new Vec3(0f, 1f, 0f), 4f));
            var config = new GravityGunConfig();

            _ = new GravityGunController(
                config,
                input,
                player,
                entities,
                physics,
                events,
                lifetime,
                new FakeLogger());

            input["grab"].Set(value: 1f, pressed: true, released: false);
            events.RaiseFrame();
            Assert(entities.Acquisitions == 1, "gravity gun acquires the opaque raycast entity");

            input["grab"].Set(value: 1f, pressed: false, released: false);
            events.RaiseFixed(new GameTimeSample(GameLoopPhase.Fixed, 0.02f, 0.02f, 1d, 1));
            Assert(motion.MoveCalls == 1 && motion.LastTarget.Equals(new Vec3(0f, 0f, 4f)),
                "gravity gun drives held motion from the player aim ray during fixed update");

            input["throw"].Set(value: 1f, pressed: true, released: false);
            events.RaiseFrame();
            Assert(motion.ThrowCalls == 1 && motion.LastThrowDirection.Equals(new Vec3(0f, 0f, 1f)),
                "gravity gun throws through IEntityMotion without native objects");

            lifetime.Dispose();
            Assert(motion.Disposed, "mod lifetime releases acquired motion");
        }

        private static void TestSafeContractSurface()
        {
            var contractTypes = new[]
            {
                typeof(IInputService), typeof(IInputAction), typeof(InputActionDefinition), typeof(InputBinding),
                typeof(IPlayerService), typeof(PlayerSnapshot), typeof(IPlayerControlLease), typeof(IEntity),
                typeof(IEntityMotion), typeof(IEntityService), typeof(IPhysicsService), typeof(PhysicsHit),
                typeof(Ray), typeof(IGameTime), typeof(GameTimeSample), typeof(IModScheduler)
            };

            foreach (var type in contractTypes)
            {
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (member is MethodInfo method)
                    {
                        if (!(method.Name == nameof(object.Equals) && method.GetParameters().Length == 1))
                        {
                            AssertSafeType(method.ReturnType, type.Name + "." + method.Name + " return type");
                            foreach (var parameter in method.GetParameters())
                            {
                                AssertSafeType(parameter.ParameterType, type.Name + "." + method.Name + " parameter");
                            }
                        }
                    }
                    else if (member is PropertyInfo property)
                    {
                        AssertSafeType(property.PropertyType, type.Name + "." + property.Name + " property");
                    }
                }
            }
        }

        private static void TestGravityGunSdkOnlySource()
        {
            var root = FindRepoRoot();
            var directory = Path.Combine(root, "mods", "TopiaForge.GravityGun");
            var source = string.Join("\n", Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));
            var project = File.ReadAllText(Path.Combine(directory, "TopiaForge.GravityGun.csproj"));
            foreach (var forbidden in new[] { "UnityEngine", "GameCode", "Harmony", "System.Reflection" })
            {
                Assert(!source.Contains(forbidden, StringComparison.Ordinal)
                    && !project.Contains(forbidden, StringComparison.Ordinal),
                    "GravityGun must not reference " + forbidden + " directly");
            }
        }

        private static void AssertSafeType(Type candidate, string location)
        {
            if (candidate.IsByRef || candidate.IsArray)
            {
                AssertSafeType(candidate.GetElementType()!, location);
                return;
            }

            if (candidate.IsGenericType)
            {
                foreach (var argument in candidate.GetGenericArguments())
                {
                    AssertSafeType(argument, location);
                }
            }

            Assert(candidate != typeof(object) && candidate != typeof(Type)
                && !string.Equals(candidate.Namespace, "System.Reflection", StringComparison.Ordinal)
                && !(candidate.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) ?? false),
                location + " exposes forbidden native/reflection type " + candidate.FullName);
        }

        private static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "TopiaForge.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the TopiaForge repository root.");
        }

        private static void TestSchedulerLifetimeCancellation()
        {
            var lifetime = new FakeLifetime();
            var backend = new UnityScheduler();
            var scheduler = new OwnerScheduler(lifetime, backend, new FakeLogger());
            var nextCalls = 0;
            var repeatingCalls = 0;

            Assert(scheduler.NextFrame(() => nextCalls++).Succeeded,
                "next-frame scheduling returns a successful operation result");
            Assert(scheduler.Every(TimeSpan.FromSeconds(0.1), () => repeatingCalls++).Succeeded,
                "repeating scheduling returns a successful operation result");
            backend.Tick(0d, 0);
            Assert(nextCalls == 0 && repeatingCalls == 0, "scheduled work does not run early");
            backend.Tick(0.1d, 1);
            Assert(nextCalls == 1 && repeatingCalls == 1, "scheduler runs frame and timed work deterministically");

            var delay = scheduler.DelayAsync(TimeSpan.FromSeconds(10));
            lifetime.Dispose();
            backend.Tick(10d, 2);
            Assert(delay.IsCompletedSuccessfully &&
                   delay.Result.ErrorCode == ModErrorCode.Cancelled &&
                   repeatingCalls == 1,
                "mod lifetime cancellation returns a stable cancellation result and stops repeating actions");
            Assert(scheduler.NextFrame(() => { }).ErrorCode == ModErrorCode.Cancelled &&
                   scheduler.DelayAsync(TimeSpan.Zero).Result.ErrorCode == ModErrorCode.Cancelled,
                "scheduler registration after lifetime shutdown reports cancellation instead of throwing");
            backend.Dispose();

            var activeLifetime = new FakeLifetime();
            var stoppedBackend = new UnityScheduler();
            var unavailableScheduler = new OwnerScheduler(activeLifetime, stoppedBackend, new FakeLogger());
            stoppedBackend.Dispose();
            Assert(unavailableScheduler.NextFrame(() => { }).ErrorCode == ModErrorCode.InvalidState &&
                   unavailableScheduler.DelayAsync(TimeSpan.Zero).Result.ErrorCode == ModErrorCode.InvalidState,
                "a stopped runtime scheduler reports InvalidState for synchronous and asynchronous registration");
            activeLifetime.Dispose();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Gameplay facade test failed: " + message);
            }
        }

        private sealed class FakeLifetime : IModLifetime
        {
            private readonly List<IDisposable> resources = new List<IDisposable>();
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private bool disposed;

            public CancellationToken StoppingToken => stopping.Token;
            public bool IsStopping => disposed;

            public IDisposable Track(IDisposable resource)
            {
                resources.Add(resource);
                return resource;
            }

            public IDisposable Defer(Action cleanup)
            {
                return Track(new ActionDisposable(cleanup));
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                stopping.Cancel();
                for (var index = resources.Count - 1; index >= 0; index--)
                {
                    resources[index].Dispose();
                }
            }
        }

        private sealed class FakeEvents : IModEvents
        {
            private readonly IModLifetime lifetime;
            private readonly List<Action<float>> frames = new List<Action<float>>();
            private readonly List<Action<GameTimeSample>> fixedUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<GameTimeSample>> lateUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<string>> scenes = new List<Action<string>>();

            public FakeEvents(IModLifetime lifetime)
            {
                this.lifetime = lifetime;
            }

            public IDisposable SubscribeUpdate(Action<float> handler) => Subscribe(frames, handler);
            public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => Subscribe(fixedUpdates, handler);
            public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => Subscribe(lateUpdates, handler);
            public IDisposable SubscribeSceneLoaded(Action<string> handler) => Subscribe(scenes, handler);
            public void RaiseFrame()
            {
                foreach (var handler in frames.ToArray())
                {
                    handler(1f / 60f);
                }
            }

            public void RaiseFixed(GameTimeSample sample)
            {
                foreach (var handler in fixedUpdates.ToArray())
                {
                    handler(sample);
                }
            }

            private IDisposable Subscribe<T>(List<Action<T>> handlers, Action<T> handler)
            {
                handlers.Add(handler);
                return lifetime.Track(new ActionDisposable(() => handlers.Remove(handler)));
            }
        }

        private sealed class FakeInputService : IInputService
        {
            private readonly Dictionary<string, FakeInputAction> actions =
                new Dictionary<string, FakeInputAction>(StringComparer.Ordinal);

            public bool IsUiFocused => false;
            public FakeInputAction this[string name] => actions[name];

            public IReadOnlyList<InputConflict> GetConflicts() => Array.Empty<InputConflict>();

            public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
            {
                if (actions.ContainsKey(definition.Name))
                {
                    return OperationResult<IInputAction>.Failure(
                        ModErrorCode.Conflict,
                        "The action already exists.");
                }

                var action = new FakeInputAction(definition.Name, definition.DefaultBindings);
                actions.Add(definition.Name, action);
                return OperationResult<IInputAction>.Success(action);
            }
        }

        private sealed class FakeInputAction : IInputAction
        {
            private readonly IReadOnlyList<InputBinding> defaults;

            public FakeInputAction(string name, IReadOnlyList<InputBinding> bindings)
            {
                Name = name;
                defaults = bindings;
                Bindings = bindings;
            }

            public string Name { get; }
            public IReadOnlyList<InputBinding> Bindings { get; private set; }
            public float Value { get; private set; }
            public bool IsHeld => Math.Abs(Value) > 0.0001f;
            public bool WasPressed { get; private set; }
            public bool WasReleased { get; private set; }
            public void Dispose() { }

            public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings)
            {
                Bindings = bindings.ToList().AsReadOnly();
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> ResetBindings() => Rebind(defaults);

            public void Set(float value, bool pressed, bool released)
            {
                Value = value;
                WasPressed = pressed;
                WasReleased = released;
            }
        }

        private sealed class FakePlayerService : IPlayerService
        {
            public PlayerSnapshot? Snapshot { get; set; }

            public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
            {
                snapshot = Snapshot;
                return snapshot != null;
            }

            public bool TryGetHealth(out PlayerHealthSnapshot? health)
            {
                health = null;
                return false;
            }

            public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request) =>
                OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.Unavailable, "not needed");

            public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) =>
                OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.Unavailable, "not needed");

            public OperationResult<IPlayerControlLease> AcquireControl(string reason)
            {
                return OperationResult<IPlayerControlLease>.Failure(ModErrorCode.Unavailable, "not needed");
            }
        }

        private sealed class FakeEntity : IEntity
        {
            public string Id => "entity";
            public string Name => "Test Entity";
            public bool IsAlive => true;
            public Vec3 Position => new Vec3(0f, 0f, 4f);
        }

        private sealed class FakeEntityService : IEntityService
        {
            private readonly IEntityMotion motion;
            public FakeEntityService(IEntityMotion motion) => this.motion = motion;
            public int Acquisitions { get; private set; }

            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                transform = TransformState.Identity;
                return false;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform) =>
                OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "not needed");

            public IReadOnlyList<IEntity> Query(EntityQuery query) => Array.Empty<IEntity>();

            public OperationResult<bool> Destroy(IEntity entity) =>
                OperationResult<bool>.Failure(ModErrorCode.Unavailable, "not needed");

            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
            {
                Acquisitions++;
                return OperationResult<IEntityMotion>.Success(motion);
            }
        }

        private sealed class FakePhysicsService : IPhysicsService
        {
            private readonly PhysicsHit hit;
            public FakePhysicsService(PhysicsHit hit) => this.hit = hit;

            public bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? result)
            {
                result = hit;
                return true;
            }

            public bool TrySphereCast(
                Ray ray,
                float radius,
                float maximumDistance,
                out PhysicsHit? result)
            {
                result = hit;
                return true;
            }

            public IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64) =>
                Array.Empty<IEntity>();
        }

        private sealed class FakeMotion : IEntityMotion
        {
            public FakeMotion(IEntity entity) => Entity = entity;
            public IEntity Entity { get; }
            public bool IsAlive => !Disposed;
            public bool Disposed { get; private set; }
            public int MoveCalls { get; private set; }
            public int ThrowCalls { get; private set; }
            public Vec3 LastTarget { get; private set; }
            public Vec3 LastThrowDirection { get; private set; }

            public OperationResult<Vec3> MoveToward(Vec3 target, float responsiveness, float damping, float maximumSpeed, float deltaTime)
            {
                MoveCalls++;
                LastTarget = target;
                return OperationResult<Vec3>.Success(target);
            }

            public OperationResult<Vec3> Throw(Vec3 direction, float speed)
            {
                ThrowCalls++;
                LastThrowDirection = direction;
                return OperationResult<Vec3>.Success(direction * speed);
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private sealed class FakeLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }

        private sealed class ActionDisposable : IDisposable
        {
            private Action? action;
            public ActionDisposable(Action action) => this.action = action;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
