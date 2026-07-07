using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using X4SectorCreator.Helpers;
using X4SectorCreator.Objects;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace X4SectorCreator.Forms.Galaxy.ProceduralGeneration
{
    internal class Shuffler
    {
        internal string tempReport;
        internal List<string> tempReportList = new List<string>();
        internal Dictionary<int, Territory> territories = new Dictionary<int, Territory>();
        internal int occupiedMax = 0;
        internal int separation = 2;

        private static readonly (int dx, int dy)[] NeighborOffsets = new[]
        {
            (0,  2),
            (0, -2),
            (1,  1),
            (1, -1),
            (-1, 1),
            (-1,-1),
        };

        private readonly Func<Cluster, List<Point>> GetNeighbors = cluster =>
        {
            var pos = cluster.Position;
            return NeighborOffsets
                .Select(off => new Point(pos.X + off.dx, pos.Y + off.dy))
                .ToList();
        };

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

        private readonly Func<Territory, Cluster, bool> IsOutside = (territory, cluster) =>
        {
            return !territory.Clusters.Contains(cluster);
        };

        internal Shuffler(IEnumerable<Cluster> clusters)
        {
            //Group clusters into territories based adjacency and DLCs
            CarveTerritories(clusters);
            //Map connections for all clusters and register entry points for territories
            FindConnections();
            //Determine if there are neighboring territories owned by the same faction
            FindAnnexed();
            //Determine if there are other close territories owned by the same faction and separated by only a neutral sector.
            FindCloseColonies();
            //Shuffle!
            Shuffle();
            //Update Map as needed.
            if (MainForm.Instance.SectorMap.IsInitialized) MainForm.Instance.SectorMap.Value.Reset();
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
            ClusterManager.Group(clusters, SortTerritory, x => GetNeighbors(x), x => DLCMatch(x), x => AreConnected(x));
            foreach (var territory in territories.Values)
            {
                territory.SetUpBox();
            }
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
            //List<string> log = new List<string>();
            string level = "Shuffle";
            _ = LogAsync(level, $"\n\n--- Shuffling ---");

            //No turning back now!
            MainForm.Instance.AllClusters.Clear();

            List<int> cards = territories.Keys.ToList();
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(cards));
            OrderedDictionary<Point, GraphDirection> slots = new OrderedDictionary<Point, GraphDirection>() { [new Point(0, 0)] = GraphDirection.Indetermined };
            //var slot = new Point(0, 0);
            //var dir = GraphDirection.Indetermined;
            SortedSet<cPoint> occupied = new SortedSet<cPoint>();
            for (int i = 0; i < cards.Count(); i++)
            {
                //Select next territory and pick the next spot.
                int card = cards[i];
                var territory = territories[card];
                var currentPos = territory.Anchor;
                var slot = slots.First();
                var newPos = slot.Key;
                var dir = slot.Value;
                _ = LogAsync(level, $"Assigning {territory.Id}: {territory.Seed.Name}, size=({territory.Size.ToTuple()}, slot @ {newPos.ToTuple()}{dir}...",true);

                //Correct insertion point if placing up or left.
                if (dir == GraphDirection.Left)
                {
                    newPos = new Point(newPos.X - territory.Size.X, newPos.Y);
                    _ = LogAsync(level, $"Picked {newPos.ToTuple()} to fit it to the LEFT...");
                }
                else if (dir == GraphDirection.Up)
                {
                    newPos = new Point(newPos.X, newPos.Y + territory.Size.Y);
                    _ = LogAsync(level, $"Picked {newPos.ToTuple()} to fit it UP...");
                }

                //Adjust to prevet overlaps & fit hex grid
                //if (i > 0) newPos = AdjustForOverlap(territory, newPos, occupied, dir, ref report).FitToHex();
                //newPos = newPos.FitToHex();

                //Move the piece and update the board.
                var displacement = newPos.FitToHex().Subtract(currentPos);
                var msg = territory.Reposition(displacement);
                var covered = territory.Extents;
                if (i == 0) occupied.Clear();
                occupied.UnionWith(covered);
                _ = LogAsync(level, $"Moved {territory.Id}: {territory.Seed.Name} to {newPos.ToTuple()}. {covered.Count()} tiles were covered, totalling {occupied.Count()} now...");

                UpdateClusterMap(territory.Clusters, i);

                //Update the slots
                slots.RemoveAt(0);
                var nextSlots = PlanNextSpots(territory, occupied, dir);

                //var nextSlot = SpiralSpots(territory, occupied, dir);
                //slot = nextSlot.Item1;
                //dir = nextSlot.Item2;

                //Fill in the gaps too
                var gaps = FillGaps(territory, covered, nextSlots);
                occupied.UnionWith(gaps);
                _ = LogAsync(level, $"{gaps.Count()} more tiles were covered to fill in gaps, totalling {occupied.Count()} now.");

                //Removing previous slots if they were were just covered up
                covered = [.. gaps];
                if (i > 0) CleanArea(covered, ref slots);

                //Finally, add the new slots
                foreach (var s in nextSlots)
                {
                    if (!slots.ContainsKey(s.pos))
                    {
                        slots.Add(s.pos, s.dir);
                    }
                }
            }
            //MessageBox.Show($"Shuffle ajusted {report.Count} territories to avoid overlappig:{string.Join(" ,", report)}");
            occupiedMax = occupied.Max.X;
        }

        private static void CleanArea(List<cPoint> covered, ref OrderedDictionary<Point, GraphDirection> slots)
        {
            List<Point> badSlots = [.. slots.Keys.Where(p => covered.Contains(p))];
            if (badSlots.Count > 0)
            {
                _ = LogAsync("CleanArea", $"{badSlots.Count()} slots were covered and must be removed: {string.Join(", ", badSlots.Select(x => x.ToTuple()))}.");
                foreach (var s in badSlots)
                {
                    slots.Remove(s);
                }
            }
        }

        private List<cPoint> FillGaps(Territory territory, List<cPoint> covered, List<(Point pos, GraphDirection dir)> nextSlots)
        {
            var result = new List<cPoint>();
            var gap = separation - 1;
            var vectors = nextSlots.Select(x => x.dir).ToList();
            int trimUp = vectors.Contains(GraphDirection.Up) ? gap : 0;
            int trimDown = vectors.Contains(GraphDirection.Down) ? gap : 0;
            int trimRight = vectors.Contains(GraphDirection.Right) ? gap : 0;
            int trimLeft = vectors.Contains(GraphDirection.Left) ? gap : 0;
            int limitY = territory.Size.Y + trimUp + trimDown;
            int limitX = territory.Size.X + trimLeft + trimRight;
            foreach (var s in nextSlots)
            {
                var slot = s.pos;
                switch (s.dir)
                {
                    case GraphDirection.Right:
                        for (var py = 0; py < limitY; py++)
                        {
                            for (var px = 0; px < gap; px++)
                            {
                                result.AddUnique(new cPoint(slot.X - px - 1, slot.Y - py - trimUp));
                            }
                        }
                        break;
                    case GraphDirection.Down:
                        for (var px = 0; px < limitX; px++)
                        {
                            for (var py = 0; py < gap; py++)
                            {
                                result.AddUnique(new cPoint(slot.X + px - trimLeft, slot.Y + py + 1));
                            }
                        }
                        break;
                    case GraphDirection.Left:
                        for (var py = 0; py < limitY; py++)
                        {
                            for (var px = 0; px < gap; px++)
                            {
                                result.AddUnique(new cPoint(slot.X + px + 1, slot.Y - py - trimUp));
                            }
                        }
                        break;
                    case GraphDirection.Up:
                        for (var px = 0; px < territory.Size.X; px++)
                        {
                            for (var py = 0; py < gap; py++)
                            {
                                result.AddUnique(new cPoint(slot.X + px - trimLeft, slot.Y - py - 1));
                            }
                        }
                        break;
                    case GraphDirection.Indetermined:
                        break;
                }
            }
            return result;
        }

        private Point AdjustForOverlap(Territory territory, Point position, SortedSet<cPoint> occupied, GraphDirection direction, ref List<string> report)
        {
            var hits = new List<Point>();
            var floor = position.Y - territory.Size.Y;
            for (int lin = 0; lin < territory.Size.Y; lin++)
            {
                for (int col = 0; col < territory.Size.X; col++)
                {
                    var test = new Point(position.X + col, position.Y - lin);
                    if (occupied.Contains(test))
                    {
                        hits.Add(new Point(col, lin));
                        break;
                    }
                }
            }
            if (hits.Count() == 0) return position;
            int moveLeft = hits.Select(p => p.X).Max() - territory.Size.X;
            int moveUp = hits.Select(p => p.Y).Max() - territory.Size.Y;
            var target = new Point(position.X + moveLeft, position.Y + moveUp);
            report.Add($"{territory.Id}: {territory.Seed.Name}, placed {direction}, moved by {moveLeft},{moveUp}");
            return target;
        }

        private void UpdateClusterMap(List<Cluster> clusters, int errorY = 0)
        {
            foreach (var c in clusters)
            {
                if (!MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                {
                    _ = LogAsync("UpdateClusterMap", $"Error placing {c.Name}: @ {c.Position.ToTuple()}...");
                    c.Position = new Point(occupiedMax +2, errorY);
                    errorY++;
                    if (MainForm.Instance.AllClusters.TryAdd(c.Position.ToTuple(), c))
                    {
                        _ = LogAsync("UpdateClusterMap", $"Set aside @ {c.Position.ToTuple()}.");
                    }
                    else
                    {
                        _ = LogAsync("UpdateClusterMap", $"ALSO FAILED! last attempt was: {c.Position.ToTuple()}.");
                    }
                }
            }
        }

        private (Point, GraphDirection) SpiralSpots(Territory territory, SortedSet<cPoint> occupied, GraphDirection lastDir)
        {
            bool hold = false;
            bool escape = false;
            //Next slot is place in clockwise order, unless not possible, resulting in a spiral pattern.
            start:
            if (hold || lastDir == GraphDirection.Right)
            {
                var nextDown = new Point(territory.Anchor.X, territory.Anchor.Y - territory.Size.Y - separation).FitToHex(GraphDirection.Down);
                if (!occupied.Contains(nextDown)) return (nextDown, GraphDirection.Down);
                else hold= true;
            }
            if (escape) return (new Point(0, 0), GraphDirection.Indetermined);
            if (hold || lastDir == GraphDirection.Up || lastDir == GraphDirection.Indetermined)
            {
                var nextRight = new Point(territory.Anchor.X + territory.Size.X + separation, territory.Anchor.Y).FitToHex(GraphDirection.Right);
                if (!occupied.Contains(nextRight)) return (nextRight, GraphDirection.Right);
                else hold = true;
            }
            if (hold || lastDir == GraphDirection.Left)
            {
                var nextUp = new Point(territory.Anchor.X, territory.Anchor.Y + separation).FitToHex(GraphDirection.Up);
                if (!occupied.Contains(nextUp)) return (nextUp, GraphDirection.Up);
                else hold = true;
            }
            if (hold || lastDir == GraphDirection.Down)
            {
                var nextLeft = new Point(territory.Anchor.X - separation, territory.Anchor.Y).FitToHex(GraphDirection.Left);
                if (!occupied.Contains(nextLeft)) return (nextLeft, GraphDirection.Left);
                else hold = true;
            }
            escape = true;
            goto start;
        }

        private List<(Point pos, GraphDirection dir)> PlanNextSpots(Territory territory, SortedSet<cPoint> occupied, GraphDirection lastDir)
        {
            ////Correct for odd size numbers so we don't get invalid hexagon displacements.
            //int snapHeight = (int)Math.Ceiling(territory.Size.Y / 2.0) * 2;
            //int snapWidth = (int)Math.Ceiling(territory.Size.X / 2.0) * 2;

            //Possible positions around for the next territory to be placed.
            var nextRight = new Point(territory.Anchor.X + territory.Size.X + separation, territory.Anchor.Y).FitToHex(GraphDirection.Right);
            var nextDown = new Point(territory.Anchor.X, territory.Anchor.Y - territory.Size.Y - separation).FitToHex(GraphDirection.Down);
            var nextLeft = new Point(territory.Anchor.X - separation, territory.Anchor.Y).FitToHex(GraphDirection.Left);
            var nextUp = new Point(territory.Anchor.X, territory.Anchor.Y + separation).FitToHex(GraphDirection.Up);

            //logging
            List<string> log = new List<string>();
            void Log(Point slot, GraphDirection dir)
            {
                log.Add($"{slot.ToTuple().ToString()}{dir}");
            }

            var result = new List<(Point, GraphDirection)>();

            //Place future slots, in clockwise order
            if (lastDir != GraphDirection.Left && (occupied.MaxBy(p => p.X).X < nextRight.X || !occupied.Contains(nextRight)))
            {
                result.Add((nextRight, GraphDirection.Right));
                //slots.TryAdd(nextRight, GraphDirection.Right);
                Log(nextRight, GraphDirection.Right);
            }
            if (lastDir != GraphDirection.Up && (occupied.MinBy(p => p.Y).Y > nextDown.Y || !occupied.Contains(nextDown)))
            {
                result.Add((nextDown, GraphDirection.Down));
                Log(nextDown, GraphDirection.Down);
            }
            if (lastDir != GraphDirection.Right && (occupied.MinBy(p => p.X).X > nextLeft.X || !occupied.Contains(nextLeft)))
            {
                result.Add((nextLeft, GraphDirection.Left));
                Log(nextLeft, GraphDirection.Left);
            }
            if (lastDir != GraphDirection.Down && (occupied.MaxBy(p => p.Y).Y < nextUp.Y || !occupied.Contains(nextUp)))
            {
                result.Add((nextUp, GraphDirection.Up));
                Log(nextUp, GraphDirection.Up);
            }

            if (result.Count() == 0)
            {
                _ = LogAsync("PlanNextSpots", $"No Slots found for {territory.Id}: {territory.Seed.Name}! Attempted:\nR.{nextRight.ToTuple()}, D.{nextDown.ToTuple()}, L.{nextLeft.ToTuple()}, U.{nextUp.ToTuple()}.");
            }
            _ = LogAsync("PlanNextSpots", $"Slots around {territory.Id}: {territory.Seed.Name}: {string.Join(", ", log)}.");
            return result;
        }

        static async Task LogAsync(string level, string message, bool lineSkip = false)
        {
            string jump = lineSkip ? "\n" : "";
            string logEntry = $"{jump}[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync("test.log", logEntry);
        }
    }

    public enum GraphDirection
    {
        Right = 0,
        Down = 1,
        Left = 2,
        Up = 3,
        Indetermined = 4
    }
}