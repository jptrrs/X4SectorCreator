using System.Text.Json;
using System.Xml.Linq;
using X4SectorCreator.Configuration;
using X4SectorCreator.Objects;

namespace DevConsole.Extractors
{
    internal static class PoliceFactionExtractor
    {
        internal static void ExtractPoliceFaction(string factionsPath)
        {
            var polices = CollectPoliceFaction(factionsPath);

            Console.WriteLine($"Exported \"{polices.Count}\" faction relations.");

            var xml = JsonSerializer.Serialize(polices, ConfigSerializer.JsonSerializerOptions);
            if (!Directory.Exists(Path.GetDirectoryName(Path.Combine("Extractions", "ExtractedPoliceFactions.xml"))))
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine("Extractions", "ExtractedPoliceFactions.xml")));
            File.WriteAllText(Path.Combine("Extractions", "ExtractedPoliceFactions.xml"), xml);
        }

        private static Dictionary<string, string> CollectPoliceFaction(string factionsPath)
        {
            var xdoc = XDocument.Load(factionsPath);
            var factions = xdoc.Element("factions").Elements("faction");

            var result = new Dictionary<string, string>();
            foreach (var faction in factions)
            {
                if (faction.Attribute("id").Value.Equals("Ownerless", StringComparison.OrdinalIgnoreCase)) continue;
                string policeFaction = faction.Attribute("policefaction")?.Value ?? "none";
                result[faction.Attribute("id").Value] = policeFaction;
            }
            return result;
        }
    }
}
