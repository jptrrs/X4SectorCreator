using System.Reflection;
using X4SectorCreator.Forms.Galaxy.Shuffler;
using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    internal class Territory : ClusterCollection
    {
        internal Point anchor = Point.Empty;
        internal List<int> annexedIds = [];
        internal List<int> closeColonyIds = [];
        internal List<cPoint> contour = [];
        internal string dlc;
        internal Direction exitDirection;
        internal List<Cluster> frontiers = [];
        internal int id, assignedDomainId;
        internal bool isBridge = false;
        internal bool? isVanilla;
        internal List<int> peers = [];
        internal Cluster seed;
        internal Point size = Point.Empty;
        private int[] box = new int[4];

        private (double x, double y) center;
        private List<Sector> destinations = [];
        private Dictionary<Sector, List<(Gate gate, Sector sector)>> exits = [];
        private bool overhead = false;

        internal Territory(Cluster seed, int lastID)
        {
            Clusters = [seed];
            id = lastID + 1;
            assignedDomainId = 0;
            this.seed = seed;
            dlc = seed.Dlc;
        }

        internal List<cPoint> Contour
        {
            get
            {
                if (!contour.Any())
                {
                    foreach (var cluster in Clusters)
                    {
                        contour.AddRangeUnique(cluster.Contour);
                    }
                }
                return contour;
            }
        }

        /// <summary>
        /// A list of connections to/from a territory.
        /// </summary>
        /// <param name="cluster">The system cluster inside the territory where's located.</param>
        /// <param name="origin">The specific sector qhere the gate departs from.</param>
        /// <param name="gate">The gate itself.</param>
        /// <param name="destination">The sector it connects to.</param>
        internal List<(Cluster cluster, Sector origin, Gate gate, Sector destination)> ExitPoints
        {
            get
            {
                var roads = new List<(Cluster, Sector, Gate, Sector)>();
                foreach (var cluster in frontiers)
                {
                    foreach (var sector in exits.Keys)
                    {
                        foreach (var exit in exits[sector])
                        {
                            roads.Add((cluster, sector, exit.gate, exit.sector));
                        }
                    }
                }
                return roads;
            }
            set
            {
                frontiers.Clear();
                exits.Clear();
                destinations.Clear();
                foreach (var item in value)
                {
                    if (item.cluster == null || item.origin == null || item.gate == null || item.destination == null) continue;
                    var cluster = item.Item1;
                    var sector = item.Item2;
                    var gate = item.Item3;
                    var dest = item.Item4;
                    frontiers.AddUnique(cluster);
                    destinations.AddUnique(dest);
                    if (exits.ContainsKey(sector))
                    {
                        exits[sector].AddUnique((gate, dest));
                    }
                    else
                    {
                        exits.Add(sector, new List<(Gate, Sector)> { (gate, dest) });
                    }
                }
            }
        }

        internal int HeightToFit => overhead ? size.Y + 1 : size.Y;

        internal bool IsVanilla
        {
            get
            {
                if (isVanilla == null)
                {
                    isVanilla = Clusters.Any(x => string.IsNullOrWhiteSpace(x.Dlc));
                }
                return (bool)isVanilla;
            }
        }

        internal string Reposition(Point displacement)
        {
            anchor = anchor.Add(displacement);
            List<string> log = new List<string>();
            foreach (var cluster in Clusters)
            {
                cluster.Position = anchor.Add(cluster.AnchorOffset);
                cluster.shuffled = true;
                log.Add($"{cluster.Name} {cluster.Position.ToTuple()}");
            }
            return $"Moving {Clusters.Count} clusters: {string.Join(", ", log.ToArray())}";
        }

        internal void Rotate(int turns)
        {
            foreach (var c in Clusters)
            {
                c.PlannedPosition = ClusterManager.RotateOrtho(c.Position, anchor, turns);
            }
            SetUpBox();
            List<string> afterRotate = Clusters.Select(c => c.PlannedPosition.ToTuple().ToString()).ToList();
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"#{id} - {seed.Name} rotated {turns * 90}°");
        }

        internal void SetUpBox()
        {
            var positions = Clusters.Select(c => c.PlannedPosition).ToList();
            var posX = positions.Select(p => p.X).ToList();
            var posY = positions.Select(p => p.Y).ToList();
            box[0] = posX.Max();
            box[1] = posY.Min();
            box[2] = posX.Min();
            box[3] = posY.Max();
            var width = box[0] - box[2] + 1;
            var height = box[3] - box[1] + 2;
            var corner = new Point(box[2], box[3]);
            anchor = corner.FitToHex();
            size = new Point(width, height);
            overhead = anchor.Y > box[3];
            double centerX = corner.X + (width - 1) / 2.0;
            double centerY = corner.Y - (height - 2) / 2.0;
            center = (centerX, centerY);
            SetUpClustersOffsets();
        }

        internal void SetUpClustersOffsets()
        {
            foreach (var cluster in Clusters)
            {
                cluster.AnchorOffset = cluster.PlannedPosition.Subtract(anchor);
            }
        }

        internal void SetUpDirection()
        {
            if (size.IsEmpty) SetUpBox();
            Direction exitDir = Direction.Undefined;
            if (size.X <= 1 && size.Y <= 2)
            {
                exitDirection = exitDir;
                return;
            }
            int voteUp = 0;
            int voteDown = 0;
            int voteRight = 0;
            int voteLeft = 0;
            List<Cluster> accountedFor = [];
            bool landLocked = destinations.Select(s => s.FindCluster()).All(c => peers.Contains(c.AssignedTerritoryId));
            var relevant = frontiers.Where(c => c.Destinations.Any(s => peers.Contains(s.AssignedTerritoryId) == landLocked));
            foreach (var c in relevant)
            {
                //Cluster position relative to its territory
                if (c.Position.X < center.x) voteLeft++;
                else if (c.Position.X > center.x) voteRight++;
                if (c.Position.Y > center.y) voteUp++;
                else if (c.Position.Y < center.y) voteDown++;

                //Destinations relative cluster position
                //The more destinations from a cluster, bigger weight given to this.
                foreach (var s in c.Destinations)
                {
                    if (peers.Contains(s.AssignedTerritoryId) != landLocked) continue;
                    var d = s.FindCluster();
                    if (accountedFor.Contains(d)) continue; //so we don't double-count
                    if (c.Position.X < d.Position.X) voteRight++;
                    else if (c.Position.X > d.Position.X) voteLeft++;
                    if (c.Position.Y > d.Position.Y) voteDown++;
                    else if (c.Position.Y < d.Position.Y) voteUp++;
                    accountedFor.Add(d);
                }
            }
            Direction vOption = exitDir;
            if (size.Y > 2)
            {
                if (voteUp > 0 && voteDown < voteUp) vOption = Direction.Up;
                if (voteDown > 0 && voteDown > voteUp) vOption = Direction.Down;
            }
            Direction hOption = exitDir;
            if (size.X > 1)
            {
                if (voteRight > 0 && voteRight > voteLeft) hOption = Direction.Right;
                if (voteLeft > 0 && voteRight < voteLeft) hOption = Direction.Left;
            }
            if (vOption == Direction.Undefined) exitDir = hOption;
            else if (hOption == Direction.Undefined) exitDir = vOption;
            else
            {
                var goV = voteUp + voteDown;
                var goH = voteLeft + voteRight;
                if (goV > goH) exitDir = vOption;
                else exitDir = hOption;
            }
            exitDirection = exitDir;
        }
    }
}