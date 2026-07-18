using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    internal class Territory : ClusterCollection
    {
        internal Cluster Seed;
        internal string Dlc;
        internal int Id, AssignedDomainId;
        internal List<Cluster> Frontiers = new List<Cluster>();
        internal List<Sector> Destinations = new List<Sector>();
        internal Dictionary<Sector, List<(Gate, Sector)>> Exits = new Dictionary<Sector, List<(Gate, Sector)>>();
        internal List<int> connectedIds = new List<int>();
        internal List<int> annexedIds = new List<int>();
        internal List<int> closeColonyIds = new List<int>();
        internal List<cPoint> contour = new List<cPoint>();
        internal int[] box = new int[4];
        internal Point Size = new Point();
        internal Point Anchor = new Point();
        internal Point Corner = new Point();

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
            }
        }

        internal Territory(Cluster seed, int lastID)
        {
            Clusters = [seed];
            Id = lastID + 1;
            AssignedDomainId = 0;
            Seed = seed;
            Dlc = seed.Dlc;
        }

        internal void SetUpBox()
        {
            var positions = Clusters.Select(c => c.Position).ToList();
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
            Size = new Point(width,height);
            overhead = Anchor.Y > box[3];
            //int centerX = width / 2;
            //int centerY = height / 2;
            //Center = new Point(centerX,centerY);
            SetUpClustersOffsets();
        }

        private bool overhead = false;
        internal int HeightToFit => overhead ? Size.Y + 1 : Size.Y;


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

        internal float SizeToContentRatio()
        {
            var area = Size.X * Size.Y;
            return area / Clusters.Count;
        }

        internal void SetUpClustersOffsets()
        {
            foreach (var cluster in Clusters)
            {
                cluster.AnchorOffset = cluster.Position.Subtract(Anchor);
            }

        }

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
    }
}
