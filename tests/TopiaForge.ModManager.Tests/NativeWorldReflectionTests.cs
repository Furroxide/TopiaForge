using System;
using TopiaForge.ModManager;
namespace TopiaForge.ModManager.Tests
{
    internal static class NativeWorldReflectionTests
    {
        internal static void Run()
        {
            var failures = new System.Collections.Generic.List<Exception>();
            foreach (var test in new Action[] { CheckpointUsesItsDeclaredAssetType,
                AwaiterUsesOnlyExactNonGenericMethods, UnsupportedCompletionShapeFailsBeforeInvocation,
                OpenGenericAwaiterIsUnavailable })
            {
                try { test(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("Native world reflection tests passed (4 cases).");
        }
        private static void CheckpointUsesItsDeclaredAssetType()
        {
            var method = NativeWorldReflection.CheckpointLoader(typeof(Loader), typeof(Checkpoint));
            Assert(method != null && method.GetParameters()[1].ParameterType == typeof(Checkpoint),
                "A derived checkpoint must not change the declared native overload.");
            var result = method!.Invoke(null, new object[] { false, new SpecializedCheckpoint() }) as Awaitable;
            Assert(result != null && result.IsDeclaredLoader, "The loader selected a runtime-subtype overload.");
        }
        private static void AwaiterUsesOnlyExactNonGenericMethods()
        {
            var methods = NativeWorldReflection.Awaiter(typeof(Awaitable));
            Assert(methods != null, "Unrelated overloads must not make the supported native awaiter unavailable.");
            var awaiter = methods!.GetAwaiter.Invoke(new Awaitable(true), null);
            methods.GetResult.Invoke(awaiter, null);
            Assert(((Awaiter)awaiter!).Calls == 1, "The exact parameterless GetResult must run once.");
        }
        private static void UnsupportedCompletionShapeFailsBeforeInvocation()
        {
            Assert(NativeWorldReflection.Awaiter(typeof(WrongAwaitable)) == null,
                "An awaiter with a non-boolean IsCompleted must fail preflight.");
        }
        private static void OpenGenericAwaiterIsUnavailable()
        {
            Assert(NativeWorldReflection.Awaiter(typeof(GenericAwaitable)) == null,
                "An unbound generic GetAwaiter cannot be invoked after native effects.");
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public class Checkpoint { }
        public sealed class SpecializedCheckpoint : Checkpoint { }
        public static class Loader
        {
            public static Awaitable LoadSceneImpl(bool automatic, Checkpoint checkpoint) => new Awaitable(true);
            public static Awaitable LoadSceneImpl(bool automatic, SpecializedCheckpoint checkpoint) => new Awaitable(false);
        }
        public sealed class Awaitable
        {
            public Awaitable(bool declared) { IsDeclaredLoader = declared; }
            public bool IsDeclaredLoader { get; }
            public Awaiter GetAwaiter() => new Awaiter();
            public Awaiter GetAwaiter(int unrelated) => throw new Exception("wrong overload");
        }
        public sealed class Awaiter
        {
            public int Calls;
            public bool IsCompleted => true;
            public void GetResult() { Calls++; }
            public void GetResult(int unrelated) => throw new Exception("wrong overload");
            public void GetResult<T>() => throw new Exception("unbound generic");
            public void OnCompleted(Action callback) => callback();
        }
        public sealed class WrongAwaitable { public WrongAwaiter GetAwaiter() => throw new Exception("preflight must not invoke"); }
        public sealed class WrongAwaiter
        {
            public string IsCompleted => "yes";
            public void GetResult() { }
            public void OnCompleted(Action callback) { }
        }
        public sealed class GenericAwaitable { public Awaiter GetAwaiter<T>() => throw new Exception("unbound generic"); }
    }
}
