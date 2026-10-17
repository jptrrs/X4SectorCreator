using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using X4SectorCreator.Forms.Galaxy.ProceduralGeneration;
using X4SectorCreator.Forms.Galaxy.ProceduralGeneration.Algorithms.GateAlgorithms;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static X4SectorCreator.Forms.Galaxy.Shuffler.AdditionalVanillaMapping;
using static X4SectorCreator.Helpers.Toolbox;

namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    internal class Shuffler
    {
        #region Header

        // Configuration
        internal bool
            FreezeMode = false, //For territories that must have their positions preserved, this will merge them into one instead of trying to replicate their sequence. If turned on, the size of those merged teritories causes the map to be less predictable and have wider gaps.
            AllowRotating = false; //Currently, rotating territories doesn't actually rotate the gates, because we have no effect over their positions once exported to XML. So, turning this on will cause connection lines to be all over the place.

        // To Do:
        // A gradient of shuffle intensity:
        // 0. Total shuffle. (maybe also clear vanilla territory ownerships?)
        // 1. Clusters that are clumped together are grouped into larger territories. (CarveTerritories)
        // 2. Keep together territories that belong to the same owner (FindAnnexed), with various criteria, and preserve internal connections:
        // 2a. Only DLC territories are kept together (ShouldMergeByDLC, FindCloseColonies);
        // 2b. Also include subordinate factions: Riptide (ShouldMergeByPolice), Hatikvah and Pioneers (IsVassal);
        // 2c. Also include predetermined neutral territories for the sake of narrative, like Heretic's End - Watchful Gaze and Getsu Fune (ArePredeterminedClusterPair);
        // 2d. Do the same for vanilla factions (SharedOwner);
        // 2e. Also maintain proximity of factions that share a border in vanilla, like the Paranid and Holy Order (ArePredeterminedNeighbors);
        // 3. Spread the Xenon clusters out.


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

        private static float gateMaxDist = 40f;
        private static (int cols, int rows) hexGridFrame;
        private static int maxSlotAdjust = 7, squareBoundary = -1, IntraDomainsDistFactor = 3;
        // when reaching for connections within domains, it will be multiplied by IntraDomainsDistFactor.

        private static Dictionary<string, string> policeFactions = [];

        private readonly Func<Territory, Cluster, bool> IsOutside = (territory, cluster) =>
        {
            return !territory.Clusters.Contains(cluster);
        };

        private Dictionary<int, Domain> domains = [];

        private GateBuilderMST GateBuilder = new GateBuilderMST(new ProceduralSettings
        {
            Seed = Localisation.GetFnvHash(Random.Shared.Next().ToString()),
            MinGatesPerSector = 1,
            MaxGatesPerSector = 1,
            GateMultiChancePerSector = 0
        });

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
        private Dictionary<int, Territory> territories = [];

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            // Gather some basic info
            hexGridFrame = ClusterManager.FrameHexGrid(clusters.ToList());
            _ = LogAsync("Initializing", $"cols = {hexGridFrame.cols} x rows = {hexGridFrame.rows}", true);

            // Group clusters into territories based adjacency and DLCs
            CarveTerritories(clusters);

            // Determine if there are neighboring territories owned by the same faction.
            var groups = FindAnnexed();

            // Determine if there are other close territories owned by the same faction and separated by only a neutral sector.
            FindCloseColonies(ref groups);

            // Consolidate neighbouring territories into one (following DLC criteria) or under merged domains (if owner is shared).
            ConsolidateDomains(groups);

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
                    foreach (var faction in GetDefaultPolice().Where(x => !x.Value.Equals("none", StringComparison.OrdinalIgnoreCase)))
                    {
                        policeFactions[faction.Key] = faction.Value;
                    }
                }
                return policeFactions;
            }
        }

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

        #endregion Header
        
        #region Territories

        private static bool IsMandatoryGateway(Cluster origin, Cluster target)
        {
            var originName = origin.Name;
            var targetName = target.Name;
            bool flag = MandatoryGateways.ContainsKey(originName) &&
                MandatoryGateways[originName] == targetName;
            return flag;
        }

        private static bool ArePredeterminedNeighbors(string owner, string targetOwner)
        {
            return ObligateNeighborFactions.ContainsKey(owner) &&
            ObligateNeighborFactions[owner] == targetOwner;
        }

        private static bool IsVassal(string owner, string targetOwner)
        {
            return VassalFactions.ContainsKey(owner) &&
            VassalFactions[owner] == targetOwner;
        }

        private static bool KeepSequence(List<Territory> set)
        {
            //Spares unmerged close colonies or sets marked to merge sets from spawning in randomized order
            return set.Count > 1 && set.Any(x => x.isBridge/* || x.toMerge*/);
        }

        private static bool SharedOwner(string owner, string targetOwner)
        {
            return !owner.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                !targetOwner.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                owner.Equals(targetOwner);
        }

        private static bool ShouldMergeByDLC(Territory selected, Territory target)
        {
            return !selected.IsVanilla && !target.IsVanilla &&
                selected.dlc.Equals(target.dlc, StringComparison.OrdinalIgnoreCase) &&
                selected.SameOwner && target.SameOwner;
        }

        private static bool SameDLC(Cluster selected, Cluster target)
        {
            return !string.IsNullOrWhiteSpace(selected.Dlc) &&
                !string.IsNullOrWhiteSpace(target.Dlc) &&
                selected.Dlc.Equals(target.Dlc, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldMergeByPolice(Sector origin, Sector destination)
        {
            //works for Terrans and Avarice
            var owner = origin.CurrentOwner.ToLower();
            var targetOwner = destination.CurrentOwner.ToLower();
            return PoliceFactions.ContainsKey(targetOwner) && owner.Equals(PoliceFactions[targetOwner]);
        }

        private void CarveTerritories(IEnumerable<Cluster> clusters)
        {
            bool amIXenon = false;

            Action<Cluster, bool> SortTerritory = (cluster, reset) =>
            {
                if (reset)
                {
                    var owner = cluster.GetOwnerShip();
                    amIXenon = !string.IsNullOrEmpty(owner) && owner.Equals(XenonName, StringComparison.OrdinalIgnoreCase);
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

            Func<Cluster, bool> NoXenon = cluster =>
            {
                //Except from DLCs
                if (!string.IsNullOrEmpty(cluster.Dlc)) return true;
                var owner = cluster.GetOwnerShip();
                bool flag = !string.IsNullOrEmpty(owner) && owner.Equals(XenonName, StringComparison.OrdinalIgnoreCase);
                return amIXenon ? flag : !flag;
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
            FlexFloodProcessor(ordered, SortTerritory, GetNeighbors, x => NoXenon(x), x => AreConnected(x));
        }

        private void ConsolidateDomains(List<HashSet<int>> unsortedGroups)
        {
            // Build groups by merging any overlapping sets into larger ones.
            List<HashSet<int>> groups = MergeOverlappingDomains(unsortedGroups);

            if (FreezeMode)
            {
                // filter out and merge the domains that ask for it.
                groups = MergeAndFilterOutDomains(groups);
            }

            // rebuild the dictionary so each entry is a consolidated domain.
            Dictionary<int, List<int>> consolidated = [];
            int idx = 0;
            foreach (var g in groups)
            {
                idx++;
                Domain domain = new Domain(this)
                {
                    Id = idx,
                    Territories = g.ToList(),
                    IsColonySet = g.Any(i => territories[i].isBridge),
                    IsSequential = KeepSequence(g.Select(id => territories[id]).ToList()),
                    IsXenon = g.All(i => territories[i].IsXenon)
                };
                domains.Add(idx, domain);
            }
            DesignateDomains();
            MarkLandlockedAndGateways();
            MapDomainAffinities();
            SetDomainsDrawOrder();

            // logging
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Consolidated {domains.Count} domain(s):\n{string.Join("\n", domains.Values
                .Select(x => $"#{x.Id}: {x.Owner}{(x.IsSequential ? ", sequencial" : "")}{(x.Affinities.Count > 0 ? $", {x.Affinities.Count} affinities" : "")} ({string.Join(", ", x.Territories.Select(t => $"{t}-{territories[t]}"))})"))}.");
        }

        private void DesignateDomains()
        {
            foreach (var d in domains)
            {
                var set = d.Value.Territories;
                foreach (var id in set)
                {
                    if (!territories.ContainsKey(id)) continue;
                    var territory = territories[id];
                    territory.assignedDomainId = d.Key;
                    territory.peers = set.Where(x => x != territory.id).ToList();
                }
            }
            var i = domains.Count + 1;
            foreach (var t in territories.Values.Where(t => t.assignedDomainId == 0))
            {
                var n = i++;
                t.assignedDomainId = n;
                domains.Add(n, new Domain(this)
                {
                    Id = n,
                    Territories = [t.id],
                    IsXenon = t.IsXenon
                });
            }
        }

        private void MapDomainAffinities()
        {
            foreach (var d in domains.Values)
            {
                if (d.IsNeutral || d.IsDivided) continue;
                if (ObligateNeighborFactions.TryGetValue(d.Owner, out var neighbor))
                {
                    var match = domains.Values.FirstOrDefault(x => x.Owner.Equals(neighbor, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        d.Affinities.AddUnique(match.Id);
                        match.Affinities.AddUnique(d.Id);
                    }
                }
                if (VassalFactions.TryGetValue(d.Owner, out var vassal))
                {
                    var match = domains.Values.FirstOrDefault(x => x.Owner.Equals(vassal, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        d.Affinities.AddUnique(match.Id);
                        match.Affinities.AddUnique(d.Id);
                    }
                }
            }
        }

        private void MarkLandlockedAndGateways()
        {
            foreach (var t in territories.Values.Where(t => !t.unconnected))
            {
                t.UpdateLandlockedStatus();
                if (t.IsVanilla) continue; 
                var gatewayId = t.InferGateway();
                if (gatewayId > 0)
                {
                    var gateway = territories[gatewayId];
                    if (!gateway.IsGateway)
                    {
                        gateway.gatewayTo = t.id;
                    }
                    else
                    {
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"ERROR: Tried to overwrite a gateway! Attempt @ {gateway}, initiated by {t}.");
                    }
                }
            }
        }

        private List<HashSet<int>> FindAnnexed()
        {
            List<HashSet<int>> results = [];
            List<(Territory territory, Cluster cluster, Sector origin, Sector destination)> connections = territories.Values
                .SelectMany(t => t.Connections
                .Select(c => (t, c.cluster, c.origin, c.destination)))
                .ToList();
            HashSet<Territory> annexed = [];
            foreach (var (territory, cluster, origin, destination) in connections)
            {
                var targetId = destination.AssignedTerritoryId;
                if (targetId < 1) continue;
                var target = territories[targetId];
                if (target != null)
                {
                    if (IsMandatoryGateway(cluster, destination.Parent))
                    {
                        territory.gatewayTo = targetId;
                        target.gatedFrom = territory.id;
                    }
                    if (origin.IsNeutral || destination.IsNeutral) continue;
                    var owner = origin.CurrentOwner.ToLower();
                    var targetOwner = destination.CurrentOwner.ToLower();
                    if (owner == null) continue;
                    bool toMerge = ShouldMergeByDLC(territory, target) &&
                        (ShouldMergeByPolice(origin, destination) ||
                        ((target.Landlocked || territory.Landlocked) && SharedOwner(owner, targetOwner)));
                    bool sameOwner = SharedOwner(owner, targetOwner) ||
                        IsVassal(owner, targetOwner);
                    if (toMerge || sameOwner)
                    {
                        territory.annexedIds.AddUnique(targetId);
                        target.annexedIds.AddUnique(territory.id);
                        results.Add([territory.id, targetId]);
                        territory.toMerge |= toMerge;
                        target.toMerge |= toMerge;
                        annexed.Add(target);
                        annexed.Add(territory);
                    }
                }
            }
            foreach (var territory in annexed)
            {
                territory.SetUpConnections();
            }
            return results;
        }

        private void FindCloseColonies(ref List<HashSet<int>> groups)
        {
            Dictionary<int, string> absorbed = [];
            var candidates = territories.Values
                .SelectMany(t => t.bordering)
                .Where(c => c.ExitPoints?.Count > 1 && c.Exits.All(s => s.IsNeutral))
                .ToList();
            foreach (var cluster in candidates)
            {
                var connected = cluster.Destinations
                    .Where(s => !s.IsNeutral && !s.CurrentOwner.Equals(XenonName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(s => s.CurrentOwner)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Select(s => territories[s.AssignedTerritoryId]).ToList())
                    .ToList();
                if (!connected.Any()) continue;
                //merge only if only one set of owned territories is being bridged
                bool toMerge = FreezeMode && connected.Count == 1;
                foreach (var grouped in connected)
                {
                    var bridge = territories[cluster.AssignedTerritoryId];
                    bridge.isBridge = true;
                    bridge.toMerge = toMerge;
                    bridge.closeColonyIds.AddRangeUnique(grouped.Select(x => x.id));
                    foreach (var neighbor in grouped)
                    {
                        if (toMerge)
                        {
                            neighbor.toMerge = true;
                        }
                        else
                        {
                            neighbor.closeColonyIds.AddRangeUnique(grouped.Except([neighbor]).Select(x => x.id));
                            neighbor.closeColonyIds.Add(bridge.id);
                            neighbor.SetUpConnections();
                        }
                    }
                    var bridged = grouped.Select(x => x.id).ToList();
                    cluster.BridgeFor.AddRange(bridged);
                    var extents = bridged.Append(bridge.id).ToHashSet();
                    groups.Add(extents);
                }
            }
        }

        private void InferOrientations()
        {
            foreach (var domain in domains.Values)
            {
                var set = domain.Territories;
                for (int i = 0; i < set.Count; i++)
                {
                    if (territories.ContainsKey(set[i]))
                    {
                        var territory = territories[set[i]];
                        territory.SetUpDirection(i > 0);
                    }
                }
            }
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
            List<string> absorbedReport = absorbed.Select(x => $"#{x}({territories[x]})").ToList();
            string separator = absorbed.Count() == 2 ? " and " : ", ";
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"{string.Join(separator, absorbedReport)} merged into #{territory.id}({territory})");
            foreach (var i in absorbed)
            {
                territories.Remove(i);
            }
            return lead;
        }

        private List<HashSet<int>> MergeOverlappingDomains(List<HashSet<int>> groups)
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

            FlexFloodProcessor(groups, MergeDomains, GetIntersecting);
            return mergedGroups;
        }

        private void SetDomainsDrawOrder()
        {
            foreach (var set in domains.Values.Where(x => x.Territories.Count > 1))
            {
                //order by owner only makes a difference for some cases.
                Func<int, int> finalOrder = set.IsSequential ? (i => i) : (i => Random.Shared.Next());

                //For domains joined by affinity:
                if (set.IsDivided)
                {
                    set.Territories = set.Territories.OrderBy(i => territories[i].Owner).ThenBy(i => territories[i].Landlocked).ThenBy(finalOrder).ToList();
                }
                //For close colonies:
                else if (set.Territories.Any(i => territories[i].isBridge))
                {
                    set.Territories = set.Territories.OrderBy(i => i).ToList();
                }
                //for everyone else:
                else
                {
                    set.Territories = set.Territories.OrderBy(i => territories[i].Landlocked).ThenBy(finalOrder).ToList();
                }
            }
        }

        private void TerritoriesReport()
        {
            var log = new StringBuilder();
            log.AppendLine($"\n\n--- Territories ---\n");
            foreach (var t in territories.Values)
            {
                //string ownerName = t.Owner ?? "Divided";
                //var owner = t.IsNeutral ? "Neutral" : ownerName;
                var owner = t.Owner ?? "Divided";
                log.Append($"#{t.id} - {t}, {owner}:");
                log.Append($"{t.Clusters.Count} clusters, {t.bordering.Count} connecting");
                if (t.Landlocked) log.Append(", landlocked");
                if (t.Gated) log.Append($", behind a gateway: {territories[t.gatedFrom]}");
                if (t.gatewayTo > 0) log.Append($", gateway to {territories[t.gatewayTo]}");
                //log.Append($"facing {t.exitDirection}");
                //log.Append(string.IsNullOrEmpty(t.dlc) ? "" : $", dlc: {t.dlc}");
                //log.Append(PoliceFactions.ContainsKey(owner.ToLower()) ? $", police: {PoliceFactions[owner.ToLower()]}" : "");]
                if (t.annexedIds.Count > 0) log.Append($"; annexed to {string.Join(", ", t.annexedIds.Select(x => $"#{territories[x].id}-{territories[x]}"))}");
                if (t.isBridge) log.Append($"; bridges {string.Join(", ", t.Clusters.First(x => x.BridgeFor.Count > 0).BridgeFor.Select(y => $"#{territories[y].id}-{territories[y]}"))}");
                if (t.closeColonyIds.Count > 0) log.Append($"; colonies {string.Join(", ", t.closeColonyIds.Select(x => $"#{territories[x].id}-{territories[x]}"))}");
                log.AppendLine(".");
            }
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, log.ToString());
        }
        #endregion Territories
        
        #region Shuffler

        internal void Shuffle()
        {
            //logging
            string level = MethodBase.GetCurrentMethod().Name;
            _ = LogAsync(level, $"\n\n--- Shuffling ---", true);

            //No turning back now!
            MainForm.Instance.AllClusters.Clear();

            List<int> cards = territories.Keys.ToList();
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(cards));

            bool inBounds = true, firstRun = true;
            //Dictionary<string, StagedSet> staged = [];
            //Dictionary<string, string> sequencesInParalelBranches = [];
            Dictionary<int, ImmutableList<int>> domainsList = domains
                .Where(x => x.Value.Territories.Count > 1)
                .Select(x => (x.Key, x.Value.Territories.ToImmutableList()))
                .ToDictionary();
            Direction lastXenonSeededBranch = Direction.Undefined;
            HashSet<int> reservedDomains = PreReservedDomains();
            List<Cluster> misplaced = [], orphanedRoads = [], secondaryRoads = [], unconnected = [];
            SortedSet<cPoint> occupied = [];

            Slot groundZero = new Slot() { Position = Point.Empty, Path = "0" };
            Queue<Slot> slots = new([groundZero]), deferred = new();
            Dictionary<string, Slot> map = new() { ["0"] = groundZero };

            bool TryGetTerritory(out Territory territory, out bool isSequence, out bool isResuming, out Point pos, out Direction dir, out Direction branch, out string path, out Slot slot)
            {
                bool flag = false;
                territory = null;
                isSequence = false;
                isResuming = false;
                pos = Point.Empty;
                dir = Direction.Undefined;
                branch = Direction.Undefined;
                path = null;
                int skipTracker = 0;
                slot = null;
                while (slots.Count > 0)
                {
                    slot = slots.Dequeue();
                    path = slot.Path;
                    pos = slot.Position;
                    dir = path.GetDirection();
                    branch = path.GetMainBranch();
                    //territory = PickNextFromStaged(staged, sequencesInParalelBranches, slot.path, ref lastXenonSeededBranch, ref skipTracker, out isSequence, out isResuming);
                    territory = PickNextFromSlot(slot, map, reservedDomains, ref skipTracker, ref lastXenonSeededBranch, out isSequence, out isResuming);
                    flag = territory != null;
                    if (flag)
                    {
                        if (skipTracker > 0)
                        {
                            _ = LogAsync(level, $"We've run out of domains before all staged were distributed. {skipTracker} slots were skipped while looking for the next viable one.", true);
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
                            _ = LogAsync(level, $"Bounds reached, spilling over!", true);
                        }
                        else
                        {
                            //end of the line
                        }
                    }
                }
                if (!flag)
                {
                    var deployed = map.Values.Where(x => x.Territory != null).Select(x => x.Territory.id);
                    var missing = territories.Keys.Except(deployed).Select(x => territories[x]);
                    var deployedGroups = domains.Values.GroupBy(x => x.Deployed).ToList();
                    var domsDeployed = deployedGroups.FirstOrDefault(g => g.Key)?.ToList() ?? [];
                    var deployedReport = domsDeployed.Count > 0 ? 
                        domsDeployed.Select(d => $"#{d.Id} - [{string.Join(", ",d.Territories.Select(t => territories[t]))}]").ToList() : 
                        ["No domains were deployed"];
                    var domsUndeployed = deployedGroups.FirstOrDefault(g => !g.Key)?.ToList() ?? [];
                    var undeployedReport = domsUndeployed.Count > 0 ?
                        domsUndeployed.Select(d => $"#{d.Id} - [{string.Join(", ", d.Territories.Select(t => territories[t]))}]").ToList() :
                        ["All domains were deployed"];
                    _ = LogAsync(level, $"ERROR: Fatal error drawing a territory! path = {path}, pos = {pos}, dir = {dir}, branch = {branch}, reserved = {reservedDomains.Count}.\nMissing:\n{string.Join(", ",missing)}\n\nDomanis probe:\n\nReserved = {string.Join(", ", reservedDomains)}.\n\nDeployed:\n{string.Join("\n", deployedReport)}\n\nSkipped:\n{string.Join("\n", undeployedReport)}");
                }
                return flag;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                bool valid = TryGetTerritory(out var territory, out var isSequence, out var isResuming, out var position, out var direction, out var branch, out var path, out var slot);

                //Reporting territory selection
                _ = LogAsync(level, $"Step {i}, branch {branch}, slot @ {position.ToTuple()}/{direction}/{path}", true);
                if (valid)
                {
                    _ = LogAsync(level, $"Assigning #{territory.id} - {territory}, size={territory.size.ToTuple()},{territory.Clusters.Count} clusters");
                }
                else
                {
                    _ = LogAsync(level, $"ERROR: we've run out of territories to assign to slots!");
                    break;
                }

                //Rotate it as needed.
                if (AllowRotating && direction != Direction.Undefined && territory.exitDirection != Direction.Undefined)
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
                _ = LogAsync(level, report);

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
                _ = LogAsync(level,
                    $"{covered.Count} tiles were covered, {cMinX} to {cMaxX} horizontal, {cMinY} to {cMaxY} vertical, totalling {occupied.Count} now.");

                //Update the board.
                UpdateClusterMap(territory.Clusters, misplaced);

                //Redo connections
                var reachAcross = !firstRun /*&& helixGeneration % periodicConnectionModule == 0*/ && direction == branch;
                Direction reachDir = reachAcross ? (Direction)(((int)direction + 3) % 4) : Direction.Undefined;
                //Reconnect(territory, reachDir, firstRun, isSequence, isResuming, orphanedRoads, secondaryRoads);
                //Reconnect(slot, reachDir, firstRun, isSequence, isResuming, map);

                //Prepare the next slots.
                var nextSlots = NextSlotsHelix(territory, occupied, slot);

                //bool isSequential = sequentialDomains.Contains(territory.AssignedDomainId);
                foreach (var node in nextSlots)
                {
                    map[node.Path] = node;
                    if (InBounds(node.Position) || isSequence || slot.Unbound) slots.Enqueue(node);
                    else deferred.Enqueue(node);
                }
                firstRun = false;
            }
            HandleMisplaced(misplaced);
            orphanedRoads.RemoveAll(x => x.PossibleExits.Count == 0);
            ReconnectNetwork(map);
            ValidateGateNetwork();

            //var delayed = DelayedBridges(orphanedRoads, secondaryRoads);
            //_ = LogAsync(level, (delayed > 0 ?
            //    $"{delayed} more gate connections were made for territories that ended up isolated." : "No isolated territories after shuffling.")
            //    + " The network will now be stitched together...", true);
            //if (FinalizeNetwork(domainsList))
            //{
            //    _ = LogAsync(level, $"Gate Network reestabilished. Galaxy shuffle complete.", true);
            //}
            //else
            //{
            //    _ = LogAsync(level, $"ERROR: Shuffle procedure ended, but the gate network is still fragmented!", true);
            //}
        }

        //Temporary Diagnostics
        private static void ValidateGateNetwork()
        {
            List<Sector> sectors = MainForm.Instance.AllClusters.Values
                .SelectMany(cluster => cluster.Sectors)
                .ToList();

            Dictionary<string, List<Sector>> sectorsByName = sectors
                .GroupBy(sector => sector.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (Sector sourceSector in sectors)
            {
                foreach (Gate sourceGate in sourceSector.Zones.SelectMany(zone => zone.Gates))
                {
                    if (!sectorsByName.TryGetValue(
                            sourceGate.DestinationSectorName,
                            out List<Sector> destinationSectors))
                    {
                        Debug.WriteLine(
                            $"MISSING DESTINATION SECTOR: " +
                            $"{sourceSector.Name} -> {sourceGate.DestinationSectorName}; " +
                            $"source path: {sourceGate.DestinationPath}");

                        continue;
                    }

                    List<Gate> matchingGates = destinationSectors
                        .SelectMany(sector => sector.Zones.SelectMany(zone => zone.Gates))
                        .Where(gate => string.Equals(
                            gate.SourcePath,
                            sourceGate.DestinationPath,
                            StringComparison.Ordinal))
                        .ToList();

                    if (matchingGates.Count == 0)
                    {
                        Debug.WriteLine(
                            $"MISSING TARGET GATE: " +
                            $"{sourceSector.Name} -> {sourceGate.DestinationSectorName}; " +
                            $"source gate ID: {sourceGate.Id}; " +
                            $"destination path: {sourceGate.DestinationPath}; " +
                            $"target paths: {string.Join(", ", destinationSectors.SelectMany(sector => sector.Zones.SelectMany(zone => zone.Gates)).Select(gate => gate.SourcePath))}");
                    }
                }
            }
        }

        private HashSet<int> PreReservedDomains()
        {
            var filtered = domains
                .Where(d => d.Value.GatedFromOutside || d.Value.IsColonySet)
                .Select(d => d.Key);
            return filtered.ToHashSet();
        }

        private void HandleMisplaced(List<Cluster> misplaced)
        {
            if (misplaced.Count == 0) return;
            int y = 1;
            foreach (var c in misplaced)
            {
                StringBuilder log = new StringBuilder();
                log.Append($"{c.Name}, from territory #{c.AssignedTerritoryId}-{territories[c.AssignedTerritoryId]} couldn't be placed @ {c.Position.ToTuple()}... ");
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
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, log.ToString(), true);
            }
        }

        private List<Slot> NextSlotsHelix(Territory territory, SortedSet<cPoint> occupied, Slot parent)
        {
            var parentAddress = parent.Path;
            var branch = parentAddress.GetMainBranch();
            var lastDir = parentAddress.GetDirection();
            bool firstRun = branch == Direction.Undefined;
            bool quadrant = branch == lastDir;
            bool cycle = quadrant && branch == Direction.Right;
            var ax = territory.Anchor.X;
            var ay = territory.Anchor.Y;
            var width = territory.size.X;
            var height = territory.HeightToFit;
            var slots = new List<Slot>();
            var max = occupied.Max();
            var min = occupied.Min();
            if (cycle) helixGeneration++;

            //logging
            List<string> log = new List<string>();
            string level = MethodBase.GetCurrentMethod().Name;

            //finishing routine
            void Select(Point pos, Direction dir)
            {
                bool front = pos.X > max.X || pos.X < min.X || pos.Y > max.Y || pos.Y < min.Y;
                if (front || !occupied.Contains(pos))
                {
                    var add = parentAddress.DownstreamAddress(dir);
                    Slot newSlot = new Slot() { Position = pos, Direction = dir, Path = add, Parent = parent };
                    slots.Add(newSlot);
                    parent.Children.Add(newSlot);
                    log.Add($"{pos.ToTuple()}/{dir}/{add}");
                }
                else
                {
                    _ = LogAsync(level, $"{pos.ToTuple()}{dir} was already occupied, slot skipped! Branch: {branch})");
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
                _ = LogAsync(level, $"No Slots found for #{territory.id}! Branch: {branch})");
            }
            _ = LogAsync(level, $"Slots around #{territory.id}: {string.Join(", ", log)} (branch: {branch}, gen: {helixGeneration}).");

            return slots.ToList();
        }

        private Territory PickNextFromSlot(Slot node, Dictionary<string, Slot> map, HashSet<int> reservedDomains, ref int skipTracker, ref Direction lastXenonSeededBranch, out bool isSequence, out bool isResuming)
        {
            Territory selected = null;
            isResuming = false;
            isSequence = false;
            var report = new StringBuilder();
            List<int> setList = [];
            Slot overthere = null;
            var path = node.Path;
            bool branching = path.GetMainBranch() == path.GetDirection();

            // 1. Check what the expectations are on the parent node.
            var parent = node.Parent;
            if (parent != null)
            {
                // First, check if it's even applicable.
                bool onStage = parent.Staged.Count > 0;
                bool hasGateway = parent.Territory.IsGateway && !parent.Territory.actedOnGateway;
                if (onStage || hasGateway)
                {
                    bool isFirstborn = node == parent.Children.First();
                    bool usedGateway = false;
                    // 1a. If both staged and gateway are present but current slot isn't the first child, serve the gateway.
                    if (onStage && hasGateway && !isFirstborn)
                    {
                        selected = territories[parent.Territory.gatewayTo];
                        parent.Territory.actedOnGateway = true;
                        usedGateway = true;
                    }
                    // 1b. If no gateway or it's the first child, serve the queue. 
                    else if (onStage)
                    {
                        selected = FromStagedGatewayAware(parent);
                    }
                    // 1c. Otherwise, there's no queue left, but there's a gateway to serve
                    else
                    {
                        selected = territories[parent.Territory.gatewayTo];
                        parent.Territory.actedOnGateway = true;
                        usedGateway = true;
                    }

                    if (selected != null)
                    {
                        if (usedGateway)
                        {
                            report.Append($"Selected a set from a mandatory gateway ({parent.Territory} -> {selected}) on the parent slot");
                            if (selected.assignedDomainId == parent.AssignedDomainId)
                            {
                                goto sameDomain;
                            }
                            goto NewSetLoaded;
                        }
                        else 
                        {
                            report.Append($"Resuming queue from parent");
                        }
                        sameDomain:
                        isResuming = true;
                        isSequence = parent.IsSequential;
                        node.Staged = parent.Staged;
                        setList = parent.Staged.ToList();
                        report.Append($", within the same domain");
                        goto Finalize;
                    }
                }
            }

            // 2. Check what's on the parallel branch
            overthere = SlotOnParallelBranch(map, path);
            Domain referredDomain = null;

            // 2a. If there's a gateway, pick the the expected target.
            if (overthere != null && overthere.Territory != null && overthere.Territory.IsGateway && !overthere.Territory.actedOnGateway)
            {
                selected = territories[overthere.Territory.gatewayTo];
                overthere.Territory.actedOnGateway = true;
                report.Append($"Selected a set from a mandatory gateway ({overthere.Territory} -> {selected}) on a parallel branch");
                if (selected.assignedDomainId == overthere.AssignedDomainId)
                {
                    node.Staged = overthere.Staged;
                    setList = overthere.Staged.ToList();
                    report.Append($", within the same domain");
                    goto Finalize;
                }
            }

            // 2b. If there's a non-sequential, ongoing set loaded there, it takes over
            else if (overthere != null && !overthere.IsSequential && overthere.Staged.Count > 0)
            {
                isResuming = true;
                selected = FromStagedGatewayAware(overthere);
                node.Staged = overthere.Staged;
                setList = overthere.Staged.ToList();
                report.Append($"Resuming queue from parallel branch");
                goto Finalize;
            }

            // 3. Next priority is fulfilling any affinities, either from the parallel branch or the parent node.
            else
            {
                bool fromParent = parent?.Territory != null &&
                    domains.TryGetValue(parent.Territory.assignedDomainId, out referredDomain) &&
                    referredDomain.Affinities.Count > 0;
                bool fromParallel = !fromParent &&
                    overthere != null &&
                    domains.TryGetValue(overthere.AssignedDomainId, out referredDomain)
                    && referredDomain.Affinities.Count > 0;
                if (fromParent || fromParallel)
                {
                    int foundId = referredDomain.Affinities.Random();
                    var foundDom = domains[foundId];
                    if (foundDom != null)
                    {
                        referredDomain.Affinities.Remove(foundId);
                        foundDom.Affinities.Remove(referredDomain.Id);
                        selected = FirstViableFrom(foundDom);
                        report.Append($"Selected a set based on affinity with the {(fromParent ? "parent" : "neighboring branch")}");
                    }
                }
            }

            // 4. Otherwise, pick a random domain, minding restrictions for the Xenon.
            if (selected == null)
            {
                // Before proceeding, check if there acually are any fresh sets to apply. Break the reserve if needed. If those are gone too, that's the end.
                //var unreservedSets = domains.Where(x => !reservedDomains.Contains(x.Key) && !x.Value.Deployed).ToList();
                //var availableSets = unreservedSets.Count > 0 ? unreservedSets : domains.Where(x => !x.Value.Deployed).ToList();
                var availableSets = domains.Where(x => !reservedDomains.Contains(x.Key) && !x.Value.Deployed).ToList();
                if (availableSets.Any())
                {
                    var mainBranch = path.GetMainBranch();
                    // Barring the Xenon from being at the centre, for gameplay reasons.
                    bool xenonAllowed = path.Length > 2 && mainBranch != lastXenonSeededBranch;
                    var regular = availableSets.Where(x => !x.Value.IsXenon);
                    var entry = (xenonAllowed || regular.Count() == 0) ? availableSets.Random() : regular.Random();
                    if (entry.Value.IsXenon) lastXenonSeededBranch = mainBranch;
                    selected = FirstViableFrom(entry.Value);
                    report.Append($"Selected a new random set");
                }
                else
                {
                    skipTracker++;
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name,
                        $"Can't fetch a new set because all of them have been deployed already (path {path}).");
                    return null;
                }
            }

            // 5. Update the stage for the newly selected set (not needed if we're only resuming a queue)
            NewSetLoaded:
            var selectedDomain = domains[selected.assignedDomainId];
            setList = selectedDomain.Territories.ToList();
            if (setList == null)
            {
                throw new Exception($"Selected domain has a null list! Looking for path {path}, {string.Join("; ", domains.Select(x => $"#{x.Key}=[{string.Join(',', x.Value)}]"))}");
            }
            setList.Remove(selected.id);
            if (setList.Count > 0)
            {
                node.Staged = new Queue<int>(setList.Where(x => !territories[x].Gated).ToList());
                isSequence = selectedDomain.IsSequential;
            }
            selectedDomain.Deployed = true;
            reservedDomains.Remove(selectedDomain.Id);
            foreach (var idx in selectedDomain.Affinities)
            {
                reservedDomains.Add(idx);
            }

            Finalize:
            node.IsSequential = isSequence;
            node.Territory = selected;

            //logging
            if (setList.Count > 0) report.Append($", picked {selected} (remaining: [{string.Join(", ", setList)}])");
            else report.Append($": picked {selected}");
            if (overthere != null)
                report.Append($"\nPROBE: slot on parallel branch: path={overthere.Path} sequential={overthere.IsSequential} staged=[{string.Join(", ", overthere.Staged)}], territory is {overthere.Territory}, reserved: {reservedDomains.Count}");
            if (parent != null)
                report.Append($"\nPROBE: slot on parent branch: path={parent.Path} sequential={parent.IsSequential} staged=[{string.Join(", ", parent.Staged)}], territory is {parent.Territory}, reserved: {reservedDomains.Count}");
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, report.ToString() + ".", true);

            return selected;

            //This will retrieve the next queued territory that doesn't risk blocking that branch.
            //(A gateway that's part of a domain but isn't the last item on the list, if called from a non-branching spot, might be boxed in, preventing it from spawning their expected predefined connection. Therefore, it must be moved to the end of the queue!).
            Territory FromStagedGatewayAware(Slot reference)
            {
                Territory selected = territories[reference.Staged.Dequeue()];
                if (selected.IsGateway && reference.Staged.Count > 0 && !branching)
                {
                    reference.Staged.Enqueue(selected.id);
                    selected = territories[reference.Staged.Dequeue()];
                }
                return selected;
            }

            Territory FirstViableFrom(Domain domain)
            {
                Territory selected = null;
                foreach (var id in domain.Territories)
                {
                    if (territories.TryGetValue(id, out selected) && (branching || !selected.IsGateway)) return selected;
                }
                return selected;
            }
        }

        private Slot SlotOnParallelBranch(Dictionary<string, Slot> map, string path)
        {
            if (path.Length > 2)
            {
                var parallel = path.GetParalelOlderAddress();
                if (!string.IsNullOrEmpty(parallel))
                {
                    if (map.ContainsKey(parallel))
                    {
                        return map[parallel];
                    }
                }
            }
            return null;
        }

        private Slot SlotOnParallelYoungerBranch(Dictionary<string, Slot> map, string path)
        {
            if (path.Length > 2)
            {
                var parallel = path.GetParalelNewerAddress();
                if (!string.IsNullOrEmpty(parallel))
                {
                    if (map.ContainsKey(parallel))
                    {
                        return map[parallel];
                    }
                }
            }
            return null;
        }

        private void UpdateClusterMap(List<Cluster> clusters, List<Cluster> misplaced)
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

        #endregion Shuffler
        
        #region Placement

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
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Error placing #{territory.id}: there wasn't enough space for it! Attempting a forced push...");
                    if (!TryToPushAround(offset, branch, dir, occupied, width, height, 20, ref adjust))
                    {
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"...still couldn't place it over the next 20 tiles! Giving up.");
                        return offset;
                    }
                    else
                    {
                        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"...moved it by {adjust.ToTuple()}.");
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
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, string.Join(" -> ", report) + ".");

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
                var hitsX = Spread(width, height, coord => new Point(hexPos.X + pushX + coord.a, hexPos.Y - coord.b), p => occupied.Contains(p)).ToList();
                viableX = !hitsX.Any();
            }
            if (viableX && (preferredDir == Direction.Right || preferredDir == Direction.Left)) goto pushX;

            //Probe Y and push it if preferred
            if (Math.Abs(pushY) > 0)
            {
                var hitsY = Spread(width, height, coord => new Point(hexPos.X + coord.a, hexPos.Y + pushY - coord.b), p => occupied.Contains(p)).ToList();
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

        private bool IsValidPlacement(Point position, bool singleTile, SortedSet<cPoint> occupied, int width, int height)
        {
            return singleTile ? !occupied.Contains(position) : !SimpleCollision(occupied, position, width, height);
        }

        private List<Point> ScanForCollisions(SortedSet<cPoint> occupied, Point position, int width, int height)
        {
            return Spread(width, height, coord => new Point(position.X + coord.a, position.Y - coord.b), p => occupied.Contains(p)).ToList();
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
        #endregion Placement
        
        #region Reconnections

        private bool CanConnectFromDirection((Cluster from, Cluster to) pair, Direction direction)
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

        private int DelayedBridges(List<Cluster> outgoing, List<Cluster> desired, float limit = -1f)
        {
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, string.Join(", ", outgoing));

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

        private bool FinalizeNetwork(Dictionary<int, ImmutableList<int>> domainsList)
        {
            string level = MethodBase.GetCurrentMethod().Name;
            bool result = false;
            var allTerritories = territories.Values.ToList();
            foreach (var territory in territories.Values)
            {
                territory.SetUpConnections();
            }
            if (StitchNetwork(FindNetworks(allTerritories)))
            {
                _ = LogAsync(level, "First pass was succesful: the network is whole!");
                result = true;
            }
            else
            {
                _ = LogAsync(level, "WARNING: First pass has failed: we couldn't put the network back together! It all depends on the individual domains now...");
            }
            int count = 0;
            foreach (var set in domainsList)
            {
                count++;
                var lists = FindNetworks(set.Value.Select(x => territories[x]).ToList());
                if (lists.Count > 1) _ = StitchNetwork(lists, gateMaxDist * IntraDomainsDistFactor);
            }
            return result || FindNetworks(allTerritories).Count == 1;
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

            FlexFloodProcessor(territories, SortGroup, GetConnected, x => groups.Last().Intersect(x.neighbors).Any());
            var count = groups.Count;

            //logging
            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Found {(count > 1 ? $"{count} network patches" : "a single network")} from a set of {territories.Count} territories.", true);

            return groups;
        }

        //private bool RemoveGatesSelectively(Territory territory, Territory exception, HashSet<Cluster> disconnected)
        //{
        //    bool result = false;
        //    foreach (var exit in DisposableConnections(territory, ex))
        //    {
        //        if (exception == null || exit.destination.AssignedTerritoryId != exception.id)
        //        {
        //            disconnected.Add(exit.cluster);
        //            var gate = exit.gate;
        //            var zone = gate.ParentZone;
        //            zone.Gates.Remove(gate);
        //        }
        //        else result = true;
        //    }
        //    return result;
        //}

        private void RemoveGatesSelectively(Territory territory, Territory parent, HashSet<Cluster> disconnected)
        {
            foreach (var exit in DisposableConnections(territory))
            {
                disconnected.Add(exit.cluster);
                var gate = exit.gate;
                var zone = gate.ParentZone;
                zone.Gates.Remove(gate);
            }
        }

        private IEnumerable<(Cluster cluster, Sector origin, Gate gate, Sector destination)> DisposableConnections(Territory origin)
        {
            if (origin.unconnected || origin.Landlocked) yield break; //spares all gates from isolated territories
            foreach (var exit in origin.Connections)
            {
                var destinationTerritory = territories[exit.destination.AssignedTerritoryId];
                //spares gates connecting colonies and toMerge (same DLC / Police), gates to landlocked territories that were previously skipped and protected gateway pairs
                if ((origin.IsDestinationDomestic(exit.destination) &&
                    (domains[origin.assignedDomainId].IsSequential ||
                    destinationTerritory.Landlocked)) ||
                    origin.gatewayTo == destinationTerritory.id ||
                    (origin.Gated && destinationTerritory.gatewayTo == origin.id)) continue;
                yield return exit;
            }
        }

        private void Reconnect(Slot slot, Direction alsoReachFor, bool firstRun, bool isSequence, bool isResuming, Dictionary<string, Slot> map)
        {
            HashSet<Cluster> disconnected = [];
            Territory origin = slot.Territory;
            var parent = slot.Parent?.Territory;
            var overthere = SlotOnParallelBranch(map, slot.Path)?.Territory;
            bool goesToParent = false;
            RemoveGatesSelectively(origin, parent, disconnected);
            if (!isSequence || !isResuming)
            {
                bool connected = false, reachedAcross = false;
                (Cluster from, Cluster to) bridge = new();
                if (!firstRun && !goesToParent)
                {
                    bridge = FindBridge(origin.Clusters.ToHashSet(), parent.Clusters, out connected, gateMaxDist);
                }
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reconnect {origin}: {(connected ? $"SUCESS! Connected {bridge.from} to {bridge.to}" : $"FAILED!")}.");
            }
            else
            {
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Avoided reconnecting {origin} to avoid redundant gates on a sequence. (isSequence={isSequence}, isResuming={isResuming})");
            }
        }

        //private void Reconnect(Territory territory, Direction alsoReachFor, bool firstRun, bool isSequence, bool isResuming, List<Cluster> orphanedRoads, List<Cluster> secondaryRoads)
        //{
        //    HashSet<Cluster> outgoing = [];

        //    bool FindBridgeManagePools(HashSet<Cluster> originCandidates, List<Cluster> destinationsPool, bool removeDestination, out (Cluster from, Cluster to) bridge, Predicate<(Cluster, Cluster)> filter = null)
        //    {
        //        bool result = false;
        //        bridge = FindBridge(originCandidates, destinationsPool, out result, gateMaxDist, filter);
        //        if (result && bridge.from != null && bridge.to != null)
        //        {
        //            if (removeDestination)
        //            {
        //                destinationsPool.Remove(bridge.to);
        //                secondaryRoads.Add(bridge.to);
        //            }
        //            if (outgoing.Contains(bridge.from))
        //            {
        //                outgoing.Remove(bridge.from);
        //                secondaryRoads.Add(bridge.from);
        //            }
        //        }
        //        return result;
        //    }

        //    if (territory.ExitGates != null && !territory.isBridge)
        //    {
        //        RemoveGatesSelectively(territory, outgoing);
        //        if (!isSequence || !isResuming)
        //        {
        //            bool connected = false, reachedAcross = false;
        //            int territoryConnected = -1;
        //            if (!firstRun)
        //            {
        //                // First, try known & close paths
        //                connected = FindBridgeManagePools(outgoing, orphanedRoads, true, out var bridge);
        //                if (!connected)
        //                {
        //                    // Then, attempt undesirable but close paths
        //                    connected = FindBridgeManagePools(outgoing, secondaryRoads, false, out bridge);
        //                }
        //                if (connected) territoryConnected = bridge.to.AssignedTerritoryId;
        //                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reconnect {territory}: {(connected ? $"SUCESS! Connected {bridge.from} to {bridge.to}" : $"FAILED! Outgoing clusters: {outgoing.Count()}")}.");
        //            }
        //            if (outgoing.Count > 0 && alsoReachFor != Direction.Undefined)
        //            {
        //                var outgoingSub = outgoing.Where(x => x.Direction != (int)alsoReachFor.OppositeDir()).ToHashSet();
        //                if (outgoingSub.Count > 0)
        //                {
        //                    reachedAcross = FindBridgeManagePools(outgoingSub, orphanedRoads, true, out var bridgeAcross, pair => CanConnectFromDirection(pair, alsoReachFor));
        //                    if (!reachedAcross)
        //                    {
        //                        var secondarySub = connected ? secondaryRoads.Where(x => x.AssignedTerritoryId != territoryConnected).ToList() : secondaryRoads;
        //                        if (secondarySub.Count > 0)
        //                        {
        //                            reachedAcross = FindBridgeManagePools(outgoingSub, secondarySub, false, out bridgeAcross, pair => CanConnectFromDirection(pair, alsoReachFor));
        //                        }
        //                    }
        //                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reach {alsoReachFor} from {territory}: {(reachedAcross ? $"SUCESS! Connected {bridgeAcross.from} to {bridgeAcross.to}" : $"FAILED! Outgoing clusters: {outgoingSub.Count()}")}.");
        //                }
        //            }
        //            if (connected || reachedAcross) secondaryRoads.AddRange(outgoing);
        //            else orphanedRoads.AddRange(outgoing);
        //        }
        //        else
        //        {
        //            _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Avoided reconnecting {territory} to avoid redundant gates on a sequence. (isSequence={isSequence}, isResuming={isResuming})");
        //        }
        //    }
        //    if (territory.absorbedExits.Count() > 0) secondaryRoads.AddRange(territory.absorbedExits);
        //    territory.SetUpConnections();
        //}

        private bool StitchNetwork(List<List<int>> patches, float limit = -1f)
        {
            var ordered = patches.OrderBy(x => x.Count());
            Dictionary<int, HashSet<Cluster>> remaining = [];
            int i = 0;
            foreach (var group in ordered)
            {
                HashSet<Cluster> outgoing = [];
                foreach (var idx in group)
                {
                    var relevant = DisposableConnections(territories[idx])
                        .Select(x => x.cluster)
                        .Distinct()
                        .Where(c => c.PossibleExits.Count > 0);
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
            bool result = true;
            while (remaining.Count > 1) //Last one is the bigger, it's all done once we get there.
            {
                var entry = remaining.First();
                var outHash = entry.Value;
                var destList = remaining.Where(x => x.Key != entry.Key).SelectMany(x => x.Value).ToList();
                var bridge = FindBridge(outHash, destList, out bool bridged, limit);
                remaining.Remove(entry.Key);
                if (bridged)
                {
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Stitched {bridge.from} with {bridge.to}.");
                    territories[bridge.from.AssignedTerritoryId].SetUpConnections();
                    territories[bridge.to.AssignedTerritoryId].SetUpConnections();
                }
                else
                {
                    result = false;
                    _ = LogAsync(MethodBase.GetCurrentMethod().Name, limit > 0 ? $"Failed stitching a patch of clusters. Likely because possible connection points were too far apart. Distance was limited to {limit}. Clusters involved: {string.Join(", ", outHash)}" : "ERROR: Failed stitching!");
                }
            }
            return result;
        }

        private void _ReconnectNetwork(Dictionary<string, Slot> map)
        {
            var actualMap = map.Where(x => x.Value.Territory != null);
            foreach (var slot in actualMap)
            {
                var current = slot.Value;
                foreach (var exit in DisposableConnections(current.Territory))
                {
                    exit.gate.ParentZone.Gates.Remove(exit.gate);
                }
            }
        }



        private void ReconnectNetwork(Dictionary<string, Slot> map)
        {
            List<string> report = [];
            bool firstRun = true;
            var actualMap = map.Where(x => x.Value.Territory != null);
            List<(Gate gate, Sector destination)> gatesToDelete = [];
            HashSet<Gate> preservedGates = [];
            HashSet<string> deletionPaths = new(StringComparer.Ordinal);
            HashSet<string> preservedPaths = new(StringComparer.Ordinal);
            Dictionary<string, string> preservationReasons = new(StringComparer.Ordinal);
            int removedCount = 0;

            foreach (var slot in actualMap)
            {
                var current = slot.Value;
                var parent = current.Parent?.Territory;
                var path = slot.Value.Path;
                var olderSibling = SlotOnParallelBranch(map, path)?.Territory;
                var youngerSibling = SlotOnParallelYoungerBranch(map, path)?.Territory;
                List<int> family = [];
                if (parent != null) family.Add(parent.id);
                if (olderSibling != null) family.Add(olderSibling.id);
                if (youngerSibling != null) family.Add(youngerSibling.id);
                if (current.Children.Count > 0)
                {
                    foreach (var child in current.Children)
                    {
                        if (child.Territory != null) family.Add(child.Territory.id);
                    }
                }

                HashSet<Cluster> disconnected = [];
                List<(Cluster, Sector)> mantained = [];
                List<(Cluster, Sector)> removed = [];
                List<string> spared = [];

                foreach (var exit in DisposableConnections(current.Territory))
                {
                    if (family.Contains(exit.destination.AssignedTerritoryId))
                    {
                        _ = preservedGates.Add(exit.gate);
                        _ = preservedPaths.Add(exit.gate.SourcePath);

                        preservationReasons[exit.gate.SourcePath] =
                            $"territory {current.Territory.id} ({current.Territory}) " +
                            $"preserved destination territory " +
                            $"{exit.destination.AssignedTerritoryId} ({exit.destination}) " +
                            $"because family=[{string.Join(", ", family)}]";
                    }
                    else
                    {
                        gatesToDelete.Add((exit.gate, exit.destination));
                        _ = deletionPaths.Add(exit.gate.SourcePath);
                    }
                }
            }

            List<string> invalidConnections = [];

            foreach ((Gate gate, Sector destination) in gatesToDelete)
            {
                Gate targetGate = destination.Zones
                    .SelectMany(zone => zone.Gates)
                    .FirstOrDefault(x => x.SourcePath == gate.DestinationPath);

                if (targetGate == null)
                {
                    invalidConnections.Add($"{gate.ParentSectorName} -> {gate.DestinationSectorName}: destination gate is missing.");
                    continue;
                }

                //test

                if (preservedPaths.Contains(targetGate.SourcePath))
                {
                    invalidConnections.Add(
                        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: destination gate was explicitly preserved. {preservationReasons[targetGate.SourcePath]}");
                }
                else if (!deletionPaths.Contains(targetGate.SourcePath))
                {
                    Territory targetTerritory = territories[destination.AssignedTerritoryId];

                    bool targetTerritoryWasProcessed = actualMap.Any(
                        slot => slot.Value.Territory == targetTerritory);

                    bool targetTerritoryReferenceIsInMap = actualMap.Any(
                        slot => slot.Value.Territory == targetTerritory);

                    bool targetTerritoryIdIsInMap = actualMap.Any(
                        slot => slot.Value.Territory.id == targetTerritory.id);

                    var targetConnection = targetTerritory.Connections
                        .FirstOrDefault(connection =>
                            connection.gate.SourcePath == targetGate.SourcePath);

                    bool targetIsInConnections = targetConnection.gate != null;

                    string diagnostic =
                        $"target path was not classified. " +
                        $"target={targetGate.ParentSectorName}" +
                        $"targetPath={targetGate.SourcePath}";// " +
                        //$"territory={targetTerritory.id} ({targetTerritory}); " +
                        //$"territoryProcessed={targetTerritoryWasProcessed}; " +
                        //$"territoryReferenceInMap={targetTerritoryReferenceIsInMap}; " +
                        //$"territoryIdInMap={targetTerritoryIdIsInMap}; " +
                        //$"targetTerritoryId={targetTerritory.id}" +
                        //$"inConnections={targetIsInConnections}; " +
                        //$"unconnected={targetTerritory.unconnected}; " +
                        //$"landlocked={targetTerritory.Landlocked}; " +
                        //$"connectionCount={targetTerritory.Connections.Count}";

                    invalidConnections.Add(
                        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: " +
                        diagnostic);
                }

                //if (!disposableGates.Contains(targetGate))
                //{
                //    invalidConnections.Add(
                //        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: " +
                //        $"target was not returned by DisposableConnections. " +
                //        $"Target source path: {targetGate.SourcePath}");
                //}
                //else if (!gatesToDelete.Any(x => x.gate == targetGate))
                //{
                //    invalidConnections.Add(
                //        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: " +
                //        "target was disposable but was not selected for deletion.");
                //}

                //if (preservedGates.Contains(targetGate))
                //{
                //    invalidConnections.Add(
                //        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: source gate is marked for deletion but the destination gate was explicitly preserved.");
                //}
                //else if (!gatesToDelete.Any(x => x.gate == targetGate))
                //{
                //    invalidConnections.Add(
                //        $"{gate.ParentSectorName} -> {gate.DestinationSectorName}: source gate is marked for deletion but the destination gate was not selected for deletion.");
                //}
            }
            if (invalidConnections.Count > 0)
            {
                _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Invalid gate decisions:\n{string.Join("\n\n", invalidConnections)}",true);
                return;
            }

            foreach ((Gate gate, _) in gatesToDelete)
            {
                _ = gate.ParentZone.Gates.Remove(gate);
                removedCount++;

            }




            //report.Add($"#{current.Territory.id} - {current.Territory}: {removedCount} deleted,{(spared.Count > 0 ? $" spared: {string.Join(", ", spared)}" : " none remains")}.");
            //}


            //_ = LogAsync(MethodBase.GetCurrentMethod().Name, $"External connections processed:\n{string.Join("\n",report.Order())}",true);

        }

        //private void Reconnect(Slot slot, Direction alsoReachFor, bool firstRun, bool isSequence, bool isResuming, Dictionary<string, Slot> map)
        //{
        //    HashSet<Cluster> disconnected = [];
        //    Territory origin = slot.Territory;
        //    var parent = slot.Parent?.Territory;
        //    var overthere = SlotOnParallelBranch(map, slot.Path)?.Territory;
        //    bool goesToParent = false;
        //    RemoveGatesSelectively(origin, parent, disconnected);
        //    if (!isSequence || !isResuming)
        //    {
        //        bool connected = false, reachedAcross = false;
        //        (Cluster from, Cluster to) bridge = new();
        //        if (!firstRun && !goesToParent)
        //        {
        //            bridge = FindBridge(origin.Clusters.ToHashSet(), parent.Clusters, out connected, gateMaxDist);
        //        }
        //        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Attempt to reconnect {origin}: {(connected ? $"SUCESS! Connected {bridge.from} to {bridge.to}" : $"FAILED!")}.");
        //    }
        //    else
        //    {
        //        _ = LogAsync(MethodBase.GetCurrentMethod().Name, $"Avoided reconnecting {origin} to avoid redundant gates on a sequence. (isSequence={isSequence}, isResuming={isResuming})");
        //    }
        //}

        #endregion Reconnections
        
        #region Nest
        internal class Domain(Shuffler host)
        {
            public int Id { get; set; }
            internal List<int> Territories { get; set; } = [];
            internal List<int> Affinities { get; set; } = [];
            internal bool IsSequential { get; set; }
            internal bool IsXenon { get; set; }
            internal bool Deployed { get; set; }
            internal bool IsColonySet { get; set; }

            private bool divided;
            private bool? isNeutral;
            private string owner;

            internal string Owner
            {
                get
                {
                    if (owner == null && Territories.Count > 0)
                    {
                        if (Territories.Count == 1) owner = host.territories[Territories.First()].Owner;
                        else
                        {
                            var ownerships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var idx in Territories)
                            {
                                var territory = host.territories[idx];
                                if (territory.IsNeutral) continue;
                                var ownerName = territory.Owner;
                                if (string.IsNullOrEmpty(ownerName)) continue;
                                ownerships.Add(ownerName.ToLower());
                            }
                            divided = ownerships.Count > 1;
                            owner = divided ? "divided" : ownerships.First();
                        }
                    }
                    return owner;
                }
            }

            internal bool IsDivided
            {
                get
                {
                    if (owner == null) _ = Owner; //force evaluation
                    return divided;
                }
            }

            internal bool IsNeutral
            {
                get
                {
                    if (isNeutral == null && Territories.Count > 0)
                    {
                        isNeutral = Territories.All(t => host.territories[t].IsNeutral);
                    }
                    return (bool)isNeutral;
                }
            }

            internal bool GatedFromOutside => Territories
                .Select(i => host.territories[i])
                .Any(t => t.Gated && host.territories.TryGetValue(t.gatedFrom, out var outside) && outside.assignedDomainId != Id);
        }
    
        internal class Slot
        {
            public Point Position { get; set; }
            public Direction Direction { get; set; }
            public string Path { get; set; }
            public Territory Territory { get; set; }
            public int AssignedDomainId => Territory?.assignedDomainId ?? -1;
            public Slot Parent { get; set; }

            public Queue<int> Staged = [];
            public bool IsSequential { get; set; }
            public List<Slot> Children = [];

            public bool Unbound => Staged.Count() > 0 || (Territory.IsGateway && !Territory.actedOnGateway);
        }

        #endregion Nest
    }
}