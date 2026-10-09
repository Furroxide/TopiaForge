using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public static partial class LaunchTransportJson
    {
        public static string WriteObservation(RuntimeObservationEnvelope observation) => Bounded(ObservationJson(observation), MaxObservationBytes);
        public static RuntimeObservationEnvelope ReadObservation(string json) => Read(json, MaxObservationBytes, ParseObservation);
        public static string WriteProgress(LaunchProgress progress) => Bounded(ProgressJson(progress), MaxDocumentBytes);
        public static LaunchProgress ReadProgress(string json) => Read(json, MaxDocumentBytes, ParseProgress);
        public static string WriteOutcome(LaunchOutcome outcome) => Bounded(OutcomeJson(outcome), MaxDocumentBytes);
        public static LaunchOutcome ReadOutcome(string json) => Read(json, MaxDocumentBytes, ParseOutcome);

        private static string ObservationJson(RuntimeObservationEnvelope observation)
        {
            var worlds = observation.DiscoveredWorlds.Select(world => Object(Pair("id", Quote(world.Id)),
                Pair("familyId", Quote(world.FamilyId)), Pair("name", Quote(world.Name)), Optional("description", world.Description)));
            var availability = observation.Availability.Select(item => Object(Pair("kind", Quote(item.Kind)),
                Pair("id", Quote(item.Id)), Pair("blocks", BlocksJson(item.Blocks))));
            return Object(Pair("schemaVersion", "1"), Pair("profileId", Quote(observation.ProfileId)),
                Pair("profileRevision", Number(observation.ProfileRevision)), Pair("producer", PackageJson(observation.Producer)),
                Pair("packageSetDigest", Quote(observation.PackageSetDigest)), Pair("observationRevision", Number(observation.ObservationRevision)),
                Pair("discoveredWorlds", Array(worlds)), Pair("availability", Array(availability)));
        }

        private static RuntimeObservationEnvelope ParseObservation(string json)
        {
            var value = new Fields(json, "schemaVersion", "profileId", "profileRevision", "producer", "packageSetDigest", "observationRevision", "discoveredWorlds", "availability");
            value.Version(1);
            var worlds = Values(value.Required("discoveredWorlds")).Select(raw =>
            {
                var world = new Fields(raw, "id", "familyId", "name", "description");
                return new DiscoveredWorldObservation(world.String("id"), world.String("familyId"), world.String("name"), world.OptionalString("description"));
            });
            var availability = Values(value.Required("availability")).Select(raw =>
            {
                var item = new Fields(raw, "kind", "id", "blocks");
                return new DeclarationAvailability(item.String("kind"), item.String("id"), ParseBlocks(item.Required("blocks")));
            });
            return new RuntimeObservationEnvelope(value.String("profileId"), value.Integer("profileRevision"), ParsePackage(value.Required("producer")),
                value.String("packageSetDigest"), value.Integer("observationRevision"), worlds, availability);
        }

        private static string ProgressJson(LaunchProgress progress) => Object(Pair("schemaVersion", "1"), Pair("requestId", Quote(progress.RequestId)),
            Pair("sequence", Number(progress.Sequence)), Pair("phase", Quote(progress.Phase)), Optional("sessionId", progress.SessionId),
            progress.NativeBusy.HasValue ? Pair("nativeBusy", Boolean(progress.NativeBusy.Value)) : null);

        private static LaunchProgress ParseProgress(string json)
        {
            var value = new Fields(json, "schemaVersion", "requestId", "sequence", "phase", "sessionId", "nativeBusy");
            value.Version(1);
            return new LaunchProgress(value.String("requestId"), value.Integer("sequence"), value.String("phase"), value.OptionalString("sessionId"), value.OptionalBool("nativeBusy"));
        }

        private static string OutcomeJson(LaunchOutcome outcome) => Object(Pair("schemaVersion", "1"), Pair("kind", Quote(outcome.Kind)),
            Pair("requestId", Quote(outcome.RequestId)), Pair("sequence", Number(outcome.Sequence)), Pair("phase", Quote(outcome.Phase)),
            Pair("status", Quote(outcome.Status)), Pair("blocks", BlocksJson(outcome.Blocks)), Optional("sessionId", outcome.SessionId),
            Optional("command", outcome.Command), outcome.Error == null ? null : Pair("error", Object(
                Pair("code", Quote(outcome.Error.Code)), Pair("message", Quote(outcome.Error.Message)))));

        private static LaunchOutcome ParseOutcome(string json)
        {
            var value = new Fields(json, "schemaVersion", "kind", "requestId", "sequence", "phase", "status", "blocks", "sessionId", "command", "error");
            value.Version(1);
            LaunchExecutionError? error = null;
            if (value.Has("error"))
            {
                var detail = new Fields(value.Required("error"), "code", "message");
                error = new LaunchExecutionError(detail.String("code"), detail.String("message"));
            }
            return new LaunchOutcome(value.String("kind"), value.String("requestId"), value.Integer("sequence"), value.String("phase"), value.String("status"),
                ParseBlocks(value.Required("blocks")), value.OptionalString("sessionId"), value.OptionalString("command"), error);
        }

        internal static string BlocksJson(IEnumerable<LaunchBlock> blocks) => Array(blocks.Select(block => Object(
            Pair("code", Quote(CodeName(block.Code))), Pair("subject", Quote(block.Subject)), Pair("subjectVersion", Quote(block.SubjectVersion)))));

        internal static string CodeName(LaunchBlockCode code)
        {
            var name = code.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static IReadOnlyList<LaunchBlock> ParseBlocks(string json) => Values(json).Select(raw =>
        {
            var item = new Fields(raw, "code", "subject", "subjectVersion");
            var name = item.String("code");
            var codes = ((LaunchBlockCode[])Enum.GetValues(typeof(LaunchBlockCode))).Where(code => CodeName(code) == name).ToArray();
            if (codes.Length != 1) throw Invalid("Unknown launch block code.");
            var version = item.String("subjectVersion");
            return new LaunchBlock(codes[0], item.String("subject"), version);
        }).ToArray();
    }
}
