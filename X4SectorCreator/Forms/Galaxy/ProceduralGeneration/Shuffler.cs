using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;
using static X4SectorCreator.Objects.Constructionplan;

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
        internal List<Cluster> misplaced = [];
        internal Point occupiedMax;
        internal Dictionary<int, Territory> territories = [];
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
        private float helixGeneration = 0;

        private Func<Point, bool> InBounds = p =>
        {
            var absX = Math.Abs(p.X);
            var absY = Math.Abs(p.Y);
            return absX <= GridFrameBounds.maxX && absY <= GridFrameBounds.maxY;
        };

        private Point Drift(Point position, out Direction dir, out Point vector)
        {
            //Push the position (pseudo)clockwise based on the distance to the center.

            //Escape if edge case
            if (position.IsEmpty || position.X == position.Y)
            {
                dir = Direction.Undefined;
                vector = new Point();
                return position;
            }

            //Bases
            float limit = GridFrameBounds.maxY; //not bothering with X because the maps fits a horizontal rectangle.
            var maxDrift = 2;
       
            //Smoothstep easing for values near zero to remain zero while larger values reach maxDrift
            float normY = Math.Clamp(Math.Abs(position.Y) / limit, 0f, 1f);
            float easedY = normY * normY * (3f - 2f * normY); // smoothstep
            float scaledY = easedY * maxDrift;
            int moveX = scaledY < 0.5f ? 0 : (int)MathF.Round(scaledY);
            moveX = Math.Min(maxDrift, moveX);

            float normX = Math.Clamp(Math.Abs(position.X) / limit, 0f, 1f);
            float easedX = normX * normX * (3f - 2f * normX);
            float scaledX = easedX * maxDrift;
            int moveY = scaledX < 0.5f ? 0 : (int)MathF.Round(scaledX);
            moveY = Math.Min(maxDrift, moveY);

            dir = GetDriftDirection(position);
            vector = GetDriftVector(position, dir, moveX, moveY);
            var newPos = position.Add(vector);
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{position.ToTuple()} drifted to {newPos.ToTuple()}, limit={limit} normalX={normX} normalY={normY} numX={moveX}, numY={moveY}");
            return newPos;
        }

        private Direction GetDriftDirection(Point position)
        {
            //Direction is based on position, 45 degrees quadrants.
            if (position.Y > position.X && position.Y > -position.X) //Above: move right then down
            {
                return Direction.Right;
            }
            else if (position.Y < position.X && position.Y < -position.X) //Below: move left then up
            {
                return Direction.Left;
            }
            else if (position.X > position.Y && position.X > -position.Y) //Right: move down then left
            {
                return Direction.Down;
            }
            else if (position.X < position.Y && position.X < -position.Y) //Left: move up then right
            {
                return Direction.Up;
            }
            return Direction.Undefined;
        }

        private Point GetDriftVector(Point position, Direction dir, int moveX, int offsetY)
        {
            int yaw = 0;
            switch (dir)
            {
                case Direction.Right: //Above: move right then down
                    if (position.X > 0) yaw = offsetY;
                    return new Point(moveX, - yaw).FitToHex(Direction.Down);
                case Direction.Left: //Below: move left then up
                    if (position.X < 0) yaw = offsetY;
                    return new Point(- moveX, yaw).FitToHex();
                case Direction.Down: //Right: move down then left
                    if (position.Y < 0) yaw = moveX;
                    return new Point(- yaw, - offsetY).FitToHex(Direction.Down);
                case Direction.Up: //Left: move up then right
                    if (position.Y > 0) yaw = moveX;
                    return new Point(yaw, offsetY).FitToHex();
                case Direction.Undefined:
                    goto Fail;
            }
            Fail:
            return new Point(0, 0);
        }

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            // Gather some basic info
            hexGridFrame = ClusterManager.FrameHexGrid(clusters.ToList());
            _ = LogAsync("Initializing", $"cols = {hexGridFrame.cols} x rows = {hexGridFrame.rows}");

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
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"\n\n--- Territories ---\n{string.Join("",log)}");
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
                var pos = slot.Key;
                var newPos = pos;
                var locDir = slot.Value.dir;
                var rootDir = slot.Value.root;
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Assigning #{territory.Id} - {territory.Seed.Name}, size=({territory.Size.ToTuple()}, slot @ {newPos.ToTuple()}{locDir}...", true);

                //Fine-tune the insertion spot so it fits right in.
                var plannedMove = newPos.Subtract(currentPos);
                if (i > 0) newPos = AdjustForInsertion(territory, plannedMove, rootDir, locDir, occupied);

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
                var nextSlots = NextSlotsHelix(pos, territory, rootDir, occupied);
                foreach (var (p, root, dir) in nextSlots)
                {
                    if (InBounds(p)) slots.TryAdd(p, (root, dir));
                    else deferred.TryAdd(pos, (root, dir));
                }
            }
            HandleMisplaced();
        }

        private static void CleanArea(List<cPoint> covered, ref OrderedDictionary<Point, (Direction root, Direction dir)> slots)
        {
            List<(Point pos, Direction root, Direction dir)> badSlots = slots.Where(x => covered.Contains(x.Key)).Select(x => (x.Key, x.Value.root, x.Value.dir)).ToList();
            if (badSlots.Count > 0)
            {
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{badSlots.Count} slots were covered and must be removed: {string.Join(", ", badSlots.Select(x => $"{x.pos.ToTuple()}{x.dir} (branch: {x.root})"))}.");
                foreach (var s in badSlots)
                {
                    slots.Remove(s.pos);
                }
            }
        }

        private static async Task LogAsync(string level, string message, bool lineSkip = false)
        {
            string jump = lineSkip ? "\n" : "";
            string logEntry = $"{jump}[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync("test.log", logEntry);
        }

        private Point AdjustForInsertion(Territory territory, Point displacement, Direction root, Direction dir, SortedSet<cPoint> occupied)
        {
            var position = territory.Anchor.Add(displacement);
            var width = territory.Size.X;
            var oddHeight = territory.Size.Y;
            var height = oddHeight + oddHeight % 2;
            var flipX = width - 1;
            var flipY = height - 2;
            bool bang = false;
            bool drift = false;
            Direction driftTo = Direction.Undefined;
            Point vector = new Point();

            var offset = AnchorRelativeToDirection(root, position, flipX, flipY);
            if (Collision(occupied, offset, width, height, ref vector))
            {
                bang = true;
                if (vector.IsEmpty)
                {
                    //This means this slot was boxed in! Last attempt to place it
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing #{territory.Id}: there wasn't enough space for it! Attempting a forced push {dir}...");
                    bool placed = false;
                    for (int i = 1; i < 10; i++)
                    {
                        var _vector = new Point();
                        offset = MoveIntoDirection(dir, offset, i * 2);
                        if (!Collision(occupied, offset, width, height, ref _vector))
                        {
                            placed = true;
                            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"...moved it {i*2} tiles {dir}. Proceeding to add drift.");
                            break;
                        }
                    }
                    if (!placed)
                    {
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"...still couldn't place it over the next 20 tiles! Giving up.");
                        return offset;
                    }
                }
                else
                {
                    // first, move to avoid overlaps.
                    offset = offset.Add(vector);
                }
                // check if we could also apply drift
                Point driftMove;
                var drifted = Drift(offset, out driftTo, out driftMove);
                if (!driftMove.IsEmpty)
                {
                    drift = true;
                    Point SecondVector = new Point();
                    if (Collision(occupied, drifted, width, height, ref SecondVector))
                    {
                        offset = offset.Add(SecondVector);
                    }
                }
            }
            offset = offset.FitToHex();
            if (bang)
            {
                string reportDrift = drift ? $", making it drift further {driftTo}" : "";
                string reportCollision = bang ? $"Overlaps were corrected{reportDrift}." : "";
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"#{territory.Id} was pushed to {offset.ToTuple()}. {reportCollision}");
            }
            return offset;
        }

        private static Point MoveIntoDirection(Direction direction, Point position, int distance)
        {
            switch (direction)
            {
                case Direction.Undefined:
                case Direction.Right:
                    position = new Point(position.X + distance, position.Y);
                    break;
                case Direction.Down:
                    position = new Point(position.X, position.Y - distance);
                    break;
                case Direction.Left:
                    position = new Point(position.X - distance, position.Y);
                    break;
                case Direction.Up:
                    position = new Point(position.X, position.Y + distance);
                    break;
            }
            return position;
        }

        private Point DirectionalCollision(Direction direction, SortedSet<cPoint> occupied, Point position, int width, int height)
        {
            var hits = Enumerable.Empty<Point>();
            switch (direction)
            {
                case Direction.Undefined:
                case Direction.Right:
                    hits = Spread(width, height, coord => new Point(position.X + coord.a, position.Y - coord.b), p => occupied.Contains(p)); // Scan right/down
                    break;
                case Direction.Down:
                    hits = Spread(width, height, coord => new Point(position.X - coord.a, position.Y - coord.b), p => occupied.Contains(p)); // Scan left/down
                    break;
                case Direction.Left:
                    hits = Spread(width, height, coord => new Point(position.X - coord.a, position.Y + coord.b), p => occupied.Contains(p)); // Scan left/up
                    break;
                case Direction.Up:
                    hits = Spread(width, height, coord => new Point(position.X + coord.a, position.Y + coord.b), p => occupied.Contains(p)); // Scan right/up
                    break;
            }
            var result = new Point();
            if (hits.Any())
            {
                result = new Point(hits.MaxBy(p => p.X).X, hits.MaxBy(p => p.Y).Y);
            }
            return result;
        }

        private bool Collision(SortedSet<cPoint> occupied, Point position, int width, int height, ref Point pushVector)
        {
            var hits = Spread(width, height, coord => new Point(position.X + coord.a, position.Y - coord.b), p => occupied.Contains(p)).ToList();
            if (!hits.Any()) return false;
            var minX = hits.MinBy(p => p.X).X;
            var maxX = hits.MaxBy(p => p.X).X +1; //for comparsions against width
            var minY = hits.MinBy(p => p.Y).Y;
            var maxY = hits.MaxBy(p => p.Y).Y +1; //for comparsions against height
            if ((minX == 0 && maxX == width - 1) || (minY == 0 && maxY == height)) return true; // hits on both sides or above and below
            bool leftHit = maxX < width;
            bool topHit = maxY < height;
            int pushX = leftHit ? maxX : -(width - minX); // if left, push right & vice-versa
            int pushY = topHit ? -maxY : height - minY; // if top, push down, & vice-versa
            pushVector = new Point(pushX, pushY);
            return true;
        }

        private static Point AnchorRelativeToDirection(Direction direction, Point position, int flipX, int flipY)
        {
            switch (direction)
            {
                case Direction.Undefined:
                case Direction.Right:
                    return position; // Anchor = slot
                case Direction.Down:
                    return new Point(position.X - flipX, position.Y); // Anchor to the right
                case Direction.Left:
                    return new Point(position.X - flipX, position.Y + flipY); // Anchor opposite to slot.
                case Direction.Up:
                    return new Point(position.X, position.Y + flipY); // Anchor at the bottom
            }
            return position;
        }

        private List<cPoint> FillArea(Territory territory)
        {
            var width = territory.Size.X + gap;
            var height = territory.Size.Y + gap;
            return Spread(width, height,
                coord => new Point(territory.Anchor.X - (gap / 2) + coord.a, territory.Anchor.Y + (gap / 2) - coord.b).FitToHex())
                .Select(x => (cPoint)x).ToList();
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

        private List<(Point pos, Direction rootDir, Direction dir)> NextSlotsHelix(Point lastSlot, Territory territory, Direction rootDir, SortedSet<cPoint> occupied) //
        {
            bool setRoot = rootDir == Direction.Undefined ? true : false;
            bool firstRun = rootDir == Direction.Undefined;
            bool quadrant = rootDir != helixLastDir;
            bool cycle = quadrant && rootDir == Direction.Right;
            var ax = firstRun ? territory.Anchor.X : lastSlot.X;
            var ay = firstRun ? territory.Anchor.Y : lastSlot.Y;
            var width = territory.Size.X - 1;
            var oddHeight = territory.Size.Y;
            var height = oddHeight + oddHeight % 2 - 2; //0
            var slots = new List<(Point pos, Direction root, Direction dir)>();
            if (cycle && !firstRun) helixGeneration++;
            List<string> log = new List<string>();
            string level = MethodBase.GetCurrentMethod().Name;
            _ = LogAsync(level, $"Calculating slots for #{territory.Id}: ax={ax}, ay={ay}, width={width}, height={height}, setRoot={setRoot}, quadrant={quadrant}, cycle={cycle}.");

            void Select(Point slot, Direction root, Direction dir)
            {
                //if (slot.X > max.X || slot.X < min.X || slot.Y > max.Y || slot.Y < min.Y)
                bool canDrift = false;
                Point drifted = new Point();
                if (!firstRun)
                {
                    drifted = Drift(slot, out _, out _);
                    canDrift = !occupied.Contains(drifted);
                }
                slot = canDrift ? drifted : slot.FitToHex(); //here because drift does its own.
                if (canDrift || !occupied.Contains(slot))
                {
                    slots.Add((slot, root, dir));
                    log.Add($"{slot.ToTuple().ToString()}{dir}");
                }
                else
                {
                    _ = LogAsync(level, $"{slot.ToTuple()}{dir} was occupied, slot skipped! Branch: {root})");
                }
            }

            //Place future slots, in clockwise order
            if (rootDir == Direction.Right || firstRun)
            {
                if (quadrant) Select(new Point(ax + width + gap, ay), setRoot ? Direction.Right : rootDir, Direction.Right);
                if (!firstRun) Select(new Point(ax, ay - height - vertGap), rootDir, Direction.Down);
            }
            if (rootDir == Direction.Down || firstRun)
            {
                int step = firstRun ? width : 0;
                if (quadrant) Select(new Point(ax + step, ay - height - vertGap), setRoot ? Direction.Down : rootDir, Direction.Down);
                if (!firstRun) Select(new Point(ax - width - gap, ay), rootDir, Direction.Left);
            }
            if (rootDir == Direction.Left || firstRun)
            {
                int step = firstRun ? 0 : width;
                if (quadrant) Select(new Point(ax - step - gap, ay), setRoot ? Direction.Left : rootDir, Direction.Left);
                if (!firstRun) Select(new Point(ax, ay + height + vertGap), rootDir, Direction.Up);
            }
            if (rootDir == Direction.Up || firstRun)
            {
                int step = firstRun ? 0 : height;
                if (quadrant) Select(new Point(ax, ay + step + vertGap), setRoot ? Direction.Up : rootDir, Direction.Up);
                if (!firstRun) Select(new Point(ax + width + gap, ay), rootDir, Direction.Right);
            }
            if (slots.Count() == 0)
            {
                _ = LogAsync(level, $"No Slots found for #{territory.Id}! Branch: {rootDir})");
            }
            _ = LogAsync(level, $"Slots around #{territory.Id}: {string.Join(", ", log)} (branch: {rootDir}, gen: {helixGeneration}).");

            //House keeping
            helixLastDir = rootDir;

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
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing {c.Name} @ {c.Position.ToTuple()}, set aside...");
                    misplaced.Add(c);
                }
            }
        }
    }
}