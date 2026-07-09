using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;

namespace X4SectorCreator.Forms.Galaxy.ProceduralGeneration
{
    public enum Direction
    {
        Undefined = 0,
        Right = 1,
        Down = 2,
        Left = 3,
        Up = 4,
    }

    internal class Shuffler
    {
        internal const int gap = 2;
        internal Point occupiedMax;
        internal Dictionary<int, Territory> territories = [];
        internal List<Cluster> misplaced = []; 

        private static readonly (int dx, int dy)[] NeighborOffsets =
        [
            (0,  2),
            (0, -2),
            (1,  1),
            (1, -1),
            (-1, 1),
            (-1,-1),
        ];

        private static (int cols, int rows) hexGridFrame;

        private readonly Func<(Cluster, Cluster), bool> AreConnected = (pair) =>
        {
            bool flag = false;
            var clusterA = pair.Item1;
            var clusterB = pair.Item2;
            foreach (var gate in clusterA.FindGates())
            {
                gate.FindDestination(out Cluster focused);
                flag = focused.Equals(clusterB);
                if (flag) break;
            }
            return flag;
        };

        private readonly Func<Cluster, List<Point>> GetNeighbors = cluster =>
        {
            var pos = cluster.Position;
            return NeighborOffsets
                .Select(off => new Point(pos.X + off.dx, pos.Y + off.dy))
                .ToList();
        };

        private readonly Func<Territory, Cluster, bool> IsOutside = (territory, cluster) =>
        {
            return !territory.Clusters.Contains(cluster);
        };

        private Direction helixLastDir = Direction.Up;

        private Func<Point, bool> InBounds = p =>
        {
            var absX = Math.Abs(p.X);
            var absY = Math.Abs(p.Y);
            return absX <= GridFrameBounds.maxX && absY <= GridFrameBounds.maxY;
        };

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            // Gather some basic info
            hexGridFrame = ClusterManager.FrameHexGrid(clusters.ToList());
            // Group clusters into territories based adjacency and DLCs
            CarveTerritories(clusters);
            // Map connections for all clusters and register entry points for territories
            FindConnections();
            // Determine if there are neighboring territories owned by the same faction
            FindAnnexed();
            // Determine if there are other close territories owned by the same faction and separated by only a neutral sector.
            FindCloseColonies();
            // Shuffle!
            Shuffle();
            // Update Map as needed.
            if (MainForm.Instance.SectorMap.IsInitialized) MainForm.Instance.SectorMap.Value.Reset();
        }

        internal int vertGap => gap * 2;
        private static (int maxX, int maxY) GridFrameBounds => (hexGridFrame.cols / 2, hexGridFrame.rows / 2);

        private Func<Cluster, bool> DLCMatch => cluster =>
        {
            return cluster.Dlc == territories.Last().Value.Dlc;
        };

        private Action<Cluster, bool> SortTerritory => (Cluster cluster, bool reset) =>
        {
            if (reset)
            {
                var newTerritory = new Territory(cluster, territories.Count);
                territories.Add(newTerritory.Id, newTerritory);
                cluster.AssignedTerritoryId = newTerritory.Id;
                return;
            }
            var territory = territories.Last().Value;
            territory.Clusters.Add(cluster);
            cluster.AssignedTerritoryId = territory.Id;
        };

        public static (int dx, int dy)[] NeighborOffsets1 => NeighborOffsets;

        internal void CarveTerritories(IEnumerable<Cluster> clusters)
        {
            var log = new List<string>();
            ClusterManager.Group(clusters, SortTerritory, x => GetNeighbors(x), x => DLCMatch(x), x => AreConnected(x));
            foreach (var territory in territories.Values)
            {
                territory.SetUpBox();
                log.Add($"\n{territory.Id} - {territory.Seed.Name}, {territory.Size.X}x{territory.Size.Y} with {territory.Clusters.Count} clusters");
            }
            //logging
            _ = LogAsync("CarveTerritories", $"\n\n--- Territories ---\n{string.Join("",log)}");
        }

