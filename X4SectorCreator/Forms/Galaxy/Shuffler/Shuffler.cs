using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using X4SectorCreator.Forms.Galaxy.ProceduralGeneration;
using X4SectorCreator.Forms.Galaxy.ProceduralGeneration.Algorithms.GateAlgorithms;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;

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
        private static int maxSlotAdjust = 7, squareBoundary = -1, IntraDomainsDistFactor = 3;
        private static float gateMaxDist = 40f; // when reaching for connections within domains, it will be multiplied by IntraDomainsDistFactor.
        private readonly Func<Territory, Cluster, bool> IsOutside = (territory, cluster) =>
        {
            return !territory.Clusters.Contains(cluster);
        };
        private Dictionary<int, List<int>> domains = [];
        private int helixGeneration = 1, periodicConnectionModule = 4;
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
        private Point occupiedMax;
        private HashSet<int> sequentialDomains = [];
        private Dictionary<string, List<int>> staged = [];
        private Dictionary<int, Territory> territories = [];
        private static Dictionary<string, string> policeFactions = [], sequencesInParalelBranches = [];

        private GateBuilderMST GateBuilder = new GateBuilderMST(new ProceduralSettings
        {
            Seed = Localisation.GetFnvHash(Random.Shared.Next().ToString()),
            MinGatesPerSector = 1,
            MaxGatesPerSector = 1,
            GateMultiChancePerSector = 0
        });

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            // Gather some basic info
            hexGridFrame = ClusterManager.FrameHexGrid(clusters.ToList());
            _ = Toolbox.LogAsync("Initializing", $"cols = {hexGridFrame.cols} x rows = {hexGridFrame.rows}", true);

            // Group clusters into territories based adjacency and DLCs
            CarveTerritories(clusters);

            // Determine if there are neighboring territories owned by the same faction.
            FindAnnexed();

            // Determine if there are other close territories owned by the same faction and separated by only a neutral sector.
            FindCloseColonies();

            // Consolidate neighbouring territories into one (following DLC criteria) or under merged domains (if owner is shared).
            ConsolidateDomains();

            // Now we're able to calculate territories' preferred orientations:
            InferOrientations();

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

        private static Dictionary<string, string> PoliceFactions
        {
            get
            {
                if (policeFactions.Count == 0)
                {
                    if (FactionsForm.AllCustomFactions.Count > 0)
                    {
                        policeFactions = FactionsForm.AllCustomFactions.ToDictionary(f => f.Key, f => f.Value.PoliceFaction);
                    }
                    foreach (var faction in AdditionalVanillaMapping.GetDefaultPolice().Where(x => !x.Value.Equals("none", StringComparison.OrdinalIgnoreCase)))
                    {
                        policeFactions[faction.Key] = faction.Value;
                    }
                }
                return policeFactions;
            }
        }

        #region territories

        private static bool SharedOwner(string owner, string targetOwner)
        {
            return !owner.Equals("none") && !targetOwner.Equals("none") && owner.Equals(targetOwner);
        }

        private static bool ArePredeterminedNeighbors(string owner, string targetOwner)
        {
            return AdditionalVanillaMapping.ObligateNeighborFactions.ContainsKey(owner)
                && AdditionalVanillaMapping.ObligateNeighborFactions[owner] == targetOwner;
        }

        private static bool IsVassal(string owner, string targetOwner)
        {
            return AdditionalVanillaMapping.VassalFactions.ContainsKey(owner)
                && AdditionalVanillaMapping.VassalFactions[owner] == targetOwner;
        }

        private static bool ArePredeterminedClusterPair(Cluster subject, Cluster target)
        {
            var subjectName = subject.Name;
            var targetName = target.Name;
            bool flag = (AdditionalVanillaMapping.ObligateClusterPairs.ContainsKey(subjectName)
                && AdditionalVanillaMapping.ObligateClusterPairs[subjectName] == targetName)
                || (AdditionalVanillaMapping.ObligateClusterPairs.ContainsValue(subjectName)
                && AdditionalVanillaMapping.ObligateClusterPairs.ReverseLookup(subjectName).First() == targetName);
            return flag;
        }
       
        private static bool ShouldMergeByPolice(Sector origin, Sector destination)
        {
            //works for Terrans and Avarice
            var owner = origin.CurrentOwner.ToLower();
            var targetOwner = destination.CurrentOwner.ToLower();
            return PoliceFactions.ContainsKey(targetOwner) && owner.Equals(PoliceFactions[targetOwner]);
        }

        private static bool ShouldMergeByDLC(Territory selected, Territory target)
        {
            return !string.IsNullOrWhiteSpace(selected.dlc) 
                && !string.IsNullOrWhiteSpace(target.dlc) 
                && selected.dlc.Equals(target.dlc, StringComparison.OrdinalIgnoreCase)
                && selected.SameOwner
                && target.SameOwner;
        }


        private void CarveTerritories(IEnumerable<Cluster> clusters)
        {
            Action<Cluster, bool> SortTerritory = (cluster, reset) =>
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
            
            Func<Cluster, HashSet<Cluster>, IEnumerable<Cluster>> GetNeighbors = (location, crowd) =>
            {
                var targetPositions = NeighborOffsets
                    .Select(offset => new Point(location.Position.X + offset.x, location.Position.Y + offset.y))
                    .ToHashSet();
                return crowd.Where(cluster => targetPositions.Contains(cluster.Position));
            };

            Func<(Cluster, Cluster), bool> AreConnected = (pair) =>
            {
                bool flag = false;
                var clusterA = pair.Item1;
                var clusterB = pair.Item2;
                foreach (var gate in clusterA.FindGates())
                {
                    flag = gate.DestinationSector.Parent.Equals(clusterB);
                    if (flag) break;
                }
                return flag;
            };

            var ordered = clusters.OrderBy(x => x.Position.DistanceSquaredOnHexGrid(Point.Empty)).ToList();
            Toolbox.FlexFloodProcessor(ordered, SortTerritory, GetNeighbors, null, x => AreConnected(x));
        }

        private void ConsolidateDomains()
        {
            // Build groups by merging any overlapping sets into larger ones.
            List<HashSet<int>> groups = MergeOverlappingDomains();

            // filter out and merge the domains that ask for it.
            List<HashSet<int>> groupsLeft = MergeAndFilterOutDomains(groups);

            // rebuild the dictionary so each entry is a consolidated domain.
            Dictionary<int, List<int>> consolidated = [];
            int idx = 0;
            foreach (var g in groupsLeft)
            {
                idx++;
                consolidated.Add(idx, /*g.OrderBy(x => Random.Shared.Next()).ToList()*/SequencedDomainFromHash(idx, g));
            }
            domains = DesignatedDomains(consolidated);

            // logging
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Consolidated {domains.Count} domain(s): {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]{(sequentialDomains.Contains(x.Key) ? "S" : "")}"))}.");
        }

        private List<int> SequencedDomainFromHash(int idx, HashSet<int> group)
        {
            List<int> result = [];
            if (KeepSequence(group.Select(x => territories[x]).ToList()))
            {
                result = group.OrderBy(x => x).ToList();
                sequentialDomains.Add(idx); //note that down for later.
            }
            else
            {
                result = group.OrderBy(x => Random.Shared.Next()).ToList();
            }
            return result;
        }

        private List<HashSet<int>> MergeOverlappingDomains()
        {
            List<HashSet<int>> mergedGroups = [];

            Action<HashSet<int>, bool> MergeDomains = (entry, reset) =>
            {
                if (reset) mergedGroups.Add(entry);
                else mergedGroups.Last().UnionWith(entry);
            };

            Func<HashSet<int>, HashSet<HashSet<int>>, IEnumerable<HashSet<int>>> GetIntersecting = (entry, crowd) =>
            {
                return crowd.Where(x => entry.Intersect(x).Any());
            };

            Toolbox.FlexFloodProcessor(domains.Values.Select(e => e.ToHashSet()).ToList(), MergeDomains, GetIntersecting);
            return mergedGroups;
        }

        private List<HashSet<int>> MergeAndFilterOutDomains(List<HashSet<int>> groups)
        {
            List<HashSet<int>> result = [];
            foreach (var g in groups)
            {
                if (g.Count > 1 && g.Any(x => territories[x].toMerge))
                {
                    var extracted = g.Where(x => territories[x].toMerge).ToHashSet();
                    var leftovers = g.Where(x => !territories[x].toMerge).ToHashSet();
                    int replacement = -1;
                    // If there are at least 2 to merge, then proceed:
                    if (extracted.Count > 1)
                    {
                        replacement = MergeDomain(extracted);
                    }
                    // Somehow, there's only 1 to merge, in which case nothing happens.
                    else
                    {
                        result.Add(g);
                        continue;
                    }
                    // If there are unmerged items left
                    if (leftovers.Count > 0)
                    {
                        // First, fix the now outdated annexedIds registries.
                        extracted.Remove(replacement);
                        foreach (var id in leftovers)
                        {
                            var territory = territories[id];
                            // On territories that were connected (not all of them), replace merged ids with the remaining one. 
                            if (extracted.Intersect(territory.annexedIds).Any())
                            {
                                territory.annexedIds.RemoveAll(extracted.Contains);
                                territory.annexedIds.Add(replacement);
                                // Also add back its id to the newly merged. 
                                territories[replacement].annexedIds.AddUnique(territory.id);
                            }
                        }
                        // Bring back the merged territory
                        if (replacement > 0) leftovers.Add(replacement);
                        result.Add(leftovers);
                    }
                    // All items were merged, so we just put it back as one.
                    else
                    {
                        result.Add(new HashSet<int>() { replacement });
                    }
                }
                else
                {
                    result.Add(g);
                }
            }
            return result;
        }

        private int MergeDomain(HashSet<int> g)
        {
            var lead = g.First();
            var territory = territories[lead];
            List<int> absorbed = [];
            foreach (var i in g.Skip(1))
            {
                var target = territories[i];
                territory.Absorb(target);
                absorbed.Add(i);
            }
            territory.annexedIds.RemoveAll(absorbed.Contains);
            territory.closeColonyIds.Clear();
            List<string> absorbedReport = absorbed.Select(x => $"#{x}({territories[x].seed.Name})").ToList();
            string separator = absorbed.Count() == 2 ? " and " : ", ";
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"{string.Join(separator, absorbedReport)} merged into #{territory.id}({territory.seed.Name})");
            foreach (var i in absorbed)
            {
                territories.Remove(i);
            }
            return lead;
        }

        private Dictionary<int, List<int>> DesignatedDomains(Dictionary<int, List<int>> set)
        {
            if (!set.Any()) return set;
            foreach (var d in set)
            {
                foreach (var id in d.Value)
                {
                    if (!territories.ContainsKey(id)) continue;
                    var territory = territories[id];
                    territory.assignedDomainId = d.Key;
                    territory.peers = d.Value.Where(x => x != territory.id).ToList();
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
            List<(Territory territory, Cluster cluster, Sector origin, Sector destination)> connections = territories.Values
                .SelectMany(t => t.Connections
                .Select(c => (t, c.cluster, c.origin, c.destination)))
                .ToList();
            //List<int> annexed = [];
            foreach (var (territory, cluster, origin, destination) in connections)
            {
                var targetId = destination.AssignedTerritoryId;
                if (targetId < 1) continue;
                bool mandatory = ArePredeterminedClusterPair(cluster, destination.Parent);
                if (!mandatory && (origin.IsNeutral || destination.IsNeutral)) continue;
                var target = territories[targetId];
                if (target != null)
                {
                    var owner = origin.CurrentOwner.ToLower();
                    var targetOwner = destination.CurrentOwner.ToLower();
                    if (owner == null/* || annexed.Contains(targetId)*/) continue;
                    bool toMerge = mandatory
                        || (ShouldMergeByDLC(territory, target)
                        && (ShouldMergeByPolice(origin, destination)
                        || ((target.Landlocked || territory.Landlocked) && SharedOwner(owner, targetOwner))));
                    bool sameOwner = SharedOwner(owner, targetOwner)
                        || ArePredeterminedNeighbors(owner, targetOwner)
                        || IsVassal(owner, targetOwner);
                    if (toMerge || sameOwner)
                    {
                        territory.annexedIds.AddUnique(targetId);
                        //annexed.Add(targetId);
                        target.annexedIds.AddUnique(territory.id);
                        var key = domains.Count + 1;
                        domains.Add(key, [territory.id, targetId]);
                                territory.toMerge |= toMerge;
                                target.toMerge |= toMerge;
                    }
                }
            }
        }

        private void FindCloseColonies()
        {
            Dictionary<int, string> absorbed = [];
            var candidates = territories.Values
                .SelectMany(t => t.bordering)
                .Where(c => c.ExitPoints?.Count > 1 && c.Exits.All(s => s.IsNeutral))
                .ToList();
            foreach (var cluster in candidates)
            {
                var connected = cluster.Destinations
                    .Where(s => !s.IsNeutral && !s.CurrentOwner.Equals("Xenon", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(s => s.CurrentOwner)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Select(s => territories[s.AssignedTerritoryId]).ToList())
                    .ToList();
                if (!connected.Any()) continue;
                //merge only if only one set of owned territories is being bridged
                bool toMerge = connected.Count == 1;
                foreach (var grouped in connected)
                {
                    foreach (var neighbor in grouped)
                    {
                        if (toMerge)
                        {
                            neighbor.toMerge = true;
                        }
                        else
                        {
                            neighbor.closeColonyIds.AddRangeUnique(grouped.Except([neighbor]).Select(x => x.id));
                        }
                    }
                    var bridged = grouped.Select(x => x.id).ToList();
                    cluster.BridgeFor.AddRange(bridged);
                    var bridge = territories[cluster.AssignedTerritoryId];
                    bridge.isBridge = true;
                    bridge.toMerge = toMerge;
                    var extents = bridged.Append(bridge.id).ToList();
                    var id = domains.Count + 1;
                    domains.Add(id, extents);
                }
            }
        }

        private static bool KeepSequence(List<Territory> set)
        {
            //Spares certain domain sets from spawning in randomized order
            return set.Count > 1
                && (set.Any(x => x.isBridge) // unmerged close colonies
                || (set.Any(x => x.annexedIds.Count > 0) && set.All(x => !string.IsNullOrWhiteSpace(x.dlc)))); // annexed + DLC
        }

        private void TerritoriesReport()
        {
            var log = new StringBuilder();
            log.AppendLine($"\n\n--- Territories ---\n");
            foreach (var t in territories.Values)
            {
                string ownerName = t.SameOwner ? t.seed.Sectors[0].CurrentOwner : "Divided";
                var owner = t.IsNeutral ? "Neutral" : ownerName;
                log.Append($"#{t.id} - {t.seed.Name}, {owner}: {t.Clusters.Count} clusters, {t.bordering.Count} connecting, facing {t.exitDirection}{(string.IsNullOrEmpty(t.dlc) ? "" : $", dlc: {t.dlc}")}{(PoliceFactions.ContainsKey(owner.ToLower()) ? $", police: {PoliceFactions[owner.ToLower()]}" : "")}");
                if (t.annexedIds.Count > 0) log.Append($"; annexed to {string.Join(", ", t.annexedIds.Select(x => $"#{territories[x].id}-{territories[x].seed.Name}"))}");
                if (t.isBridge) log.Append($"; bridges {string.Join(", ", t.Clusters.First(x => x.BridgeFor.Count > 0).BridgeFor.Select(y => $"#{territories[y].id}-{territories[y].seed.Name}"))}");
                if (t.closeColonyIds.Count > 0) log.Append($"; colonies {string.Join(", ", t.closeColonyIds.Select(x => $"#{territories[x].id}-{territories[x].seed.Name}"))}");
                log.AppendLine(".");
            }
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, log.ToString());
        }

        private void InferOrientations()
        {
            foreach (var domain in domains.Values)
            {
                for (int i = 0; i < domain.Count; i++)
                {
                    if (territories.ContainsKey(domain[i]))
                    {
                        var territory = territories[domain[i]];
                        territory.SetUpDirection(i > 0);
                    }
                }
            }
        }

        #endregion

        #region shuffler
        internal Territory PickNextFromStaged(string path, ref int skipTracker, out bool isResuming)
        {
            bool domsRemain = domains.Count > 0;
            bool stagedRemain = staged.Count > 0;

            //Bail out if something hasn't been intialized or the lists have been exausted.
            if (!domsRemain && stagedRemain && staged.Values.All(x => x.Count == 0))
            {
                isResuming = false;
                return null;
            }

            isResuming = IsCloseToHome(path, out string ancestor); //detects if the current slot connects with an ongoing sequence.
            var branch = isResuming ? ancestor : path.GetAddressAtDepth(1); //selects either the main branch or the divergence point for a sequence.
            if (string.IsNullOrEmpty(branch) || branch == "0")
            {
                branch = "1"; //prevents the domain called at the origin from generating a dead-end entry.
            }
            if (!staged.ContainsKey(branch))
            {
                staged.Add(branch, new List<int>());
            }
            if (staged[branch].Count == 0)
            {
                //This queue is empty! Cross out that branch (it will be re-added automatically later if needed) and bail out.
                staged.Remove(branch);

                //Also clean up any eventual notes on parallel branches.
                var obsoleteParallels = sequencesInParalelBranches.ReverseLookup(branch);
                if (obsoleteParallels.Any())
                {
                    foreach (var key in obsoleteParallels)
                    {
                        sequencesInParalelBranches.Remove(key);
                    }
                }
                if (!domsRemain)
                {
                    skipTracker++;
                    return null;
                }
                //And any other non-root emptied-out branches.
                var depleted = domains.Where(x => x.Key > 9 && x.Value.Count == 0).Select(x => x.Key).ToList();
                if (depleted.Count > 0)
                {
                    foreach (var key in depleted)
                    {
                        domains.Remove(key);
                    }
                }
                //_ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Branch: {branch}. isResuming = {isResuming}. Staged domains:\n{string.Join(", ",staged.Select(x => $"#{x.Key}[{string.Join(",",x.Value)}]"))}",true);

                //Load another set:
                //NOTE: A new sequence starts here. It both selects the sequence set and changes the branch, creating a divergence.
                branch = RefreshStage(branch, path);
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
            var card = selected.First();
            staged[branch].Remove(card);
            //_ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Selected {territories[card].seed.Name} for path {path}. skipTracker={skipTracker}, isResuming={isResuming}, ancestor={ancestor}, branch={branch}.", true);
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
            Queue<(Point pos, string path)> slots = new([(Point.Empty, "0")]), deferred = [];
            SortedSet<cPoint> occupied = [];
            bool inBounds = true, firstRun = true;
            List<Cluster> misplaced = [], orphanedRoads = [], secondaryRoads = [], unconnected = [];
            List<ImmutableList<int>> domainsList = domains.Values.Where(x => x.Count > 1).Select(x => x.ToImmutableList()).ToList();


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
                    territory = PickNextFromStaged(slot.path, ref skipTracker, out isSequence);
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
                            _ = Toolbox.LogAsync(level, $"ERROR: we've run out of slots! Staged domains left out: {string.Join(", ", staged.Select(x => $"#{x.Key}[{string.Join(",", x.Value)}]"))}");
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
                    _ = Toolbox.LogAsync(level, $"Assigning #{territory.id} - {territory.seed.Name}, size={territory.size.ToTuple()},{territory.Clusters.Count} clusters");
                }
                else
                {
                    _ = Toolbox.LogAsync(level, $"ERROR: we've run out of territories to assign to slots!");
                    break;
                }

                //Rotate it as needed.
                if (direction != Direction.Undefined && territory.exitDirection != Direction.Undefined)
                {
                    var entryDirection = territory.exitDirection.OppositeDir();
                    if (entryDirection != direction)
                    {
                        var turns = entryDirection.ClockwiseStepsTo(direction);
                        territory.Rotate(turns);
                    }
                }

                //Fine-tune the insertion spot so it fits right in.
                var currentPos = territory.Anchor;
                var planned = position.Subtract(currentPos);
                if (!firstRun) position = AdjustForInsertion(territory, planned, branch, direction, occupied, isSequence);

                //Mark the first territory, so its Anchor property doesn't go into a loop of constant re-evaluation.
                else territory.origin = true;

                //Move the piece
                var move = position.Subtract(currentPos);
                var report = territory.Reposition(move);
                _ = Toolbox.LogAsync(level, report);

                //Keep track of occupied areas
                var covered = territory.Contour;
                if (firstRun) occupied.Clear();
                occupied.UnionWith(covered);
                occupiedMax = occupied.Max;

                //Reporting covered tiles
                var cMaxY = covered.MaxBy(p => p.Y).Y;
                var cMinY = covered.MinBy(p => p.Y).Y;
                var cMaxX = covered.MaxBy(p => p.X).X;
                var cMinX = covered.MinBy(p => p.X).X;
                _ = Toolbox.LogAsync(level, $"{covered.Count} tiles were covered, {cMinX} to {cMaxX} horizontal, {cMinY} to {cMaxY} vertical, totalling {occupied.Count} now.");

                //Update the board.
                UpdateClusterMap(territory.Clusters, misplaced);

                //Redo connections
                var reachAcross = !firstRun /*&& helixGeneration % periodicConnectionModule == 0*/ && direction == branch;
                Direction reachDir = reachAcross ? (Direction)(((int)direction + 3) % 4) : Direction.Undefined;
                Reconnect(territory, reachDir, firstRun, orphanedRoads, secondaryRoads);

                //Prepare the next slots.
                var nextSlots = NextSlotsHelix(territory, occupied, path);

                //bool isSequential = sequentialDomains.Contains(territory.AssignedDomainId);
                foreach (var (pos, add) in nextSlots)
                {
                    if (InBounds(pos) || isSequence) slots.Enqueue((pos, add));
                    else deferred.Enqueue((pos, add));
                }
                firstRun = false;
            }
            HandleMisplaced(misplaced);
            orphanedRoads.RemoveAll(x => x.PossibleExits.Count == 0);
            var delayed = DelayedBridges(orphanedRoads, secondaryRoads);
            _ = Toolbox.LogAsync(level, (delayed > 0 ?
                $"{delayed} more gate connections were made for territories that ended up isolated." : "No isolated territories after shuffling.")
                + " The network will now be stitched together...",true);
            FinalizeNetwork(domainsList);
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

        private static Point MoveIntoResultingDirection(Direction directionA, Direction directionB, Point position, int distance)
        {
            if (directionA.OppositeDir() == directionB) return position;
            var componentA = MoveIntoDirection(directionA, Point.Empty, distance);
            var componentB = MoveIntoDirection(directionB, Point.Empty, distance);
            return position.Add(componentA).Add(componentB);
        }

        private Point AdjustForInsertion(Territory territory, Point displacement, Direction branch, Direction dir, SortedSet<cPoint> occupied, bool isSequence)
        {
            var selected = territory.Anchor.Add(displacement);
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
            var outDir = GetOutwardDirection(selected);
            var driftDir = GetDriftDirection(selected);
            var flush = Point.Empty;
            if (occupied.Contains(selected) && TryToPushAround(selected, outDir, driftDir, occupied, 0, 0, maxSlotAdjust, ref flush))
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
                if (TryToPushAround(offset, dir.OppositeDir(), Direction.Undefined, occupied, width, height, width, ref adjust))
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
        private Direction GetOutwardDirection(Point pos)
        {
            //Direction is based on position, 45 degrees quadrants.
            if (InsideSquare(pos) && pos.Y > pos.X && pos.Y > -pos.X || pos.X < SquareBoundary && pos.Y > SquareBoundary)
            {
                return Direction.Up;
            }
            else if (InsideSquare(pos) && pos.Y < pos.X && pos.Y < -pos.X || pos.X > -SquareBoundary && pos.Y < -SquareBoundary)
            {
                return Direction.Down;
            }
            else if (InsideSquare(pos) && pos.X > pos.Y && pos.X > -pos.Y || pos.X > SquareBoundary && pos.Y > -SquareBoundary)
            {
                return Direction.Right;
            }
            else if (InsideSquare(pos) && pos.X < pos.Y && pos.X < -pos.Y || pos.X < -SquareBoundary && pos.Y < SquareBoundary)
            {
                return Direction.Left;
            }
            return Direction.Undefined;
        }

        private Direction GetDriftDirection(Point pos)
        {
            return (Direction)(((int)GetOutwardDirection(pos) + 1) % 4);
        }

        private Point GetDriftVector(Point position, Direction dir, int moveX, int moveY)
        {
            int yaw = 0;
            switch (dir)
            {
                case Direction.Right: //Above: move right then down
                    if (position.X > 0) yaw = moveY;
                    return new Point(moveX, -yaw);

                case Direction.Left: //Below: move left then up
                    if (position.X < 0) yaw = moveY;
                    return new Point(-moveX, yaw);

                case Direction.Down: //Right: move down then left
                    if (position.Y < 0) yaw = moveX;
                    return new Point(-yaw, -moveY);

                case Direction.Up: //Left: move up then right
                    if (position.Y > 0) yaw = moveX;
                    return new Point(yaw, moveY);

                case Direction.Undefined:
                    goto Fail;
            }
            Fail:
            return Point.Empty;
        }

        private void HandleMisplaced(List<Cluster> misplaced)
        {
            if (misplaced.Count == 0) return;
            int y = 1;
            foreach (var c in misplaced)
            {
                StringBuilder log = new StringBuilder();
                log.Append($"{c.Name}, from territory #{c.AssignedTerritoryId}-{territories[c.AssignedTerritoryId].seed.Name} couldn't be placed @ {c.Position.ToTuple()}... ");
                if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    log.Append("solved on a second try.");
                }
                else
                {
                    var failed = c.Position; 
                    c.Position = occupiedMax.Add(new Point(gap, y * VertGap)).FitToHex();
                    y++;
                    if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                    {
                        log.Append($"pushed aside and placed @ {c.Position.ToTuple()}.");
                    }
                    else
                    {
                        log.Append($"ERROR: failure to push it aside. We couldn't place it anywhere! Last attempt: {c.Position.ToTuple()}.");
                    }
                }
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, log.ToString(), true);
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

        private bool IsCloseToHome(string path, out string found)
        {
            if (path.Length < 2) goto Fail;
            var paralel = path.GetParalelOlderAddress();
            if (paralel.GetMainBranch() == path.GetMainBranch())
            {
                if (sequencesInParalelBranches.ContainsKey(paralel))
                {
                    found = sequencesInParalelBranches[paralel];
                    goto Parallel;
                }
                else if (staged.ContainsKey(paralel))
                {
                    found = paralel;
                    goto Parallel;
                }
                else if (HasAncestorStaged(paralel, out found)) goto Parallel;
            }
            var parent = path.GetParentAddress();
            if (sequencesInParalelBranches.ContainsKey(parent))
            {
                found = sequencesInParalelBranches[parent];
                return true;
            }
            else if (staged.ContainsKey(parent))
            {
                found = parent;
                return true;
            }
            else if (HasAncestorStaged(parent, out found)) return true;
            
            Fail:
            found = path;
            return false;

            Parallel:
            sequencesInParalelBranches.TryAdd(path, found);
            return true;
        }

        private List<(Point position, string address)> NextSlotsHelix(Territory territory, SortedSet<cPoint> occupied, string parentAddress)
        {
            var branch = parentAddress.GetMainBranch();
            var lastDir = parentAddress.GetDirection();
            bool firstRun = branch == Direction.Undefined;
            bool quadrant = branch == lastDir;
            bool cycle = quadrant && branch == Direction.Right;
            var ax = territory.Anchor.X;
            var ay = territory.Anchor.Y;
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

            return slots.ToList();
        }

        private string RefreshStage(string branch, string path)
        {
            var set = domains.Random();
            if (set.Value == null)
            {
                _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: selected domain has a null list! Looking for branch {branch.ToString()}, {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]"))}");
            }
            if (set.Value.Count > 1 && !path.Equals("0") && !staged.ContainsKey(path))
            {
                //It's a sequence, needs own branch.
                staged.Add(path, set.Value);
                domains.Remove(set.Key);
                branch = path;
            }
            else
            {
                //New set replaces the depleted one.
                staged[branch] = set.Value;
                domains.Remove(set.Key);
            }
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Starting a new domain - [{string.Join(", ", set.Value)}]. Loaded to branch {branch}.", true);
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
            bool singleTile = width <= 1 && height <= 2;

            bool valid = false;
            Point tPos = Point.Empty;
            Direction tDir = primaryDir;
            int i = 0, dist = 0;
            while (dist < maxPush && !valid)
            {
                int mi = i % 3;
                if (mi == 0) dist++;
                if (mi == 1)
                {
                    tPos = MoveIntoResultingDirection(primaryDir, secondaryDir, position, dist);
                }
                else
                {
                    tDir = mi == 0 ? primaryDir : secondaryDir;
                    tPos = MoveIntoDirection(tDir, position, dist);
                }
                valid = IsValidPlacement(tPos, singleTile, occupied, width, height);
                i++;
            }
            vector = tPos.Subtract(position);
            return valid;
        }

        private bool IsValidPlacement(Point position, bool singleTile, SortedSet<cPoint> occupied, int width, int height)
        {
            return singleTile ? !occupied.Contains(position) : !SimpleCollision(occupied, position, width, height);
        }

        private void UpdateClusterMap(List<Cluster> clusters, List<Cluster> misplaced)
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

        #region Reconnections
        private void    Reconnect(Territory territory, Direction alsoReachFor, bool firstRun, List<Cluster> orphanedRoads, List<Cluster> secondaryRoads)
        {
            HashSet<Cluster> outgoing = [];
            if (territory.ExitGates != null && !territory.isBridge)
            {
                foreach (var link in territory.Connections)
                {
                    outgoing.Add(link.cluster);
                    var gate = link.gate;
                    var zone = gate.ParentZone;
                    zone.Gates.Remove(gate);
                }

                bool FindBridgeManagePools(HashSet<Cluster> originCandidates, List<Cluster> destinationsPool, bool removeDestination, out (Cluster from, Cluster to) bridge, Predicate<(Cluster, Cluster)> filter = null)
                {
                    bool result = false;
                    bridge = FindBridge(originCandidates, destinationsPool, out result, gateMaxDist, filter);
                    if (result && bridge.from != null && bridge.to != null)
                    {
                        if (removeDestination)
                        {
                            destinationsPool.Remove(bridge.to);
                            secondaryRoads.Add(bridge.to);
                        }
                        if (outgoing.Contains(bridge.from))
                        {
                            outgoing.Remove(bridge.from);
                            secondaryRoads.Add(bridge.from);
                        }
                    }
                    return result;
                }

                bool connected = false, reachedAcross = false;
                int territoryConnected = -1;
                if (!firstRun)
                {
                    // First, try known & close paths
                    connected = FindBridgeManagePools(outgoing, orphanedRoads, true, out var bridge);
                    if (!connected)
                    {
                        // Then, attempt undesirable but close paths
                        connected = FindBridgeManagePools(outgoing, secondaryRoads, false, out bridge);
                    }
                    if (connected) territoryConnected = bridge.to.AssignedTerritoryId;
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reconnect {territory.seed.Name}: {(connected ? $"SUCESS! Connected to {bridge.to}" : $"FAILED! Outgoing clusters: {outgoing.Count()}")}.");
                }

                if (outgoing.Count > 0 && alsoReachFor != Direction.Undefined)
                {
                    var outgoingSub = outgoing.Where(x => x.Direction != (int)alsoReachFor.OppositeDir()).ToHashSet();
                    if (outgoingSub.Count > 0)
                    {
                        reachedAcross = FindBridgeManagePools(outgoingSub, orphanedRoads, true, out var bridgeAcross, pair => CanConnectFromDirection(pair, alsoReachFor));
                        if (!reachedAcross)
                        {
                            var secondarySub = connected ? secondaryRoads.Where(x => x.AssignedTerritoryId != territoryConnected).ToList() : secondaryRoads;
                            if (secondarySub.Count > 0)
                            {
                                reachedAcross = FindBridgeManagePools(outgoingSub, secondarySub, false, out bridgeAcross, pair => CanConnectFromDirection(pair, alsoReachFor));
                            }
                        }
                        _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reach {alsoReachFor} from {territory.seed.Name}: {(reachedAcross ? $"SUCESS! Connected to {bridgeAcross.to}" : $"FAILED! Outgoing clusters: {outgoingSub.Count()}")}.");
                    }
                }

                if (connected || reachedAcross) secondaryRoads.AddRange(outgoing);
                else orphanedRoads.AddRange(outgoing);
            }
            if (territory.absorbedExits.Count() > 0) secondaryRoads.AddRange(territory.absorbedExits);
            territory.SetUpConnections();
        }

        private (Cluster from, Cluster to) FindBridge(HashSet<Cluster> outgoingHash, List<Cluster> desired, out bool flag, float limit = -1f, Predicate<(Cluster, Cluster)> filter = null)
        {
            (Cluster, Cluster) result = (null, null);
            var outgoing = outgoingHash.Where(x => x.PossibleExits.Count > 0).ToList();
            desired = desired.Where(x => x.PossibleExits.Count > 0).ToList();
            if (outgoing.Count == 0 || desired.Count == 0) goto finish;
            Dictionary<(Cluster, Cluster), float> edges = ClusterManager.BridgedParwiseDistances(outgoing, desired, limit, filter);
            if (edges.Count == 0) goto finish;
            var ((origin, destination), _) = edges.MinBy(x => x.Value);
            GateBuilder.AddGate(origin, origin.PossibleExits.First(), destination, destination.PossibleExits.First());
            result = (origin, destination);

            finish:
            flag = (result.Item1 != null && result.Item2 != null);
            return result;
        }

        private int DelayedBridges(List<Cluster> outgoing, List<Cluster> desired, float limit = -1f)
        {
            var result = 0;
            if (outgoing.Count == 0 || desired.Count == 0) goto finish;

            Dictionary<(Cluster from, Cluster to), float> edges = ClusterManager.BridgedParwiseDistances(outgoing, desired, limit);
            if (edges.Count == 0) goto finish;

            List<Cluster> plugged = [];
            foreach (var origin in outgoing)
            {
                var subset = edges.Where(x => x.Key.from == origin).ToDictionary();
                if (subset.Count == 0) continue;
                (Cluster from, Cluster to) route;
                Cluster destination;
                do
                {
                    route = subset.MinBy(x => x.Value).Key;
                    destination = route.to;
                    subset.Remove(route);
                }
                while (subset.Count > 0 && territories[origin.AssignedTerritoryId].neighbors.Contains(destination.AssignedTerritoryId));
                if (destination == null) continue;
                GateBuilder.AddGate(origin, origin.PossibleExits.First(), destination, destination.PossibleExits.First());
                result++;
                edges.Remove(route);
                var taken = edges.Keys.Where(k => k.from == route.from || k.to == route.to).ToList(); //just one bridge to/from there per execution.
                foreach (var key in taken)
                {
                    edges.Remove(key);
                }
            }
            finish:
            return result;
        }

        private void FinalizeNetwork(List<ImmutableList<int>> domainsList)
        {
            foreach (var territory in territories.Values)
            {
                territory.SetUpConnections();
            }
            StitchNetwork(FindNetworks(territories.Values.ToList()));
            foreach (var set in domainsList)
            {
                StitchNetwork(FindNetworks(set.Select(x => territories[x]).ToList()), gateMaxDist * IntraDomainsDistFactor);
            }
        }

        private List<List<int>> FindNetworks(List<Territory> territories)
        {
            List<List<int>> groups = [];

            Action<Territory, bool> SortGroup = (territory, reset) =>
            {
                if (territory.unconnected) return;
                if (reset)
                {
                    groups.Add(new List<int>() { territory.id });
                    return;
                }
                groups.Last().Add(territory.id);
            };

            Func<Territory, HashSet<Territory>, IEnumerable<Territory>> GetConnected = (subject, crowd) =>
            {
                return crowd.Where(x => subject.neighbors.Contains(x.id));
            };

            Toolbox.FlexFloodProcessor(territories, SortGroup, GetConnected, x => groups.Last().Intersect(x.neighbors).Any());

            //logging
            int count = groups.Count;
            _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Found {(count>1 ? $"{count} groups" : "only one group")} out of {territories.Count} territories.", true);

            return groups;
        }   

        private void StitchNetwork(List<List<int>> patches, float limit = -1f)
        {
            var ordered = patches.OrderBy(x => x.Count());
            Dictionary<int, HashSet<Cluster>> remaining = [];
            int i = 0;
            foreach (var group in ordered)
            {
                HashSet<Cluster> outgoing = [];
                foreach (var idx in group)
                {
                    var relevant = territories[idx].Clusters.Where(c => c.PossibleExits.Count > 0);
                    if (relevant.Count() > 0)
                    {
                        foreach (var cluster in relevant)
                        {
                            outgoing.Add(cluster);
                        }
                    }
                }
                remaining.Add(i++, outgoing);
            }
            while (remaining.Count > 1) //Last one is the bigger, it's all done once we get there.
            {
                var entry = remaining.First();
                var outHash = entry.Value;
                var destList = remaining.Where(x => x.Key != entry.Key).SelectMany(x => x.Value).ToList();
                var bridge = FindBridge(outHash, destList, out bool bridged, limit);
                remaining.Remove(entry.Key);
                if (bridged)
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, $"Stitched {bridge.from} with {bridge.to}.");
                }
                else
                {
                    _ = Toolbox.LogAsync(MethodBase.GetCurrentMethod().Name, limit > 0 ? $"Failed stitching a patch of clusters. Likely because possible connection points were too far apart. Distance was limited to {limit}. Clusters involved: {string.Join(", ",outHash)}" : "ERROR: Failed stitching!");
                }
            }
        }

        public bool CanConnectFromDirection((Cluster from, Cluster to) pair, Direction direction)
        {
            var origin = pair.from;
            var target = pair.to;
            return direction switch
            {
                Direction.Undefined => false,
                Direction.Right => target.Position.X > origin.Position.X,
                Direction.Down => target.Position.Y < origin.Position.Y,
                Direction.Left => target.Position.X < origin.Position.X,
                Direction.Up => target.Position.Y > origin.Position.Y,
                _ => false
            };
        }

        #endregion
    }
}