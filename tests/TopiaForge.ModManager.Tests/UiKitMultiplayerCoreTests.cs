using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Unit tests for the Unity-free Core math behind the multiplayer kit widgets: plain-text
    /// bounding, signal bars, decibel metering and peak hold, talk-state presentation and pulse,
    /// the phase-stepper state machine, chat eviction and fade, camera projection and name-tag
    /// placement, and stable table sorting. Every steady-state path is also checked to allocate
    /// nothing.
    /// </summary>
    internal static partial class UiKitMultiplayerCoreTests
    {
        public static void Run()
        {
            PlainTextIsBoundedAndInert();
            SignalBarsMapPingQualityAndLevels();
            DecibelMappingAndZones();
            MeterBallisticsAndPeakHold();
            TalkStatesStayDistinctWithoutColor();
            TalkPulseHonoursMotion();
            PhaseStepperTransitions();
            PhaseStepperProgressAndBounds();
            ChatLogEvictsOldestAndSanitizes();
            ChatLinesFadeWhileClosed();
            ProjectionMirrorsUnityCameraConventions();
            NametagFalloffAndPlacement();
            TableSortStateToggles();
            StableSorterKeepsTiesInSuppliedOrder();
            StableSorterSurvivesBadComparers();
            SortStateClearsWhenRowsFallBack();
            IconRasterDrawsEveryIcon();
            SteadyStatePathsAllocateNothing();
            Console.WriteLine("UiKitMultiplayerCoreTests passed.");
        }

        private static void AssertNear(float actual, float expected, string message, float tolerance = 0.0001f)
        {
            if (Math.Abs(actual - expected) > tolerance || float.IsNaN(actual))
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertEqual<T>(T actual, T expected, string message)
        {
            if (!Equals(actual, expected))
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message + " (expected " + typeof(TException).Name + ")");
        }
    }
}