        internal void FindAnnexed()
        {
            foreach (var territory in territories.Values)
            {
                if (territory.ExitPoints?.Count == 0) continue;
                foreach (var entry in territory.ExitPoints)
                {
                    var origin = entry.Item2;
                    var gate = entry.Item3;
                    var destination = entry.Item4;
                    var foundId = destination.AssignedTerritoryId;
                    if (foundId > 0)
                    {
                        if (territory.annexedIds.Contains(foundId)) continue;
                        if (origin.Owner != null && origin.Owner.Equals(destination.Owner, StringComparison.Ordinal))
                        {
                            territory.annexedIds.AddUnique(foundId);
                            territories[foundId].annexedIds.AddUnique(territory.Id);
                        }
                    }
                }
            }
        }

        internal void FindCloseColonies()
        {
            var candidates = territories.Values
                .SelectMany(t => t.Frontiers)
                .Where(c => c.ExitPoints?.Count > 1 && c.Exits.Keys.All(y => y.IsNeutral))
                .ToList();
            foreach (var cluster in candidates)
            {
                var connected = cluster.Destinations
                    .Where(s => !s.IsNeutral)
                    .GroupBy(s => s.Owner)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Select(s => territories[s.AssignedTerritoryId]))
                    .ToList();
                if (!connected.Any()) continue;
                foreach (var grouped in connected)
                {
                    foreach (var neighbor in grouped)
                    {
                        neighbor.closeColonyIds.AddRangeUnique(grouped.Except([neighbor]).Select(x => x.Id));
                    }
                    cluster.BridgeFor.AddRange(grouped.Select(x => x.Id).ToArray());
                }
            }
        }

        internal void FindConnections()
        {
            foreach (var territory in territories.Values)
            {
                var roads = new List<(Cluster, Sector, Gate, Sector)>();
                foreach (var cluster in territory.Clusters)
                {
                    cluster.ExitPoints = ClusterManager.PickDestinationsFromCluster(cluster, c => c != cluster);
                    foreach (var exit in cluster.ExitPoints.Where(x => x.destination.AssignedTerritoryId != territory.Id))
                    {
                        roads.Add((cluster, exit.origin, exit.gate, exit.destination));
                    }
                }
                territory.ExitPoints = roads;
            }
        }

        internal void Shuffle()
        {
            //logging
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"\n\n--- Shuffling ---",true);

            //No turning back now!
            MainForm.Instance.AllClusters.Clear();

            List<int> cards = territories.Keys.ToList();
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(cards));
            var slots = new OrderedDictionary<Point, (Direction root, Direction dir)>() { [new Point(0, 0)] = (Direction.Undefined, Direction.Undefined) };
            var deferred = new OrderedDictionary<Point, (Direction root, Direction dir)>();
            SortedSet<cPoint> occupied = new SortedSet<cPoint>();
            for (int i = 0; i < cards.Count(); i++)
            {
                //Select next territory and pick the next slot.
                int card = cards[i];
                var territory = territories[card];
                var currentPos = territory.Anchor;
                if (!slots.Any())
                {
                    if (deferred.Any())
                    {
                        slots = deferred;
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Bounds reached, spilling over @ #{territory.Id} - {territory.Seed.Name}...");
                    }
                    else
                    {
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: we've run out of slots! #{territory.Id} - {territory.Seed.Name} and beyond can't be placed...", true);
                        break;
                    }
                }
                var slot =  slots.First();
                var newPos = slot.Key;
                var locDir = slot.Value.dir;
                var rootDir = slot.Value.root;
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Assigning #{territory.Id} - {territory.Seed.Name}, size=({territory.Size.ToTuple()}, slot @ {newPos.ToTuple()}{locDir}...", true);

                //Fine-tune the insertion spot so it fits right in.
                var plannedMove = newPos.Subtract(currentPos);
                if (i > 0) newPos = AdjustForInsertionHelix(territory, plannedMove, rootDir, locDir, occupied);

                //Move the piece
                var move = newPos.FitToHex().Subtract(currentPos);
                var report = territory.Reposition(move);
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Moved #{territory.Id} to {newPos.ToTuple()}...");

                //Keep track of occupied areas
                var covered = FillArea(territory);
                if (i == 0) occupied.Clear();
                occupied.UnionWith(covered);
                occupiedMax = occupied.Max;
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{covered.Count} tiles were covered, totalling {occupied.Count} now...");

                //Update the board.
                UpdateClusterMap(territory.Clusters, i);
                
                //Prepare the next slots.
                slots.RemoveAt(0);
                if (i > 0) CleanArea(covered, ref slots);
                var nextSlots = NextSlotsHelix(territory, rootDir, occupied);
                foreach (var (pos, root, dir) in nextSlots)
                {
                    if (InBounds(pos)) slots.Add(pos, (root, dir));
                    else deferred.Add(pos, (root, dir));
                }
            }
            HandleMisplaced();
        }

        private static void CleanArea(List<cPoint> covered, ref OrderedDictionary<Point, (Direction root, Direction dir)> slots)
        {
            List<(Point pos, Direction root, Direction dir)> badSlots = slots.Where(x => covered.Contains(x.Key)).Select(x => (x.Key, x.Value.root, x.Value.dir)).ToList();
            if (badSlots.Count > 0)
            {
                _ = LogAsync("CleanArea", $"{badSlots.Count} slots were covered and must be removed: {string.Join(", ", badSlots.Select(x => $"{x.pos.ToTuple()}{x.dir} (branch: {x.root})"))}.");
                foreach (var s in badSlots)
                {
                    slots.Remove(s.pos);
                }
            }
        }

        private static async Task LogAsync(string level, string message, bool lineSkip = false)
        {
            string jump = lineSkip ? "\n" : "";
            string logEntry = $"{jump}[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync("test.log", logEntry);
        }

        private Point AdjustForInsertionHelix(Territory territory, Point displacement, Direction root, Direction dir, SortedSet<cPoint> occupied)
        {
            var position = territory.Anchor.Add(displacement);
            var width = territory.Size.X;
            var oddHeight = territory.Size.Y;
            var height = oddHeight + oddHeight % 2;
            var flipX = width - 1;
            var flipY = height - 2;
            var offset = new Point();
            var hits = Enumerable.Empty<Point>();
            int push = 0;

            switch (root)
            {
                case Direction.Undefined:
                case Direction.Right:
                    offset = position; // Anchor = slot
                    hits = Spread(width, height, coord => new Point(position.X + coord.a, position.Y - coord.b), p => occupied.Contains(p)); // Scan right/down
                    break;

                case Direction.Down:
                    offset = new Point(position.X - flipX, position.Y); // Anchor to the right
                    hits = Spread(width, height, coord => new Point(position.X - coord.a, position.Y - coord.b), p => occupied.Contains(p)); // Scan left/down
                    break;

                case Direction.Left:
                    offset = new Point(position.X - flipX, position.Y + flipY); // Anchor opposite to slot.
                    hits = Spread(width, height, coord => new Point(position.X - coord.a, position.Y + coord.b), p => occupied.Contains(p)); // Scan left/up
                    break;

                case Direction.Up:
                    offset = new Point(position.X, position.Y + flipY); // Anchor at the bottom
                    hits = Spread(width, height, coord => new Point(position.X + coord.a, position.Y + coord.b), p => occupied.Contains(p)); // Scan right/up
                    break;
            }
            if (hits.Any())
            {
                // Move into the appropriate direction
                switch (dir)
                {
                    case Direction.Undefined:
                    case Direction.Right:
                        push = hits.MaxBy(p => p.X).X;
                        offset = new Point(offset.X + push, offset.Y);
                        break;

                    case Direction.Down:
                        push = hits.MaxBy(p => p.Y).Y;
                        offset = new Point(position.X, position.Y - push);
                        break;

                    case Direction.Left:
                        push = hits.MaxBy(p => p.X).X;
                        offset = new Point(offset.X - push, offset.Y);
                        break;

                    case Direction.Up:
                        push = hits.MaxBy(p => p.Y).Y;
                        offset = new Point(offset.X, offset.Y + push);
                        break;
                }
            }
            offset = offset.FitToHex();
            if (push > 0) _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"#{territory.Id} was pushed {push} tiles {dir}, to {offset.ToTuple()}.");
            return offset;
        }

        private List<cPoint> FillArea(Territory territory)
        {
            var width = territory.Size.X + gap;
            var height = territory.Size.Y + vertGap;
            return Spread(width, height,
                coord => new Point(territory.Anchor.X - (gap / 2) + coord.a, territory.Anchor.Y + (vertGap / 2) - coord.b).FitToHex())
                .Select(x => (cPoint)x).ToList();
        }

        private List<(Point pos, Direction rootDir, Direction dir)> NextSlotsHelix(Territory territory, Direction rootDir, SortedSet<cPoint> occupied)
        {
            var ax = territory.Anchor.X;
            var ay = territory.Anchor.Y;
            var width = territory.Size.X - 1;
            var oddHeight = territory.Size.Y;
            var height = oddHeight + oddHeight % 2 - 2;
            bool setRoot = rootDir == Direction.Undefined ? true : false;
            bool firstRun = rootDir == Direction.Undefined;
            bool quadrant = rootDir != helixLastDir;

            var slots = new List<(Point pos, Direction root, Direction dir)>();
            //logging
            List<string> log = new List<string>();
            string level = MethodBase.GetCurrentMethod().Name;
            void Select((Point slot, Direction root, Direction dir) set)
            {
                if (!occupied.Contains(set.slot))
                {
                    slots.Add(set);
                    log.Add($"{set.slot.ToTuple().ToString()}{set.dir}");
                }
                else
                {
                    _ = LogAsync(level, $"{set.slot.ToTuple()}{set.dir} was occupied, slot skipped! Branch: {set.root})");
                }
            }

            //Place future slots, in clockwise order
            if (rootDir == Direction.Right || firstRun)
            {
                if (quadrant) Select((new Point(ax + width + gap, ay).FitToHex(), setRoot ? Direction.Right : rootDir, Direction.Right));
                if (!firstRun) Select((new Point(ax, ay - height - vertGap).FitToHex(), rootDir, Direction.Down));
            }
            if (rootDir == Direction.Down || firstRun)
            {
                if (quadrant) Select((new Point(ax + width, ay - height - vertGap).FitToHex(), setRoot ? Direction.Down : rootDir, Direction.Down));
                if (!firstRun) Select((new Point(ax - gap, ay), rootDir, Direction.Left));
            }
            if (rootDir == Direction.Left || firstRun)
            {
                if (quadrant) Select((new Point(ax - gap, ay - height).FitToHex(), setRoot ? Direction.Left : rootDir, Direction.Left));
                if (!firstRun) Select((new Point(ax + width, ay + vertGap).FitToHex(), rootDir, Direction.Up));
            }
            if (rootDir == Direction.Up || firstRun)
            {
                if (quadrant) Select((new Point(ax, ay + vertGap).FitToHex(), setRoot ? Direction.Up : rootDir, Direction.Up));
                if (!firstRun) Select((new Point(ax + width + gap, ay - height).FitToHex(), rootDir, Direction.Right));
            }
            if (slots.Count() == 0)
            {
                _ = LogAsync(level, $"No Slots found for #{territory.Id}! Branch: {rootDir})");
            }
            helixLastDir = rootDir;
            _ = LogAsync(level, $"Slots around #{territory.Id}: {string.Join(", ", log)} (branch: {rootDir}).");
            return slots.ToList();
        }

        private IEnumerable<Point> Spread(int limitA, int limitB, Func<(int a, int b), Point> form, Predicate<Point> filter = null, bool filtered = false)
        {
            for (var a = 0; a < limitA; a++)
            {
                for (var b = 0; b < limitB; b++)
                {
                    var p = form((a, b));
                    if (filter == null) yield return p;
                    else if (filter(p)) yield return filtered ? p : new Point(a, b);
                }
            }
        }

        private void UpdateClusterMap(List<Cluster> clusters, int errorY = 0)
        {
            foreach (var c in clusters)
            {
                if (!MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = LogAsync("UpdateClusterMap", $"Error placing {c.Name} @ {c.Position.ToTuple()}, set aside...");
                    misplaced.Add(c);
                }
            }
        }

        private void HandleMisplaced()
        {
            if (misplaced.Count == 0) return;
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{misplaced.Count} clusters were misplaced and will be set aside...", true);
            int y = 1;
            foreach (var c in misplaced)
            {
                c.Position = occupiedMax.Add(new Point(gap, y * vertGap - vertGap)).FitToHex();
                y++;
                if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{c.Name}, territory #{c.AssignedTerritoryId}, placed @ ({c.Position.ToTuple()}).");
                }
                else
                {
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: couldn't place {c.Name}, territory #{c.AssignedTerritoryId}, anywhere! Last attempt: {c.Position.ToTuple()}.");
                }
            }
        }
    }
}