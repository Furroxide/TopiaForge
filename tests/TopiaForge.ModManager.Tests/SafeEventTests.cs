using System;
using System.IO;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class SafeEventTests
    {
        public static void Run()
        {
            GenericDispatchContinuesAfterFailure();
            ParameterlessDispatchContinuesWhenErrorReporterFails();
            FallbackWithholdsPrivateMessages();
            Console.WriteLine("SafeEventTests passed.");
        }

        private static void GenericDispatchContinuesAfterFailure()
        {
            var observed = 0;
            var reported = 0;
            Action<int> handlers = _ => throw new InvalidOperationException("first subscriber failed");
            handlers += value => observed = value;

            SafeEvent.Invoke(handlers, 42, _ => reported++);

            Assert(observed == 42, "a failing subscriber must not starve later subscribers");
            Assert(reported == 1, "subscriber failure should be reported once");
        }

        private static void ParameterlessDispatchContinuesWhenErrorReporterFails()
        {
            var reached = false;
            Action handlers = () => throw new InvalidOperationException("subscriber failed");
            handlers += () => reached = true;

            var fallback = CaptureStandardError(() =>
                SafeEvent.Invoke(handlers, _ => throw new InvalidOperationException("logger failed")));

            Assert(reached, "a failing error reporter must not starve later subscribers");
            Assert(fallback.Contains("subscriber failed (InvalidOperationException)") && fallback.Contains("reporter also failed (InvalidOperationException)"),
                "a failed error reporter should use the independent console fallback");
        }

        private static void FallbackWithholdsPrivateMessages()
        {
            var reached = false;
            Action handlers = () => throw new PrivateFailure();
            handlers += () => reached = true;
            var output = CaptureStandardError(() => SafeEvent.Invoke(handlers));
            Assert(reached && output.Contains(nameof(PrivateFailure)), "an unreported failure keeps its type and later subscribers");
            reached = false;
            output = CaptureStandardError(() => SafeEvent.Invoke(handlers, _ => throw new PrivateFailure()));
            Assert(reached && output.Contains("reporter also failed (PrivateFailure)"), "a failed reporter keeps only its type");
        }

        private sealed class PrivateFailure : Exception
        {
            public override string Message => throw new InvalidOperationException("Private messages must not be read.");
            public override string ToString() => throw new InvalidOperationException("Private exception text must not be read.");
        }

        private static string CaptureStandardError(Action action)
        {
            var original = Console.Error;
            using var capture = new StringWriter();
            try
            {
                Console.SetError(capture);
                action();
                return capture.ToString();
            }
            finally
            {
                Console.SetError(original);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
