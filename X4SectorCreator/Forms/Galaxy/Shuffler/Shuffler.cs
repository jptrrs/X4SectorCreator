using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;
using static X4SectorCreator.Objects.Constructionplan;

namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    internal class Shuffler
    {
        private const int gap = 1;
        private static readonly (int x, int y)[] NeighborOffsets =
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
        private readonly Func<Territory, Cluster, bool> IsOutside = (territory, cluster) =>
        {
            return !territory.Clusters.Contains(cluster);
        };
        private Dictionary<int, List<int>> domains = [];
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
        private List<Cluster> misplaced = [];
        private Point occupiedMax;
        private HashSet<int> sequentialDomains = [];
        private Dictionary<string, List<int>> staged = [];
        private Dictionary<int, Territory> territories = [];
        
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
            // Report results so far
            TerritoriesReport();
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

        #region territories
        
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
        private readonly Func<Cluster, HashSet<Cluster>, IEnumerable<Cluster>> GetNeighbors = (location, crowd) =>
        {
            var targetPositions = NeighborOffsets
                .Select(offset => new Point(location.Position.X + offset.x, location.Position.Y + offset.y))
                .ToHashSet();
            return crowd.Where(cluster => targetPositions.Contains(cluster.Position));
        };
        private Func<Cluster, bool> DLCMatch => cluster =>
        {
            return cluster.Dlc == territories.Last().Value.dlc;
        };

        private Action<Cluster, bool> SortTerritory => (cluster, reset) =>
                {
            if (reset)
            {
                var newTerritory = new Territory(cluster, territories.Count);
                territories.Add(newTerritory.id, newTerritory);
                cluster.AssignedTerritoryId = newTerritory.id;
                return;
            }
            var territory = territories.Last().Value;
            territory.Clusters.Add(cluster);
            cluster.AssignedTerritoryId = territory.id;
        };
        private void CarveTerritories(IEnumerable<Cluster> clusters)
        {
            var ordered = clusters.OrderBy(x => x.Position.DistanceSquaredOnHexGrid(Point.Empty)).ToList();
            Toolbox.FlexFloodProcessor(ordered, SortTerritory, GetNeighbors, x => DLCMatch(x), x => AreConnected(x));
            foreach (var territory in territories.Values)
            {
                territory.SetUpBox();
            }
        }

        private void ConsolidateDomains()
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
                if (KeepSequence(g.Select(x => territories[x]).ToList()))
                {
                    reordered = g.OrderBy(x => x).ToList();
                    sequentialDomains.Add(idx); //note that down for later.
                }
                else
                {
                    reordered = g.OrderBy(x => Random.Shared.Next()).ToList();
                }
                consolidated.Add(idx++, reordered);
            }
            domains = DesignatedDomains(consolidated);

            //Now we're able to calculate territories' preferred orientations:
            InferOrientations();

            // logging
            count += domains.Count - idx;
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Consolidated {domains.Count} domain(s): {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]{(sequentialDomains.Contains(x.Key) ? "S" : "")}"))}\n{count} territories total.");
        }

        private Dictionary<int, List<int>> DesignatedDomains(Dictionary<int, List<int>> set)
        {
            if (!set.Any()) return set;
            foreach (var d in set)
            {
                foreach (var id in d.Value)
                {
                    var territory = territories[id];
                    territory.assignedDomainId = d.Key;
                    territory.peers = d.Value;
                }
            }
            var i = set.Count + 1;
            foreach (var t in territories.Values.Where(t => t.assignedDomainId == 0))
            {
                var n = i++;
                t.assignedDomainId = n;
                set.Add(n, [t.id]);
            }
            return set;
        }

        private void FindAnnexed()
        {
            foreach (var territory in territories.Values)
            {
                if (territory.ExitPoints?.Count == 0) continue;
                foreach (var entry in territory.ExitPoints)
                {
                    var origin = entry.origin;
                    var destination = entry.destination;
                    var foundId = destination.AssignedTerritoryId;
                    if (foundId > 0)
                    {
                        if (territory.annexedIds.Contains(foundId)) continue;
                        if (origin.Owner != null &&
                            !origin.IsNeutral &&
                            origin.Owner.Equals(destination.Owner, StringComparison.Ordinal) &&
                            !origin.Owner.Equals("Xenon", StringComparison.OrdinalIgnoreCase))
                        {
                            territory.annexedIds.AddUnique(foundId);
                            territories[foundId].annexedIds.AddUnique(territory.id);
                            var key = domains.Count + 1;
                            domains.Add(key, [territory.id, foundId]);
                        }
                    }
                }
            }
        }

        private void FindCloseColonies()
        {
            var candidates = territories.Values
                .SelectMany(t => t.frontiers)
                .Where(c => c.ExitPoints?.Count > 1 && c.Exits.Keys.All(s => s.IsNeutral))
                .ToList();
            foreach (var cluster in candidates)
            {
                var connected = cluster.Destinations
                    .Where(s => !s.IsNeutral && !s.Owner.Equals("Xenon", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(s => s.Owner)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Select(s => territories[s.AssignedTerritoryId]))
                    .ToList();
                if (!connected.Any()) continue;
                foreach (var grouped in connected)
                {
                    foreach (var neighbor in grouped)
                    {
                        neighbor.closeColonyIds.AddRangeUnique(grouped.Except([neighbor]).Select(x => x.id));
                    }
                    var bridged = grouped.Select(x => x.id).ToList();
                    cluster.BridgeFor.AddRange(bridged);
                    var bridge = territories[cluster.AssignedTerritoryId];
                    bridge.isBridge = true;
                    var extents = bridged.Append(bridge.id).ToList();
                    var id = domains.Count + 1;
                    domains.Add(id, extents);
                }
            }
        }

        private void FindConnections()
        {
            foreach (var territory in territories.Values)
            {
                var roads = new List<(Cluster, Sector, Gate, Sector)>();
                foreach (var cluster in territory.Clusters)
                {
                    cluster.ExitPoints = ClusterManager.PickDestinationsFromCluster(cluster, c => c != cluster);
                    foreach (var exit in cluster.ExitPoints.Where(x => x.destination.AssignedTerritoryId != territory.id))
                    {
                        roads.Add((cluster, exit.origin, exit.gate, exit.destination));
                    }
                }
                territory.ExitPoints = roads;
            }
        }
        
        private bool KeepSequence(List<Territory> set)
        {
            //Spares certain domain sets from spawning in randomized order: , .
            return set.Any(x => x.isBridge) // close colonies
                || (set.Any(x => x.annexedIds.Count > 0) && set.All(x => !string.IsNullOrWhiteSpace(x.dlc))); // annexed + DLC
        }
        
        private void TerritoriesReport()
        {
            var log = new StringBuilder();
            log.AppendLine($"\n\n--- Territories ---\n\n");
            foreach (var t in territories.Values)
            {
                var owner = t.seed.Sectors[0].IsNeutral ? "Neutral" : t.seed.Sectors[0].Owner;
                log.Append($"#{t.id} - {t.seed.Name}, {owner}: {t.Clusters.Count} clusters, {t.frontiers.Count} connecting, entry from {t.entryDirection}");
                if (t.annexedIds.Count > 0) log.Append($"; annexed to {string.Join(", ", t.annexedIds.Select(x => $"#{territories[x].id}-{territories[x].seed.Name}"))}");
                if (t.isBridge) log.Append($"; bridges {string.Join(", ", t.Clusters.First(x => x.BridgeFor.Count > 0).BridgeFor.Select(y => $"#{territories[y].id}-{territories[y].seed.Name}"))}");
                if (t.closeColonyIds.Count > 0) log.Append($"; colonies {string.Join(", ", t.closeColonyIds.Select(x => $"#{territories[x].id}-{territories[x].seed.Name}"))}");
                log.AppendLine(".");
            }
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, log.ToString());
        }

        private void InferOrientations()
        {
            foreach (var territory in territories.Values)
            {
                territory.SetUpDirection();
            }
        }

        #endregion

        #region shuffler

        internal Territory PickNextFromStaged(string path, bool sequencesAllowed, ref int skipTracker, out bool isSequence)
        {
            bool domsRemain = domains.Count > 0;
            bool stagedRemain = staged.Count > 0;

            //Bail out if something hasn't been intialized or the lists have been exausted.
            if (!domsRemain && stagedRemain && staged.Values.All(x => x.Count == 0))
            {
                isSequence = false;
                return null;
            }

            string branch;
            bool startsSequence = false;

            isSequence = HasAncestorStaged(path, out string ancestor); //detects if the path is from a sequence that already started.
            branch = isSequence ? ancestor : path.GetAddressAtDepth(1); //selects either the main branch or the divergence point for a sequence
            if (string.IsNullOrEmpty(branch) || branch == "0") branch = "1"; //prevents the domain called at the origin from generating a dead-end entry.
            if (!isSequence && !staged.ContainsKey(branch))
            {
                staged.Add(branch, new List<int>());
            }
            if (staged[branch].Count == 0)
            {
                //The requested branch is currently empty, so...
                if (!domsRemain)
                {
                    //The queue is empty! Cross out that branch (it will be re-added automatically later if needed) and bail out.
                    staged.Remove(branch);
                    skipTracker++;
                    return null;
                }
                //Load another set:
                //NOTE: A new sequence starts here. It both selects the sequence set and changes the branch, creating a divergence.
                branch = RefreshStage(branch, path, sequencesAllowed, ref startsSequence);
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
            var card = (startsSequence || isSequence) ? selected.First() : selected.RandomOrDefault();
            staged[branch].Remove(card);
            return territories[card];
        }

        internal void Shuffle()
        {
            //logging
            string level = MethodBase.GetCurrentMethod().Name;
            _ = Toolbox.LogAsync(level, $"\n\n--- Shuffling ---", true);

            //No turning back now!
            MainForm.Instance.AllClusters.Clear();

            List<int> cards = territories.Keys.ToList();
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(cards));
            var slots = new Queue<(Point pos, string path)>([(Point.Empty, "")]);
            var deferred = new Queue<(Point pos, string add)>();
            SortedSet<cPoint> occupied = new SortedSet<cPoint>();
            bool inBounds = true;

            bool TryGetTerritory(out Territory territory, out bool isSequence, out Point pos, out Direction dir, out Direction branch, out string path)
            {
                bool flag = false;
                territory = null;
                isSequence = false;
                pos = Point.Empty;
                dir = Direction.Undefined;
                branch = Direction.Undefined;
                path = null;
                int skipTracker = 0;
                while (slots.Count > 0)
                {
                    var slot = slots.Dequeue();
                    path = slot.path;
                    pos = slot.pos;
                    dir = path.GetDirection();
                    branch = path.GetMainBranch();
                    bool sequencesAllowed = dir != branch; //This makes them necessarily linear
                    territory = PickNextFromStaged(slot.path, sequencesAllowed, ref skipTracker, out isSequence);
                    flag = territory != null;
                    if (flag)
                    {
                        if (skipTracker > 0)
                        {
                            _ = Toolbox.LogAsync(level, $"We've run out of domains before all staged were distributed. {skipTracker} slots were skipped while looking for the next viable one.", true);
                        }
                        break;
                    }
                    if (inBounds) deferred.Enqueue(slot); //recycle slots by throwing them back at the end of the line.
                    if (slots.Count == 0)
                    {
                        if (deferred.Count > 0)
                        {
                            //refresh slots queue and switch to plan B.
                            slots = deferred;
                            inBounds = false;
                            _ = Toolbox.LogAsync(level, $"Bounds reached, spilling over!", true);
                        }
                        else
                        {
                            //end of the line
                            _ = Toolbox.LogAsync(level, $"ERROR: we've run out of slots!", true);
                        }
                    }
                }
                return flag;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                bool valid = TryGetTerritory(out var territory, out var isSequence, out var position, out var direction, out var branch, out var path);

                //Reporting territory selection
                _ = Toolbox.LogAsync(level, $"Step {i}, branch {branch}, slot @ {position.ToTuple()}/{direction}/{path}", true);
                if (valid)
                {
                    _ = Toolbox.LogAsync(level, $"Assigning #{territory.id}-{territory.seed.Name}, size=({territory.size.ToTuple()}, {territory.Clusters.Count} clusters");
                }
                else
                {
                    _ = Toolbox.LogAsync(level, $"ERROR: we've run out of territories to assign to slots!");
                    break;
                }

                //Rotate it as needed.
                if (direction != Direction.Undefined && territory.entryDirection != Direction.Undefined && territory.entryDirection != direction)
                {
                    var turns = territory.entryDirection.ClockwiseStepsTo(direction);
                    territory.Rotate(turns);
                }

                //Fine-tune the insertion spot so it fits right in.
                var currentPos = territory.anchor;
                var planned = position.Subtract(currentPos);
                if (i > 0) position = AdjustForInsertion(territory, planned, branch, direction, occupied, isSequence);

                //Move the piece
                var move = position.Subtract(currentPos);
                var report = territory.Reposition(move);

                //Keep track of occupied areas
                var covered = territory.Contour;
                if (i == 0) occupied.Clear();
                occupied.UnionWith(covered);
                occupiedMax = occupied.Max;

                //Reporting covered tiles
                var cMaxY = covered.MaxBy(p => p.Y).Y;
                var cMinY = covered.MinBy(p => p.Y).Y;
                var cMaxX = covered.MaxBy(p => p.X).X;
                var cMinX = covered.MinBy(p => p.X).X;
                _ = Toolbox.LogAsync(level, $"{covered.Count} tiles were covered, {cMinX} to {cMaxX} horizontal, {cMinY} to {cMaxY} vertical, totalling {occupied.Count} now.");

                //Update the board.
                UpdateClusterMap(territory.Clusters, i);

                //Prepare the next slots.
                var nextSlots = NextSlotsHelix(territory, occupied, path);
                //bool isSequential = sequentialDomains.Contains(territory.AssignedDomainId);
                foreach (var (pos, add) in nextSlots)
                {
                    if (InBounds(pos) || isSequence) slots.Enqueue((pos, add));
                    else deferred.Enqueue((pos, add));
                }
            }
            HandleMisplaced();
        }
        
        private static Point AnchorRelativeToDirection(Direction direction, Point position, int flipX, int flipY)
        {
            Point result = Point.Empty;
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

        private Point AdjustForInsertion(Territory territory, Point displacement, Direction branch, Direction dir, SortedSet<cPoint> occupied, bool isSequence)
        {
            var selected = territory.anchor.Add(displacement);
            var width = territory.size.X;
            var oddHeight = territory.HeightToFit;
            var height = oddHeight + oddHeight % 2;
            var flipX = width - 1;
            var flipY = height - 2;

            //Logging logic
            string slot = selected.ToTuple().ToString();
            bool flushed = false;
            bool attracted = false;
            bool drifted = false;
            Point drift = Point.Empty;

            //1. Flush out the slot if covered.
            var driftDirection = GetDriftDirection(selected);
            var flush = Point.Empty;
            if (occupied.Contains(selected) && TryToPushAround(selected, dir, driftDirection, occupied, 0, 0, 10, ref flush))
            {
                flushed = true;
                selected = selected.Add(flush);
            }

            //2. Calculate relative position
            var offset = AnchorRelativeToDirection(branch, selected, flipX, flipY);
            string relative = offset.ToTuple().ToString();

            //3. Check the surroundings for overlaps or gaps...
            string collisionReport = "";
            bool bang = Collision(occupied, offset, width, height, dir, ref collisionReport, out Point adjust);
            if (bang)
            {
                //Overlap detected...
                if (adjust.IsEmpty)
                {
                    //This means this slot was boxed in! Last attempt to place it...
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing #{territory.id}: there wasn't enough space for it! Attempting a forced push...");
                    if (!TryToPushAround(offset, branch, dir, occupied, width, height, 20, ref adjust))
                    {
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"...still couldn't place it over the next 20 tiles! Giving up.");
                        return offset;
                    }
                    else
                    {
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"...moved it by {adjust.ToTuple()}.");
                    }
                }
                //Move to avoid overlaps...
                offset = offset.Add(adjust);
            }
            else if (!flushed)
            {
                //Look for a gap against the parent and move to close it, as needed.
                if (TryToPushAround(offset, dir.OppositeDir(), Direction.Undefined, occupied, width, height, 5, ref adjust))
                {
                    offset = offset.Add(adjust);
                    attracted = true;
                }
            }

            //4. Drift, if possible, around the center
            int fixedDrift = isSequence ? 1 : 0;
            if (dir != GetDriftDirection(offset) && Drift(offset, out drift, fixedDrift))
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
            List<string> report = [$"#{territory.id} inserted @ {result.ToTuple()}, from {slot}{dir}"];
            if (flushed) report.Add($"moved to uncover by {flush.ToTuple()}");
            report.Add($"anchor @ {relative}");
            if (drifted) report.Add($"drifted by {drift.ToTuple()}");
            if (bang) report.Add(collisionReport);
            if (attracted) report.Add($"moved to fill the gap by {adjust.ToTuple()}");
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, string.Join(" -> ", report) + ".");

            return result;
        }

        private bool Collision(SortedSet<cPoint> occupied, Point position, int width, int height, Direction preferredDir, ref string report, out Point pushVector)
        {
            var hexPos = position.WiggleToFit(occupied); //not fitting could result in undetected collisions
            var hits = ScanForCollisions(occupied, hexPos, width, height);
            pushVector = Point.Empty;
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
            report = $"Collisions @ {hexPos.ToTuple()} -> horizontal push: {pushVector.X}";
            return true;

            pushY:
            pushVector = new Point(0, pushY);
            report = $"Collisions @ {hexPos.ToTuple()} -> vertical push: {pushVector.Y}";
            return true;
        }

        private bool Drift(Point pos, out Point vector, int fixDrift = 0, int maxDrift = 3)
        {
            //Push the position (pseudo)clockwise based on the distance to the center.

            //Escape if edge case
            if (pos.IsEmpty || pos.X == pos.Y)
            {
                vector = Point.Empty;
                return false;
            }
            int moveX = fixDrift;
            int moveY = fixDrift;
            if (fixDrift == 0)
            {
                //Bases
                float limit = GridFrameBounds.maxY * 1.25f; //not bothering with X because the maps fits a horizontal rectangle.

                //Distance on the Y axis determines drift in the X axis, and vice-versa
                //Smoothstep easing for values near zero to remain zero while larger values reach maxDrift
                float normY = Math.Clamp(Math.Abs(pos.Y) / limit, 0f, 1f);
                float easedY = normY * normY * (3f - 2f * normY); // smoothstep
                float scaledY = easedY * maxDrift;
                moveX = scaledY < 0.3f ? 0 : (int)MathF.Round(scaledY);
                moveX = Math.Min(maxDrift, moveX);

                float normX = Math.Clamp(Math.Abs(pos.X) / limit, 0f, 1f);
                float easedX = normX * normX * (3f - 2f * normX);
                float scaledX = easedX * maxDrift;
                moveY = scaledX < 0.3f ? 0 : (int)MathF.Round(scaledX);
                moveY = Math.Min(maxDrift, moveY);
            }
            vector = GetDriftVector(pos, GetDriftDirection(pos), moveX, moveY);
            return !vector.IsEmpty;
        }

        private Direction GetDriftDirection(Point pos)
        {
            //Direction is based on position, 45 degrees quadrants.
            if (InsideSquare(pos) && pos.Y > pos.X && pos.Y > -pos.X || pos.X < SquareBoundary && pos.Y > SquareBoundary) //Above: move right then down
            {
                return Direction.Right;
            }
            else if (InsideSquare(pos) && pos.Y < pos.X && pos.Y < -pos.X || pos.X > -SquareBoundary && pos.Y < -SquareBoundary) //Below: move left then up
            {
                return Direction.Left;
            }
            else if (InsideSquare(pos) && pos.X > pos.Y && pos.X > -pos.Y || pos.X > SquareBoundary && pos.Y > -SquareBoundary) //Right: move down then left
            {
                return Direction.Down;
            }
            else if (InsideSquare(pos) && pos.X < pos.Y && pos.X < -pos.Y || pos.X < -SquareBoundary && pos.Y < SquareBoundary) //Left: move up then right
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
            return Point.Empty;
        }

        private void HandleMisplaced()
        {
            if (misplaced.Count == 0) return;
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{misplaced.Count} clusters were misplaced and will be set aside...", true);
            int y = 1;
            foreach (var c in misplaced)
            {
                c.Position = occupiedMax.Add(new Point(gap, y * VertGap)).FitToHex();
                y++;
                if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{c.Name}, from territory #{c.AssignedTerritoryId}-{territories[c.AssignedTerritoryId].seed.Name}, placed @ ({c.Position.ToTuple()}).");
                }
                else
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: couldn't place {c.Name}, territory #{c.AssignedTerritoryId}, anywhere! Last attempt: {c.Position.ToTuple()}.");
                }
            }
        }

        private bool HasAncestorStaged(string path, out string found)
        {
            var generation = path.Length;
            if (generation <= 2) goto fail; //that would just return the trunk or main branch, in which case regular beahviour will do.
            for (var i = generation - 1; i > 1; i--)
            {
                var tested = path.GetAddressAtDepth(i);
                if (staged.ContainsKey(tested))
                {
                    found = tested;
                    return true;
                }
            }
            fail:
            found = path;
            return false;
        }

        private List<(Point position, string address)> NextSlotsHelix(Territory territory, SortedSet<cPoint> occupied, string parentAddress)
        {
            var branch = parentAddress.GetMainBranch();
            bool firstRun = branch == Direction.Undefined;
            bool quadrant = branch != helixLastBranch;
            bool cycle = quadrant && branch == Direction.Right;
            var ax = territory.anchor.X;
            var ay = territory.anchor.Y;
            var width = territory.size.X;
            var height = territory.HeightToFit;
            var slots = new List<(Point pos, string add)>();
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
                    var add = parentAddress.DownstreamAddress(dir);
                    slots.Add((slot, add));
                    log.Add($"{slot.ToTuple()}/{dir}/{add}");
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
                _ = Toolbox.LogAsync(level, $"No Slots found for #{territory.id}! Branch: {branch})");
            }
            _ = Toolbox.LogAsync(level, $"Slots around #{territory.id}: {string.Join(", ", log)} (branch: {branch}, gen: {helixGeneration}).");

            //House keeping
            helixLastBranch = branch;

            return slots.ToList();
        }

        private string RefreshStage(string branch, string path, bool sequencesAllowed, ref bool newSequence)
        {
            var regularDomains = domains.Where(x => !sequentialDomains.Contains(x.Key));
            bool holdSequences = !sequencesAllowed && regularDomains.Any();
            var set = holdSequences ? regularDomains.Random() : domains.Random();
            if (set.Value == null)
            {
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: selected domain has a null list! Looking for branch {branch.ToString()}, sequencesAllowed={sequencesAllowed}, {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]"))}");
            }
            if (sequencesAllowed && sequentialDomains.Contains(set.Key) /*&& !staged.ContainsKey(path)*/)
            {
                //It's a sequence, needs own branch.
                staged.Add(path, set.Value);
                domains.Remove(set.Key);
                branch = path;
                newSequence = true;
            }
            else
            {
                //New set replaces the depleted one.
                staged[branch] = set.Value;
                domains.Remove(set.Key);
            }
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Starting a new domain - [{string.Join(", ", set.Value)}]. Draw order will be {(newSequence ? "SEQUENTIAL" : "random")}. New branch is {branch}.", true);
            return branch;
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

        private bool TryToPushAround(Point position, Direction primaryDir, Direction secondaryDir, SortedSet<cPoint> occupied, int width, int height, int maxPush, ref Point vector)
        {
            if (primaryDir == Direction.Undefined) return false;
            bool placed = false;
            bool singleTile = width <= 1 && height <= 2;
            for (int i = 1; i < maxPush; i++)
            {
                bool sucess = false;
                Point target = Point.Empty;
                Direction chosenDir = new Direction();
                Point forced1 = MoveIntoDirection(primaryDir, position, i);
                if (singleTile && !occupied.Contains(forced1) || !SimpleCollision(occupied, forced1, width, height))
                {
                    sucess = true;
                    target = forced1;
                    chosenDir = primaryDir;
                }
                else if (secondaryDir != Direction.Undefined)
                {
                    Point forced2 = MoveIntoDirection(secondaryDir, position, i);
                    if (singleTile && !occupied.Contains(forced2) || !SimpleCollision(occupied, forced2, width, height))
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
        #endregion
    }
}