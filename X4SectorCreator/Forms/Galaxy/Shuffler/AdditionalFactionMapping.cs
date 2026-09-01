using System.Text.Json;
using X4SectorCreator.Configuration;

namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    internal static class AdditionalFactionMapping
    {
        private static Dictionary<string, string> vanillaPoliceCached;

        public static Dictionary<string, string> GetDefaultPolice()
        {
            if (vanillaPoliceCached != null) return vanillaPoliceCached;
            string policeJson = File.ReadAllText(Constants.DataPaths.VanillaPoliceFactionsMappingFilePath);
            return vanillaPoliceCached ??= JsonSerializer.Deserialize<Dictionary<string, string>>(policeJson, ConfigSerializer.JsonSerializerOptions);
        }
    }
}
