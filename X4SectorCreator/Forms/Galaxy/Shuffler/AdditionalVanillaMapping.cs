using System.Text.Json;
using X4SectorCreator.Configuration;

namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    internal static class AdditionalVanillaMapping
    {
        internal static Dictionary<string, string> VassalFactions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "hatikvah", "argon" },
            { "antigone", "argon" },
            { "pioneers", "terran" }
        };

        internal static Dictionary<string, string> ObligateNeighborFactions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "holyorder", "paranid" }
        };

        internal static Dictionary<string, string> MandatoryGateways = new(StringComparer.OrdinalIgnoreCase)
        {
            // Order matters. The second cluster and the territory behind it will be pushed out.
            { "Heretic's End", "Watchful Gaze" },
            { "Getsu Fune", "Asteroid Belt" },
            { "Ianamus Zura", "Mitsuno's Revelation" }
        };

        private static Dictionary<string, string> vanillaPoliceCached;

        internal const string XenonName = "xenon";

        internal static Dictionary<string, string> GetDefaultPolice()
        {
            if (vanillaPoliceCached != null) return vanillaPoliceCached;
            string policeJson = File.ReadAllText(Constants.DataPaths.VanillaPoliceFactionsMappingFilePath);
            return vanillaPoliceCached ??= JsonSerializer.Deserialize<Dictionary<string, string>>(policeJson, ConfigSerializer.JsonSerializerOptions);
        }
    }
}
