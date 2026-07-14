using System;
using System.IO;
using Robotopia.Mods.Internal;

namespace Robotopia.ModManager.Tests
{
    internal static class SafeEventTests
    {
        public static void Run()
        {
            GenericDispatchContinuesAfterFailure();
            ParameterlessDispatchContinuesWhenErrorReporterFails();
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
            Assert(fallback.Contains("subscriber failed") && fallback.Contains("logger failed"),
                "a failed error reporter should use the independent console fallback");
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
