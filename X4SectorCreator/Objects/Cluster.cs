using System.Text.Json.Serialization;
using X4SectorCreator.Forms;
using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    public class Cluster : ICloneable
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string BackgroundVisualMapping { get; set; }
        public string BaseGameMapping { get; set; }
        public string Soundtrack { get; set; }
        public string Dlc { get; set; }
        public List<Sector> Sectors { get; set; }
        public Point Position
        {
            get
            {
                return position;
            }
            set
            {
                position = value;
                plannedPosition = null;
            }
        }
        public bool CustomSectorPositioning { get; set; } = false;
        public string CustomClusterXml { get; set; }

        public const string TemplateClusterXml = @"<?xml version=""1.0""?>
<components>
	<component name=""{CLUSTERCODE}"" class=""celestialbody"">
		<source geometry=""assets\environments\cluster\Cluster_01_data""/>
		
	</component>
</components>";

        internal bool shuffled = false;
        internal List<Sector> Destinations = new List<Sector>();
        internal Dictionary<Sector, List<(Gate, Sector)>> Exits = new Dictionary<Sector, List<(Gate, Sector)>>();
        internal List<int> BridgeFor = new List<int>();
        internal Point AnchorOffset = new Point();
        private Point? plannedPosition;

        [JsonIgnore]
        public Point PlannedPosition
        {
            get
            {
                return plannedPosition ?? Position;
            }
            set
            {
                plannedPosition = value;
            }
        }

        [JsonIgnore]
        public int AssignedTerritoryId
        {
            get
            {
                return assignedTerritoryId;
            }
            set
            {
                assignedTerritoryId = value;
                if (Sectors.Any())
                {
                    foreach (var sector in Sectors)
                    {
                        sector.AssignedTerritoryId = value;
                    }
                }
            }
        }

        private int assignedTerritoryId = -1;
        private Point position;

        [JsonIgnore]
        public Hexagon Hexagon { get; set; }

        [JsonIgnore]
        public bool IsBaseGame => !shuffled && !string.IsNullOrWhiteSpace(BaseGameMapping);

        [JsonIgnore]
        public List<(Sector origin, Gate gate, Sector destination)> ExitPoints
        {
            get
            {
                var roads = new List<(Sector, Gate, Sector)>();
                foreach (var sector in Exits.Keys)
                {
                    foreach (var exit in Exits[sector])
                    {
                        roads.Add((sector, exit.Item1, exit.Item2));
                    }
                }
                return roads;
            }
            set
            {
                Exits.Clear();
                Destinations.Clear();
                foreach (var item in value)
                {
                    if (item.Item1 == null || item.Item2 == null || item.Item3 == null) continue;
                    var sector = item.Item1;
                    var gate = item.Item2;
                    var dest = item.Item3;
                    Destinations.AddUnique(dest);
                    if (Exits.ContainsKey(sector))
                    {
                        Exits[sector].AddUnique((gate, dest));
                    }
                    else
                    {
                        Exits.Add(sector, new List<(Gate, Sector)> { (gate, dest) });
                    }
                }
            }
        }


        public void AutoPositionSectors(bool randomize = false, Random random = null)
        {
            int sectorCount = Sectors.Count;
            if (sectorCount <= 1)
            {
                return; // Always centered, placement has no effect
            }

            var combinations = SectorForm.ValidSectorCombinations
                .Where(a => a.Length == sectorCount)
                .ToArray();

            SectorPlacement[] combination = randomize ?
                combinations.Random(random) : combinations.First();

            for (int i = 0; i < sectorCount; i++)
            {
                Sectors[i].Placement = combination[i];
            }
        }

        public object Clone()
        {
            return new Cluster
            {
                Id = Id,
                Dlc = Dlc,
                BackgroundVisualMapping = BackgroundVisualMapping,
                BaseGameMapping = BaseGameMapping,
                Soundtrack = Soundtrack,
                CustomSectorPositioning = CustomSectorPositioning,
                Hexagon = Hexagon,
                Name = Name,
                Position = Position,
                Description = Description,
                CustomClusterXml = CustomClusterXml,
                Sectors = Sectors.Select(a => (Sector)a.Clone()).ToList()
            };
        }

        public override string ToString()
        {
            return Name ?? "Unknown";
        }

        //A bit of self-awareness
        internal List<Gate> FindGates()
        {
            List<Gate> result = new List<Gate>();
            foreach (var sector in Sectors)
            {
                foreach (var zone in sector.Zones)
                {
                    result.AddRange(zone.Gates);
                }
            }
            return result;
        }

        internal List<cPoint> Contour
        {
            get
            {
                var left = Position.X - 1;
                var right = Position.X + 1;
                var upSide = Position.Y + 1;
                var downSide = Position.Y - 1;
                List<cPoint> set = new List<cPoint>()
                {
                    new Point(Position.X, Position.Y + 2),
                    new Point(Position.X, Position.Y - 2),
                };
                set.AddRange(Toolbox.Spread(3, 3, coord => new Point(left + coord.a, upSide - coord.b)).Select(p => (cPoint)p));
                return set;
            }
        }
    }

    public enum ClusterOption
    {
        Custom,
        Vanilla,
        Both
    }
}
