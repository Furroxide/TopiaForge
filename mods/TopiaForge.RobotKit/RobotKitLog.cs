using System;

namespace TopiaForge.RobotKit
{
    internal static class RobotKitLog
    {
        // Exception messages, inner exceptions and stack traces can contain paths, credentials or player text.
        // Keep the failure category without inspecting any of that payload, including overridden Message/ToString.
        public static string ExceptionType(Exception exception) => exception.GetType().Name;
    }
}
