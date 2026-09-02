using System.Diagnostics.Metrics;
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
        internal List<Sector> Destinations = [];
        internal List<Sector> Exits = [];
        internal List<int> BridgeFor = [];
        internal Point AnchorOffset = Point.Empty;
        private Point? plannedPosition;

        [JsonIgnore]
        internal cPoint cPosition => (cPoint)Position;

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
        public bool IsBaseGame => !string.IsNullOrWhiteSpace(BaseGameMapping);

        [JsonIgnore]
        public List<(Sector origin, Gate gate, Sector destination)> ExitPoints
        {
            get
            {
                var roads = new List<(Sector, Gate, Sector)>();
                foreach (var sector in Exits)
                {
                    foreach (var exit in sector.Destinations)
                    {
                        roads.Add((sector, exit.Value, exit.Key));
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
                    if (item.origin == null || item.gate == null || item.destination == null) continue;
                    var sector = item.origin;
                    var gate = item.gate;
                    var dest = item.destination;
                    Destinations.AddUnique(dest);
                    Exits.AddUnique(sector);
                    sector.Destinations.TryAdd(dest, gate);
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

        internal string GetOwnerShip()
        {
            var ownerships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sector in Sectors)
            {
                ownerships.Add(sector.CurrentOwner);
            }
            return ownerships != null && ownerships.Count == 1 ? ownerships.First() : "";
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

        internal bool SameTerritoryAs(Cluster other)
        {
            if (other == null) return false;
            if (AssignedTerritoryId == -1 || other.AssignedTerritoryId == -1) return false;
            return AssignedTerritoryId == other.AssignedTerritoryId;
        }

        internal void FollowUpRotation(int turns)
        {
            bool doPlacement = Sectors.Count > 1;
            foreach (Sector sector in Sectors)
            {
                if (doPlacement) sector.RotatePlacementOrtho(turns);
                foreach (Zone zone in sector.Zones)
                {
                    zone.Position = ClusterManager.RotateOrtho(zone.Position, Point.Empty, turns);
                    zone.Gates.ForEach(gate => gate.UpdateFacing(turns));
                }
                foreach (Region region in sector.Regions)
                {
                    region.Position = ClusterManager.RotateOrtho(region.Position, Point.Empty, turns);
                }
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
