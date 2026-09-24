using System;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class TransportFixtureRunner
    {
        public static JsonElement Snapshot(JsonElement body)
        {
            var transport = body.GetProperty("transport").GetString();
            string RoundTrip(string json) => transport switch
            {
                "plan" => LaunchTransportJson.WritePlan(LaunchTransportJson.ReadPlan(json)),
                "profile" => LaunchTransportJson.WriteProfile(LaunchTransportJson.ReadProfile(json)),
                "progress" => LaunchTransportJson.WriteProgress(LaunchTransportJson.ReadProgress(json)),
                "outcome" => LaunchTransportJson.WriteOutcome(LaunchTransportJson.ReadOutcome(json)),
                "observation" => LaunchTransportJson.WriteObservation(LaunchTransportJson.ReadObservation(json)),
                _ => throw new InvalidOperationException("Unknown transport fixture: " + transport)
            };
            object output;
            try
            {
                using var first = JsonDocument.Parse(RoundTrip(body.TryGetProperty("wireJson", out var wire) ? wire.GetString()! : body.GetProperty("payload").GetRawText()));
                using var second = JsonDocument.Parse(RoundTrip(first.RootElement.GetRawText()));
                if (!DeclarationDigest.Equal(first.RootElement, second.RootElement))
                    throw new InvalidOperationException("Transport normalization is not stable.");
                output = new { outcome = "accept", normalized = first.RootElement.Clone() };
            }
            catch (InvalidDataException) { output = new { outcome = "reject", errorCodes = new[] { "transport" } }; }
            using var result = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return result.RootElement.Clone();
        }
    }
}
