using System.Reflection;
using X4SectorCreator.Forms.Galaxy.Shuffler;
using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    internal class Territory : ClusterCollection
    {
        internal List<int> annexedIds = [], closeColonyIds = [], peers = [];
        internal List<cPoint> contour = [];
        internal string dlc;
        internal Direction exitDirection;
        internal List<Cluster> bordering = [];
        internal int id, assignedDomainId;
        internal bool isBridge = false, origin = false, toMerge = false, unconnected = false;
        internal Cluster seed;
        internal Point size = Point.Empty;
        internal List<Cluster> absorbedExits = [];
        internal HashSet<int> neighbors = [];
        
        private int[] box = new int[4];
        private bool? isNeutral, isVanilla, sameOwner, landlocked;
        private bool overhead = false;
        private Point anchor = Point.Empty;
        private (double x, double y) center;
        private HashSet<Cluster> exitClusters = [];
        private List<(Cluster cluster, Sector origin, Gate gate, Sector destination)> connections = [];

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

        internal Point Anchor
        {
            get
            {
                if (!origin && anchor.IsEmpty) SetUpBox();
                return anchor;
            }
            set
            {
                anchor = value;
            }
        }

        /// <summary>
        /// A list of connections to/from a territory.
        /// </summary>
        /// <param name="cluster">The system cluster inside the territory where's located.</param>
        /// <param name="origin">The specific sector qhere the gate departs from.</param>
        /// <param name="gate">The gate itself.</param>
        /// <param name="destination">The sector it connects to.</param>
        internal List<(Cluster cluster, Sector origin, Gate gate, Sector destination)> Connections
        { 
            get
            {
                if (connections.Count == 0 && !unconnected) SetUpConnections();
                return connections;
            } 
        }

        internal void SetUpConnections()
        {
            bordering.Clear();
            connections.Clear();
            foreach (var cluster in Clusters)
            {
                cluster.ExitPoints = ClusterManager.PickDestinationsFromCluster(cluster, c => c != cluster);
                foreach (var exit in cluster.ExitPoints.Where(x => x.destination.AssignedTerritoryId != id))
                {
                    connections.Add((cluster, exit.origin, exit.gate, exit.destination));
                    bordering.AddUnique(cluster);
                    neighbors.Add(exit.destination.AssignedTerritoryId);
                }
            }
            if (connections.Count == 0) unconnected = true;
        }

        internal HashSet<Cluster> ExitClusters //This is persistent once set for the 1st time.
        {
            get
            {
                if (exitClusters.Count == 0)
                {
                    if (Connections != null)
                    {
                        exitClusters = Connections?.Select(x => x.cluster).ToHashSet();
                    }
                }
                return exitClusters;
            }
        }

        internal HashSet<Gate> ExitGates
        {
            get
            {
                return Connections?.Select(x => x.gate).ToHashSet();
            }
        }

        internal int HeightToFit => overhead ? size.Y + 1 : size.Y;

        internal bool IsNeutral
        {
            get
            {
                if (isNeutral == null)
                {
                    isNeutral = Clusters.All(c => c.Sectors.All(s => s.IsNeutral));
                }
                return (bool)isNeutral;
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
                return (bool)isVanilla;
            }
        }

        // Determines if all clusters in the territory belong tp the same owner, ignoring vacant clusters and disputes with the Xenon. Xenon-only territories return positive, though.
        internal bool SameOwner
        {
            get
            {
                if (sameOwner == null)
                {
                    var ownerships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cluster in Clusters)
                    {
                        var owner = cluster.GetOwnerShip();
                        if (string.IsNullOrEmpty(owner) || owner.Equals("None")) continue;
                        ownerships.Add(owner.ToLower());
                    }
                    bool allSameOwner = ownerships != null && ownerships.Count == 1;
                    bool xenonInvading = !allSameOwner && ownerships.Count == 2 && ownerships.Contains("xenon");
                    sameOwner = allSameOwner || xenonInvading;
                }
                return (bool)sameOwner;
            }
        }

        internal bool Landlocked
        {
            get
            {
                if (landlocked == null)
                {
                    landlocked = Connections.Count > 0 && Connections.All(x => x.origin.CurrentOwner.Equals(x.destination.CurrentOwner, StringComparison.OrdinalIgnoreCase));
                }
                return (bool)landlocked;
            }
        }

        internal string Reposition(Point displacement)
        {
            Anchor = Anchor.Add(displacement);
            List<string> log = new List<string>();
            foreach (var cluster in Clusters)
            {
                cluster.Position = Anchor.Add(cluster.AnchorOffset);
                cluster.shuffled = true;
                log.Add($"{cluster.Name} {cluster.Position.ToTuple()}");
            }
            return $"Anchored @ {Anchor.ToTuple()}, moving {Clusters.Count} clusters: {string.Join(", ", log.ToArray())}";
        }

        internal void Rotate(int turns)
        {
            foreach (var c in Clusters)
            {
                c.PlannedPosition = ClusterManager.RotateOrthoOnHexGrid(c.Position, Anchor, turns);
                c.FollowUpRotation(turns);
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
            SetUpClustersOffsets(anchor);
        }

        internal void SetUpClustersOffsets(Point origin)
        {
            foreach (var cluster in Clusters)
            {
                cluster.AnchorOffset = cluster.PlannedPosition.Subtract(origin);
            }
        }

        internal void SetUpDirection(bool restricted = false)
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
            //Unless restricted, take into account only clusters connected to outside of the domain.
            var relevant = peers.Count > 0 ? bordering.Where(c => c.Destinations.Any(s => peers.Contains(s.AssignedTerritoryId) == restricted)) : bordering;
            foreach (var c in relevant)
            {
                //localized results
                int cVoteUp = 0;
                int cVoteDown = 0;
                int cVoteRight = 0;
                int cVoteLeft = 0;

                //Cluster position relative to its territory
                if (c.Position.X < center.x) cVoteLeft++;
                else if (c.Position.X > center.x) cVoteRight++;
                if (c.Position.Y > center.y) cVoteUp++;
                else if (c.Position.Y < center.y) cVoteDown++;

                //Destinations relative cluster position
                //The more destinations from a cluster, bigger weight given to this.
                foreach (var s in c.Destinations)
                {
                    if (peers.Contains(s.AssignedTerritoryId) != restricted) continue;
                    var d = s.Parent;
                    if (accountedFor.Contains(d)) continue; //so we don't double-count
                    if (c.Position.X < d.Position.X) cVoteRight++;
                    else if (c.Position.X > d.Position.X) cVoteLeft++;
                    if (c.Position.Y > d.Position.Y) cVoteDown++;
                    else if (c.Position.Y < d.Position.Y) cVoteUp++;
                    accountedFor.Add(d);
                }

                //Register the cluster vocation for later.
                c.Direction = (int)ResolveDirection(cVoteUp, cVoteDown, cVoteRight, cVoteLeft, true, true);

                //Transfer votes for the overall direction.
                voteUp += cVoteUp;
                voteDown += cVoteDown;
                voteRight += cVoteRight;
                voteLeft += cVoteLeft;
            }
            bool checkVertical = size.Y > 2;
            bool checkHorizontal = size.X > 1;

            exitDirection = ResolveDirection(voteUp, voteDown, voteRight, voteLeft, checkVertical, checkHorizontal);
        }

        private Direction ResolveDirection(int voteUp, int voteDown, int voteRight, int voteLeft, bool checkVertical, bool checkHorizontal)
        {
            var result = Direction.Undefined;
            var vOption = Direction.Undefined;
            var hOption = Direction.Undefined;
            if (checkVertical)
            {
                if (voteUp > 0 && voteDown < voteUp) vOption = Direction.Up;
                if (voteDown > 0 && voteDown > voteUp) vOption = Direction.Down;
            }
            if (checkHorizontal)
            {
                if (voteRight > 0 && voteRight > voteLeft) hOption = Direction.Right;
                if (voteLeft > 0 && voteRight < voteLeft) hOption = Direction.Left;
            }
            if (vOption == Direction.Undefined) result = hOption;
            else if (hOption == Direction.Undefined) result = vOption;
            else
            {
                var goV = voteUp + voteDown;
                var goH = voteLeft + voteRight;
                if (goV > goH) result = vOption;
                else result = hOption;
            }
            return result;
        }

        internal void Absorb(Territory other)
        {
            if (other == null) return;
            var clusters = other.Clusters.ToList();
            absorbedExits.AddRange(other.ExitClusters);
            foreach (var cluster in clusters)
            {
                Clusters.Add(cluster);
                cluster.AssignedTerritoryId = id;
            }
            SetUpConnections();
        }
    }
}