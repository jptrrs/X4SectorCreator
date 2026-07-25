using System.Reflection;
using X4SectorCreator.Forms.Galaxy.Shuffler;
using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    internal class Territory : ClusterCollection
    {
        internal Point Anchor = Point.Empty;
        internal List<int> annexedIds = [];
        internal int[] box = new int[4];
        internal List<int> closeColonyIds = [];
        internal List<int> connectedIds = [];
        internal List<cPoint> contour = [];
        internal (double x, double y) Center;
        internal Point Corner = Point.Empty;
        internal List<Sector> Destinations = [];
        internal string Dlc;
        internal Dictionary<Sector, List<(Gate, Sector)>> Exits = [];
        internal List<Cluster> Frontiers = [];
        internal int Id, AssignedDomainId;
        internal bool IsBridge = false;
        internal bool? isVanilla;
        internal Cluster Seed;
        internal Point Size = Point.Empty;
        internal Direction EntryDirection;
        
        private bool overhead = false;

        internal Territory(Cluster seed, int lastID)
        {
            Clusters = [seed];
            Id = lastID + 1;
            AssignedDomainId = 0;
            Seed = seed;
            Dlc = seed.Dlc;
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
                foreach (var cluster in Frontiers)
                {
                    foreach (var sector in Exits.Keys)
                    {
                        foreach (var exit in Exits[sector])
                        {
                            roads.Add((cluster, sector, exit.Item1, exit.Item2));
                        }
                    }
                }
                return roads;
            }
            set
            {
                Frontiers.Clear();
                Exits.Clear();
                Destinations.Clear();
                foreach (var item in value)
                {
                    if (item.Item1 == null || item.Item2 == null || item.Item3 == null || item.Item4 == null) continue;
                    var cluster = item.Item1;
                    var sector = item.Item2;
                    var gate = item.Item3;
                    var dest = item.Item4;
                    Frontiers.AddUnique(cluster);
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
                SetUpDirection();
            }
        }

        internal bool IsVanilla
        {
            get
            {
                if (isVanilla == null)
                {
                    isVanilla = Clusters.Any(x => string.IsNullOrWhiteSpace(x.Dlc));
                }
                return (bool) isVanilla;
            }
        }

        internal int HeightToFit => overhead ? Size.Y + 1 : Size.Y;

        internal string Reposition(Point displacement)
        {
            Anchor = Anchor.Add(displacement);
            List<string> list = new List<string>();
            foreach (var cluster in Clusters)
            {
                cluster.Position = Anchor.Add(cluster.AnchorOffset);
                cluster.shuffled = true;
                list.Add($"{cluster.Name}({cluster.Position.ToTuple()})");
            }
            string report = $"\n{Seed.Name} moved to {Anchor.ToTuple()}: {string.Join(", ", list.ToArray())}";
            return report;
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
            Corner = new Point(box[2], box[3]);
            Anchor = Corner.FitToHex();
            Size = new Point(width, height);
            overhead = Anchor.Y > box[3];
            double centerX = Anchor.X + (Size.X - 1) / 2.0;
            double centerY = Anchor.Y - (Size.Y - 2) / 2.0;
            Center = (centerX, centerY);
            SetUpClustersOffsets();
        }

        internal void SetUpClustersOffsets()
        {
            foreach (var cluster in Clusters)
            {
                cluster.AnchorOffset = cluster.PlannedPosition.Subtract(Anchor);
            }
        }

        internal void SetUpDirection()
        {
            if (Size.IsEmpty) SetUpBox();
            Direction exitDir = Direction.Undefined;
            if (Size.X <= 1 && Size.Y <= 2)
            {
                EntryDirection = exitDir;
                return;
            }
            int voteUp = 0;
            int voteDown = 0;
            int voteRight = 0;
            int voteLeft = 0;
            foreach (var cluster in Frontiers)
            {
                if (cluster.Position.X < Center.x) voteLeft++;
                else if (cluster.Position.X > Center.x) voteRight++;
                if (cluster.Position.Y > Center.y) voteUp++;
                else if (cluster.Position.Y < Center.y) voteDown++;
            }
            Direction vOption = exitDir;
            if (Size.Y > 2)
            {
                if (voteUp > 0 && voteDown < voteUp) vOption = Direction.Up;
                if (voteDown > 0 && voteDown > voteUp) vOption = Direction.Down;
            }
            Direction hOption = exitDir;
            if (Size.X > 1)
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
            EntryDirection = exitDir.OppositeDir();
        }

        internal float SizeToContentRatio()
        {
            var area = Size.X * Size.Y;
            return area / Clusters.Count;
        }

        internal void Rotate(int turns)
        {
            foreach (var c in Clusters)
            {
                c.PlannedPosition = ClusterManager.RotateOrtho(c.Position, Center.x, Center.y, turns);
            }
            SetUpBox();
            List<string> afterRotate = Clusters.Select(c => c.PlannedPosition.ToTuple().ToString()).ToList();
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"#{Id}-{Seed.Name} rotated {turns * 90}° (from {EntryDirection})");
        }
    }
}