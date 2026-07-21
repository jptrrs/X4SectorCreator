using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TreeView;

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
        internal const int gap = 1;
        internal List<Cluster> misplaced = [];
        internal Point occupiedMax;
        internal Dictionary<int, Territory> territories = [];
        internal Dictionary<int, List<int>> domains = [];
        internal HashSet<int> sequentialDomains = [];
        private Dictionary<Direction, List<int>> stagedOld = []; // remove!!!
        private Dictionary<ulong, List<int>> staged = [];


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

        private static int squareBoundary = -1;

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

        private int helixGeneration = 1;
        private Direction helixLastBranch = Direction.Up;

        private Func<Point, bool> InBounds = p =>
        {
            var absX = Math.Abs(p.X);
            var absY = Math.Abs(p.Y);
            return absX <= GridFrameBounds.maxX && absY <= GridFrameBounds.maxY;
        };

        private Func<Point, bool> InsideSquare = p =>
        {
            var absX = Math.Abs(p.X);
            var absY = Math.Abs(p.Y);
            return absX < SquareBoundary && absY < SquareBoundary;
        };

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            // Gather some basic info
            hexGridFrame = ClusterManager.FrameHexGrid(clusters.ToList());
            _ = Toolbox.LogAsync("Initializing", $"cols = {hexGridFrame.cols} x rows = {hexGridFrame.rows}");

            // Group clusters into territories based adjacency and DLCs
            CarveTerritories(clusters);
            // Map connections for all clusters and register entry points for territories
            FindConnections();
            // Determine if there are neighboring territories owned by the same faction TO-DO: exclude the Xenon! 
            FindAnnexed();
            // Determine if there are other close territories owned by the same faction and separated by only a neutral sector. TO-DO: exclude the Xenon! 
            FindCloseColonies();
            // Consolidate neighbouring territories with the same owner under merged domains.
            ConsolidateDomains();
            // Shuffle!
            Shuffle();
            // Update Map as needed.
            if (MainForm.Instance.SectorMap.IsInitialized) MainForm.Instance.SectorMap.Value.Reset();
        }

        internal static int VertGap => gap * 2;

        private static (int maxX, int maxY) GridFrameBounds => (hexGridFrame.cols / 2, hexGridFrame.rows / 2);

        private static int SquareBoundary
        {
            get
            {
                if (squareBoundary < 0)
                {
                    squareBoundary = Math.Min(GridFrameBounds.maxX, GridFrameBounds.maxY);
                }
                return squareBoundary;
            }
        }

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
                log.Add($"\n{territory.Id} - {territory.Seed.Name}, {territory.Clusters.Count} clusters.");
            }
            //logging
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"\n\n--- Territories ---\n{string.Join("", log)}");
        }

        internal void FindAnnexed()
        {
            foreach (var territory in territories.Values)
            {
                if (territory.ExitPoints?.Count == 0) continue;
                foreach (var entry in territory.ExitPoints)
                {
                    var origin = entry.origin;
                    var gate = entry.gate;
                    var destination = entry.destination;
                    var foundId = destination.AssignedTerritoryId;
                    if (foundId > 0)
                    {
                        if (territory.annexedIds.Contains(foundId)) continue;
                        if (origin.Owner != null && !origin.IsNeutral && origin.Owner.Equals(destination.Owner, StringComparison.Ordinal))
                        {
                            territory.annexedIds.AddUnique(foundId);
                            territories[foundId].annexedIds.AddUnique(territory.Id);
                            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{territory.Seed.Name} annexed to #{territories[foundId].Id}-{territories[foundId].Seed.Name}");
                            // register pair into the class-level domains dictionary
                            var key = domains.Count + 1;
                            domains.Add(key, [territory.Id, foundId]);
                        }
                    }
                }
            }
        }

        internal void ConsolidateDomains()
        {
            // Build groups by merging any overlapping sets into larger ones.
            var groups = new List<HashSet<int>>();

            foreach (var values in domains.Values)
            {
                if (values == null || values.Count < 2) continue;
                int a = values[0];
                int b = values[1];
                int c = values.Count > 2 ? values[2] : -1;

                // Find all existing groups that intersect this set
                var intersecting = groups.Where(g => g.Contains(a) || g.Contains(b) || g.Contains(c)).ToList();

                if (intersecting.Count == 0)
                {
                    // new group
                    if (c > 0) groups.Add(new HashSet<int> { a, b, c });
                    else groups.Add(new HashSet<int> { a, b });
                }
                else
                {
                    // merge all intersecting groups plus the set into the first one
                    var target = intersecting.First();
                    target.Add(a);
                    target.Add(b);
                    if (c > 0) target.Add(c);

                    foreach (var g in intersecting.Skip(1))
                    {
                        foreach (var id in g)
                        {
                            target.Add(id);
                            groups.Remove(g);
                        }
                    }
                }
            }

            // rebuild the dictionary so each entry is a consolidated domain.
            var consolidated = new Dictionary<int, List<int>>();
            int idx = 1;
            int count = 0;
            foreach (var g in groups)
            {
                count += g.Count;
                List<int> reordered = [];
                if (g.Any(x => territories[x].IsBridge)) //spares close colonies sets.
                {
                    reordered = g.ToList();
                    sequentialDomains.Add(idx); //note that down for later.
                }
                else
                {
                    reordered = g.OrderBy(x => Random.Shared.Next()).ToList();
                }
                consolidated.Add(idx++, reordered);
            }
            domains = DesignatedDomains(consolidated);
            count += domains.Count - idx;

            // logging
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Consolidated {domains.Count} domain(s): {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]"))}\n{count} territories total.");
        }

        internal Dictionary<int, List<int>> DesignatedDomains(Dictionary<int, List<int>> set)
        {
            if (!set.Any()) return set;
            foreach (var d in set)
            {
                foreach (var id in d.Value)
                {
                    territories[id].AssignedDomainId = d.Key;
                }
            }
            var i = set.Count + 1;
            foreach (var t in territories.Values.Where(t => t.AssignedDomainId == 0))
            {
                var n = i++;
                t.AssignedDomainId = n;
                set.Add(n, [t.Id]);
            }
            return set;
        }

        internal void FindCloseColonies()
        {
            var candidates = territories.Values
                .SelectMany(t => t.Frontiers)
                .Where(c => c.ExitPoints?.Count > 1 && c.Exits.Keys.All(s => s.IsNeutral))
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
                    var bridged = grouped.Select(x => x.Id).ToList();
                    cluster.BridgeFor.AddRange(bridged);
                    var bridge = territories[cluster.AssignedTerritoryId];
                    bridge.IsBridge = true;
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"#{bridge.Id}-{bridge.Seed.Name} is a bridge between {string.Join(" & ", grouped.Select(x => ("#" + x.Id, x.Seed.Name)))}");
                    // register set into the class-level domains dictionary
                    var id = domains.Count + 1;
                    domains.Add(id, bridged.Prepend(bridge.Id).ToList());
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
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"\n\n--- Shuffling ---", true);

            //No turning back now!
            MainForm.Instance.AllClusters.Clear();

            List<int> cards = territories.Keys.ToList();
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(cards));
            var slots = new Queue<(Point pos, ulong add, int gen)>([(new Point(0, 0), 0UL, 0)]);
            var deferred = new Queue<(Point pos, ulong add, int gen)>();
            SortedSet<cPoint> occupied = new SortedSet<cPoint>();
            for (int i = 0; i < cards.Count; i++)
            {
                Selection:
                //Pick the next slot.
                if (slots.Count == 0)
                {
                    if (deferred.Any())
                    {
                        slots = deferred;
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Bounds reached, spilling over!");
                    }
                    else
                    {
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: we've run out of slots!", true);
                        break;
                    }
                }
                var slot = slots.Dequeue();
                var newPos = slot.pos;
                var dir = slot.add.GetDirection();
                var branch = slot.add.GetMainBranch(slot.gen);

                //Select next territory.
                bool sequencesAllowed = dir == Direction.Undefined || dir == branch;
                var territory = PickNextFromStaged(slot.add, slot.gen, sequencesAllowed);
                if (territory == null)
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"No territory remaining to be assigned to branch {branch}, skipping. Last direction was {helixLastBranch}, {staged.Count} staged sets and {slots.Count} slots remaining.");
                    goto Selection;
                }
                var currentPos = territory.Anchor;

                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Assigning #{territory.Id} - {territory.Seed.Name}, size=({territory.Size.ToTuple()}, {territory.Clusters.Count} clusters, to slot @ {newPos.ToTuple()}{dir}", true);

                //Fine-tune the insertion spot so it fits right in.
                var planned = newPos.Subtract(currentPos);
                if (i > 0) newPos = AdjustForInsertion(territory, planned, branch, dir, occupied); //fitted

                //Move the piece
                var move = newPos.Subtract(currentPos); //defitted
                var report = territory.Reposition(move);

                //Keep track of occupied areas
                //var covered = FillArea(territory); //fitted
                var covered = territory.Contour;
                if (i == 0) occupied.Clear();
                occupied.UnionWith(covered);
                occupiedMax = occupied.Max;

                var cMax = covered.MaxBy(p => p.Y).Y;
                var cMin = covered.MinBy(p => p.Y).Y;
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{covered.Count} tiles were covered, {cMin} to {cMax} vertical, totalling {occupied.Count} now...");

                //Update the board.
                UpdateClusterMap(territory.Clusters, i);

                //Prepare the next slots.
                var nextSlots = NextSlotsHelix(territory, occupied, slot.add, slot.gen);
                foreach (var (pos, add, gen) in nextSlots)
                {
                    if (InBounds(pos)) slots.Enqueue((pos, add, gen));
                    else deferred.Enqueue((pos, add, gen));
                }
            }
            HandleMisplaced();
        }

        //pick next refactor
        //1. receive an ulong and a int for depth, representing a slot.
        //2. null guard remains the same
        //3. ulong tells us the root - enough for the current sorting mechanism. But also where the slot came from - the parent - so domains that require following the sequence can be corectly picked. -> need to diferentiate them and revert back to root logic until the cycle comes around again.
        //3. look for a staged entry indexed by that ulong: root for regular placement, particular path for sequenced domains. If there isn't one, draw randomly from domains (similar to now) and index with that ulong.
        //4. return drawing from that set.

        private bool HasAncestorStaged(ulong path, int generation, out ulong found)
        {
            if (generation <= 2) goto fail; //that would just return the trunk or main branch
            bool flag = false;
            for (var i = generation - 1; i > 1; i--)
            {
                var tested = path.GetAddressAtDepth(generation, i);
                flag = staged.ContainsKey(tested);
                if (flag)
                {
                    found = tested;
                    return true;
                }
            }
            fail:
            found = path;
            return false;
        }

        internal Territory PickNextFromStaged(ulong path, int generation, bool sequencesAllowed)
        {
            bool domsRemain = domains.Count > 0;
            bool stagedRemain = staged.Count > 0;
            //Bail out if something hasn't been intialized or the lists have been exausted.
            if (!domsRemain && stagedRemain && staged.Values.All(x => x.Count == 0)) return null;
            bool isSequence = HasAncestorStaged(path, generation, out ulong ancestor);
            var branch = isSequence ? ancestor : path.GetAddressAtDepth(generation, 1); //selects either the root branch or the divergence point for a sequence
            if (!isSequence && !staged.ContainsKey(branch))
            {
                staged.Add(branch, new List<int>());
            }
            if (staged[branch].Count == 0)
            {
                //The requested branch is currently empty, so...
                if (!domsRemain) return null; //...give up
                //...or load another set:
                var regularDomains = domains.Where(x => !sequentialDomains.Contains(x.Key));
                bool holdSequences = !sequencesAllowed && regularDomains.Any(); 
                var set = holdSequences ? regularDomains.Random() : domains.Random();
                if (set.Value == null)
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: selected domain has a null list! Looking for branch {branch.ToString()}, isSequence={isSequence}, sequencesAllowed={sequencesAllowed}, {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]"))}");
                }
                if (sequencesAllowed && sequentialDomains.Contains(set.Key) && !staged.ContainsKey(path))
                {
                    //It's a colony sequence, needs own branch.
                    staged.Add(path, set.Value);
                    domains.Remove(set.Key);
                    branch = path;
                }
                else
                {
                    staged[branch] = set.Value;
                    domains.Remove(set.Key);
                }
            }
            if (!staged.TryGetValue(branch, out var selected) || selected == null || selected.Count == 0)
            {
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: unable to find a valid domain set in the staged collection.");
                return null;
            }
            else if (selected.Any(x => !territories.ContainsKey(x)))
            {
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: Some selected territories don't exist.Attempting to purge them.");
                selected = selected.Where(x => territories.ContainsKey(x)).ToList();
                if (selected.Count == 0) return null;
            }
            var card = selected.Any(x => territories[x].IsBridge) ? selected.First() : selected.RandomOrDefault();
            staged[branch].Remove(card);
            return territories[card];
            
        }

        private static Point AnchorRelativeToDirection(Direction direction, Point position, int flipX, int flipY)
        {
            Point result = new Point();
            switch (direction)
            {
                case Direction.Undefined:
                case Direction.Right:
                    result = position; // Anchor = slot
                    goto finish;
                case Direction.Down:
                    result = new Point(position.X - flipX, position.Y); // Anchor to the right
                    break;

                case Direction.Left:
                    result = new Point(position.X - flipX, position.Y + flipY); // Anchor opposite to slot.
                    break;

                case Direction.Up:
                    result = new Point(position.X, position.Y + flipY); // Anchor at the bottom
                    break;
            }
            position = result;
            finish:
            return position;
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

        private Point AdjustForInsertion(Territory territory, Point displacement, Direction branch, Direction dir, SortedSet<cPoint> occupied)
        {
            var selected = territory.Anchor.Add(displacement);
            var width = territory.Size.X;
            var oddHeight = territory.HeightToFit;
            var height = oddHeight + oddHeight % 2;
            var flipX = width - 1;
            var flipY = height - 2;

            //Logging logic
            string slot = selected.ToTuple().ToString();
            bool flushed = false;
            bool drifted = false;
            Point drift = new Point();

            //1. Flush out the slot if covered.
            var flush = new Point();
            if (TryToPushAround(selected, branch, dir, occupied, 0, 0, 10, ref flush))
            {
                flushed = true;
                selected = selected.Add(flush);
            }

            //2. Calculate relative position
            var offset = AnchorRelativeToDirection(branch, selected, flipX, flipY);
            string relative = offset.ToTuple().ToString();

            //3. Deal with collisions around it
            string collisionReport = "";
            bool bang = Collision(occupied, offset, width, height, dir, ref collisionReport, out Point dodge);
            if (bang)
            {
                if (dodge.IsEmpty)
                {
                    //This means this slot was boxed in! Last attempt to place it
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing #{territory.Id}: there wasn't enough space for it! Attempting a forced push...");
                    if (!TryToPushAround(offset, branch, dir, occupied, width, height, 20, ref dodge))
                    {
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"...still couldn't place it over the next 20 tiles! Giving up.");
                        return offset;
                    }
                    else
                    {
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"...moved it by {dodge}.");
                    }
                }
                // When possible, move to avoid overlaps.
                offset = offset.Add(dodge);
            }

            //4. Drift, if possible, around the center
            bool canDrift = dir != GetDriftDirection(offset);
            if (canDrift && Drift(offset, out drift))
            {
                Point driftedPos = offset.Add(drift);
                if (!SimpleCollision(occupied, driftedPos, width, height))
                {
                    offset = driftedPos;
                    drifted = true;
                }
            }

            var result = offset.FitToHex();

            //logging
            List<string> report = [$"#{territory.Id} inserted @ {result.ToTuple()}, from {slot}{dir}"];
            if (flushed) report.Add($"uncovered by moving {flush.ToTuple()}");
            report.Add($"anchor @ {relative}");
            if (drifted) report.Add($"drifted by {drift.ToTuple()}");
            if (bang) report.Add(collisionReport);
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, string.Join(" -> ", report) + ".");

            return result;
        }

        private bool TryToPushAround(Point position, Direction primaryDir, Direction secondaryDir, SortedSet<cPoint> occupied, int width, int height, int maxPush, ref Point vector)
        {
            bool placed = false;
            bool singleTile = width <= 1 && height <= 2;
            for (int i = 1; i < maxPush; i++)
            {
                bool sucess = false;
                Point target = new Point();
                Direction chosenDir = new Direction();
                Point forced1 = MoveIntoDirection(primaryDir, position, i);
                if ((singleTile && !occupied.Contains(forced1)) || !SimpleCollision(occupied, forced1, width, height))
                {
                    sucess = true;
                    target = forced1;
                    chosenDir = primaryDir;
                }
                else
                {
                    Point forced2 = MoveIntoDirection(secondaryDir, position, i);
                    if ((singleTile && !occupied.Contains(forced2)) || !SimpleCollision(occupied, forced2, width, height))
                    {
                        sucess = true;
                        target = forced2;
                        chosenDir = secondaryDir;
                    }
                }
                if (sucess)
                {
                    placed = true;
                    vector = target.Subtract(position);
                    break;
                }
            }
            return placed;
        }

        private bool Collision(SortedSet<cPoint> occupied, Point position, int width, int height, Direction preferredDir, ref string report, out Point pushVector)
        {
            var hexPos = position.WiggleToFit(occupied); //not fitting could result in undetected collisions
            var hits = ScanForCollisions(occupied, hexPos, width, height);
            pushVector = new Point();
            if (!hits.Any()) return false;
            var minX = hits.MinBy(p => p.X).X;
            var maxX = hits.MaxBy(p => p.X).X + 1; //for comparsions against width
            var minY = hits.MinBy(p => p.Y).Y;
            var maxY = hits.MaxBy(p => p.Y).Y + 1; //for comparsions against height
            bool leftHit = maxX < width;
            bool rightHit = maxX == width;
            bool topHit = maxY < height;
            bool bottomHit = maxY == height;
            bool blockedX = leftHit && rightHit;
            bool blockedY = topHit && bottomHit;
            if (blockedX && blockedY)
            {
                report = $"{hexPos.ToTuple()} was boxed in, size={width}x{height}, last vector was {pushVector.ToTuple()}";
                return true;
            }
            int[] range = new int[4];
            range[0] = rightHit ? -(width - minX) : 0; //push left
            range[1] = bottomHit ? height - minY : 0; //push up
            range[2] = leftHit ? maxX : 0; //push right
            range[3] = topHit ? -maxY : 0; //push down
            int pushX = blockedX ? 0 : range[0] + range[2];
            int pushY = blockedY ? 0 : range[1] + range[3];
            bool viableX = false;
            bool viableY = false;

            //Probe X and push it if preferred
            if (Math.Abs(pushX) > 0)
            {
                var hitsX = Toolbox.Spread(width, height, coord => new Point(hexPos.X + pushX + coord.a, hexPos.Y - coord.b), p => occupied.Contains(p)).ToList();
                viableX = !hitsX.Any();
            }
            if (viableX && (preferredDir == Direction.Right || preferredDir == Direction.Left)) goto pushX;

            //Probe Y and push it if preferred
            if (Math.Abs(pushY) > 0)
            {
                var hitsY = Toolbox.Spread(width, height, coord => new Point(hexPos.X + coord.a, hexPos.Y + pushY - coord.b), p => occupied.Contains(p)).ToList();
                viableY = !hitsY.Any();
            }
            if (viableY && (preferredDir == Direction.Up || preferredDir == Direction.Down)) goto pushY;

            //Decide where to go
            if (viableX && viableY)
            {
                if (preferredDir == Direction.Right || preferredDir == Direction.Left) goto pushX;
                if (preferredDir == Direction.Up || preferredDir == Direction.Down) goto pushY;
            }
            if (viableX) goto pushX;
            if (viableY) goto pushY;
            if (!viableX && !viableY)
            {
                return true;
            }

            pushX:
            pushVector = new Point(pushX, 0);
            report = $"Collisions @ {hexPos.ToTuple()} require a horizontal push of {pushVector.X}.";
            return true;

            pushY:
            pushVector = new Point(0, pushY);
            report = $"Collisions @ {hexPos.ToTuple()} require a vertical push of {pushVector.Y}.";
            return true;
        }

        private bool Drift(Point pos, out Point vector)
        {
            //Push the position (pseudo)clockwise based on the distance to the center.

            //Escape if edge case
            if (pos.IsEmpty || pos.X == pos.Y)
            {
                vector = new Point();
                return false;
            }

            //Bases
            float limit = GridFrameBounds.maxY * 1.25f; //not bothering with X because the maps fits a horizontal rectangle.
            var maxDrift = 3;

            //Distance on the Y axis determines drift in the X axis, and vice-versa
            //Smoothstep easing for values near zero to remain zero while larger values reach maxDrift
            float normY = Math.Clamp(Math.Abs(pos.Y) / limit, 0f, 1f);
            float easedY = normY * normY * (3f - 2f * normY); // smoothstep
            float scaledY = easedY * maxDrift;
            int moveX = scaledY < 0.3f ? 0 : (int)MathF.Round(scaledY);
            moveX = Math.Min(maxDrift, moveX);

            float normX = Math.Clamp(Math.Abs(pos.X) / limit, 0f, 1f);
            float easedX = normX * normX * (3f - 2f * normX);
            float scaledX = easedX * maxDrift;
            int moveY = scaledX < 0.3f ? 0 : (int)MathF.Round(scaledX);
            moveY = Math.Min(maxDrift, moveY);

            //Conclusion
            vector = GetDriftVector(pos, GetDriftDirection(pos), moveX, moveY);
            return !vector.IsEmpty;
        }

        private Direction GetDriftDirection(Point pos)
        {
            //Direction is based on position, 45 degrees quadrants.
            if ((InsideSquare(pos) && pos.Y > pos.X && pos.Y > -pos.X) || (pos.X < SquareBoundary && pos.Y > SquareBoundary)) //Above: move right then down
            {
                return Direction.Right;
            }
            else if ((InsideSquare(pos) && pos.Y < pos.X && pos.Y < -pos.X) || (pos.X > -SquareBoundary && pos.Y < -SquareBoundary)) //Below: move left then up
            {
                return Direction.Left;
            }
            else if ((InsideSquare(pos) && pos.X > pos.Y && pos.X > -pos.Y) || (pos.X > SquareBoundary && pos.Y > -SquareBoundary)) //Right: move down then left
            {
                return Direction.Down;
            }
            else if ((InsideSquare(pos) && pos.X < pos.Y && pos.X < -pos.Y) || (pos.X < -SquareBoundary && pos.Y < SquareBoundary)) //Left: move up then right
            {
                return Direction.Up;
            }
            return Direction.Undefined;
        }

        private Point GetDriftVector(Point position, Direction dir, int moveX, int moveY)
        {
            int yaw = 0;
            switch (dir)
            {
                case Direction.Right: //Above: move right then down
                    if (position.X > 0) yaw = moveY;
                    return new Point(moveX, -yaw)/*.FitToHex(Direction.Down)*/;

                case Direction.Left: //Below: move left then up
                    if (position.X < 0) yaw = moveY;
                    return new Point(-moveX, yaw)/*.FitToHex()*/;

                case Direction.Down: //Right: move down then left
                    if (position.Y < 0) yaw = moveX;
                    return new Point(-yaw, -moveY)/*.FitToHex(Direction.Down)*/;

                case Direction.Up: //Left: move up then right
                    if (position.Y > 0) yaw = moveX;
                    return new Point(yaw, moveY)/*.FitToHex()*/;

                case Direction.Undefined:
                    goto Fail;
            }
            Fail:
            return new Point();
        }

        private void HandleMisplaced()
        {
            if (misplaced.Count == 0) return;
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{misplaced.Count} clusters were misplaced and will be set aside...", true);
            int y = 1;
            foreach (var c in misplaced)
            {
                c.Position = occupiedMax.Add(new Point(gap, y * VertGap - VertGap)).FitToHex();
                y++;
                if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{c.Name}, territory #{c.AssignedTerritoryId}, placed @ ({c.Position.ToTuple()}).");
                }
                else
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: couldn't place {c.Name}, territory #{c.AssignedTerritoryId}, anywhere! Last attempt: {c.Position.ToTuple()}.");
                }
            }
        }

        private List<(Point position, ulong address, int generation)> NextSlotsHelix(Territory territory, SortedSet<cPoint> occupied, ulong parentAddress, int parentGeneration)
        {
            var branch = parentAddress.GetMainBranch(parentGeneration);
            bool firstRun = branch == Direction.Undefined;
            bool quadrant = branch != helixLastBranch;
            bool cycle = quadrant && branch == Direction.Right;
            var ax = territory.Anchor.X;
            var ay = territory.Anchor.Y;
            var width = territory.Size.X;
            var height = territory.HeightToFit;
            var slots = new List<(Point pos, ulong add, int gen)>();
            var max = occupied.Max();
            var min = occupied.Min();
            if (cycle) helixGeneration++;

            //logging
            List<string> log = new List<string>();
            string level = MethodBase.GetCurrentMethod().Name;

            //finishing routine
            void Select(Point slot, Direction dir)
            {
                bool front = slot.X > max.X || slot.X < min.X || slot.Y > max.Y || slot.Y < min.Y;
                if (front || !occupied.Contains(slot))
                {
                    slots.Add((slot, parentAddress.DownstreamAddress(dir), helixGeneration));
                    log.Add($"{slot.ToTuple().ToString()}{dir}");
                }
                else
                {
                    _ = Toolbox.LogAsync(level, $"{slot.ToTuple()}{dir} was already occupied, slot skipped! Branch: {branch})");
                }
            }

            //Place future slots, in clockwise order
            if (branch == Direction.Right || firstRun)
            {
                if (quadrant) Select(new Point(ax + width + gap, ay), Direction.Right);
                if (!firstRun) Select(new Point(ax, ay - height - VertGap), Direction.Down);
            }
            if (branch == Direction.Down || firstRun)
            {
                if (quadrant) Select(new Point(ax + width - 1, ay - height - VertGap), Direction.Down);
                if (!firstRun) Select(new Point(ax - 1 - gap, ay), Direction.Left);
            }
            if (branch == Direction.Left || firstRun)
            {
                if (quadrant) Select(new Point(ax - 1 - gap, ay - height + 2), Direction.Left);
                if (!firstRun) Select(new Point(ax + width - 1, ay + 2 + VertGap), Direction.Up);
            }
            if (branch == Direction.Up || firstRun)
            {
                if (quadrant) Select(new Point(ax, ay + 2 + VertGap), Direction.Up);
                if (!firstRun) Select(new Point(ax + width + gap, ay - height + 2), Direction.Right);
            }
            if (slots.Count() == 0)
            {
                _ = Toolbox.LogAsync(level, $"No Slots found for #{territory.Id}! Branch: {branch})");
            }
            _ = Toolbox.LogAsync(level, $"Slots around #{territory.Id}: {string.Join(", ", log)} (branch: {branch}, gen: {helixGeneration}).");

            //House keeping
            helixLastBranch = branch;

            return slots.ToList();
        }

        private List<Point> ScanForCollisions(SortedSet<cPoint> occupied, Point position, int width, int height)
        {
            return Toolbox.Spread(width, height, coord => new Point(position.X + coord.a, position.Y - coord.b), p => occupied.Contains(p)).ToList();
        }

        private bool SimpleCollision(SortedSet<cPoint> occupied, Point position, int width, int height)
        {
            var hexPos = position.FitToHex(); //not fitting could result in undetected collisions
            return ScanForCollisions(occupied, hexPos, width, height).Any();
        }

        private void UpdateClusterMap(List<Cluster> clusters, int errorY = 0)
        {
            foreach (var c in clusters)
            {
                if (!MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing {c.Name} @ {c.Position.ToTuple()}, set aside...");
                    misplaced.Add(c);
                }
            }
        }

    }
}

    