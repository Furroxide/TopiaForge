using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Multiplayer.Generators
{
    public sealed partial class MultiplayerContractGenerator
    {
        private const int MaximumUpdatesPerSecondLimit = 60;

        private static readonly DiagnosticDescriptor InvalidSetting = new DiagnosticDescriptor(
            "TFMP016",
            "Invalid multiplayer setting",
            "'{0}' sets {1} to {2}; {3}",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Error,
            true,
            helpLinkUri: DocsRoot + "TFMP016");

        /// <summary>Reads and validates the settings shared by commands and replicated objects.</summary>
        private static bool TryReadHandlerSettings(
            SourceProductionContext context,
            IMethodSymbol method,
            AttributeData attribute,
            out int prediction,
            out int maximumPerSecond,
            out int maximumPayloadBytes)
        {
            prediction = GetNamedInt(attribute, "Prediction", 0);
            maximumPerSecond = GetNamedInt(attribute, "MaximumPerSecond", 30);
            maximumPayloadBytes = GetNamedInt(attribute, "MaximumPayloadBytes", 16 * 1024);
            var location = method.Locations.FirstOrDefault();
            var valid = CheckSetting(context, location, method.Name, "Prediction", prediction, 0, 1,
                "use PredictionMode.None or PredictionMode.Owner");
            valid &= CheckSetting(context, location, method.Name, "MaximumPerSecond", maximumPerSecond, 1, 1000,
                "use 1 to 1000");
            valid &= CheckSetting(context, location, method.Name, "MaximumPayloadBytes", maximumPayloadBytes, 64,
                1024 * 1024, "use 64 to 1048576");
            return valid;
        }

        /// <summary>Reads and validates the downstream delivery settings of replicated state and objects.</summary>
        private static bool TryReadDeliverySettings(
            SourceProductionContext context,
            ISymbol owner,
            AttributeData attribute,
            out int delivery,
            out int maximumUpdatesPerSecond)
        {
            delivery = GetNamedInt(attribute, "Delivery", 0);
            maximumUpdatesPerSecond = GetNamedInt(attribute, "MaximumUpdatesPerSecond", 0);
            var location = owner.Locations.FirstOrDefault();
            var valid = CheckSetting(context, location, owner.Name, "Delivery", delivery, 0, 1,
                "use MultiplayerDelivery.Coalesced or MultiplayerDelivery.Immediate");
            valid &= CheckSetting(context, location, owner.Name, "MaximumUpdatesPerSecond", maximumUpdatesPerSecond, 0,
                MaximumUpdatesPerSecondLimit, "use 1 to 60, or 0 for the provider's snapshot rate");
            return valid;
        }

        private static bool CheckSetting(
            SourceProductionContext context,
            Location? location,
            string owner,
            string setting,
            int value,
            int minimum,
            int maximum,
            string remedy)
        {
            if (value >= minimum && value <= maximum) return true;
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidSetting,
                location,
                owner,
                setting,
                value.ToString(CultureInfo.InvariantCulture),
                remedy));
            return false;
        }

        /// <summary>
        /// Schema lines for non-default delivery settings. Defaults add nothing, so contracts that never set them keep
        /// byte-identical schema digests and contract locks.
        /// </summary>
        private static IEnumerable<string> BuildDeliverySchemaLines(
            IEnumerable<StateModel> states,
            IEnumerable<ObjectModel> objects)
        {
            foreach (var state in states.OrderBy(item => item.Id, System.StringComparer.Ordinal))
            {
                if (state.Delivery == 0 && state.MaximumUpdatesPerSecond == 0) continue;
                yield return "state-delivery:" + state.Id + ":" + state.Delivery.ToString(CultureInfo.InvariantCulture) +
                             ":" + state.MaximumUpdatesPerSecond.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var item in objects.OrderBy(item => item.Id, System.StringComparer.Ordinal))
            {
                if (item.Delivery == 0 && item.MaximumUpdatesPerSecond == 0) continue;
                yield return "object-delivery:" + item.Id + ":" + item.Delivery.ToString(CultureInfo.InvariantCulture) +
                             ":" + item.MaximumUpdatesPerSecond.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
