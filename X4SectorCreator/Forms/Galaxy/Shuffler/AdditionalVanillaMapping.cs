using System.Text.Json;
using X4SectorCreator.Configuration;

namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    internal static class AdditionalVanillaMapping
    {
        internal static Dictionary<string, string> VassalFactions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "hatikvah", "argon" },
            { "pioneers", "terran" }
        };

        internal static Dictionary<string, string> ObligateNeighborFactions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "holyorder", "paranid" }
        };

        internal static Dictionary<string, string> ObligateClusterPairs = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Heretic's End", "Watchful Gaze" },
            { "Getsu Fune", "Asteroid Belt" }
        };

        private static Dictionary<string, string> vanillaPoliceCached;

        public static Dictionary<string, string> GetDefaultPolice()
        {
            if (vanillaPoliceCached != null) return vanillaPoliceCached;
            string policeJson = File.ReadAllText(Constants.DataPaths.VanillaPoliceFactionsMappingFilePath);
            return vanillaPoliceCached ??= JsonSerializer.Deserialize<Dictionary<string, string>>(policeJson, ConfigSerializer.JsonSerializerOptions);
        }
    }
}
