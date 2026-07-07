
using X4SectorCreator.Objects;

namespace X4SectorCreator.Helpers
{
    internal static class ClusterManager
    {
        public static void Group(
            IEnumerable<Cluster> items,
            Action<Cluster, bool> process,
            Func<Cluster, List<Point>> selector,
            Predicate<Cluster> filter = null,
            Predicate<(Cluster, Cluster)> comparsion = null,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var seed = items.FirstOrDefault();
            var remaining = new HashSet<Cluster>(items);
            int total = remaining.Count;
            if (total <= 0) return;
            var posIndex = remaining.ToDictionary(c => c.Position);

            int processed = 0;

            void Report()
            {
                int percent = Math.Clamp(processed * 100 / total, 0, 100);
                progress?.Report(percent);
            }

            void Iterate(Cluster current, bool reset)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!remaining.Remove(current)) return;

                posIndex.Remove(current.Position);

                process(current, reset);
                processed++;
                Report();

                var neighbors = selector(current);
                if (neighbors == null) return;

                foreach (var pos in neighbors)
                {
                    if (posIndex.TryGetValue(pos, out Cluster neighborCluster))
                    {
                        bool filterCheck = filter == null || filter(neighborCluster);
                        bool comparsionCheck = comparsion == null || comparsion((current, neighborCluster));
                        if (filterCheck && comparsionCheck)
                        {
                            Iterate(neighborCluster, false);
                        }
                    }
                }
            }

            if (remaining.Contains(seed))
            {
                Iterate(seed, true);
            }

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextSeed = remaining.First();
                Iterate(nextSeed, true);
            }

            progress?.Report(100);
        }

        public static List<(Cluster, Sector, Gate, Sector)> PickDestinations(IEnumerable<Cluster> items, Predicate<Cluster> filter)
        {
            var result = new List<(Cluster, Sector, Gate, Sector)>();
            foreach (Cluster cluster in items)
            {
                result.AddRange(PickDestinationsFromCluster(cluster).Select(x => (cluster, x.Item1, x.Item2, x.Item3)));
            }
            return result;
        }

        public static List<(Sector, Gate, Sector)> PickDestinationsFromCluster(Cluster cluster, Predicate<Cluster> filter = null)
        {
            var result = new List<(Sector, Gate, Sector)>();
            if (cluster.Sectors?.Count == 0) return result;
            foreach (Sector sector in cluster.Sectors)
            {
                if (sector.Zones?.Count == 0) continue;
                foreach (Zone zone in sector.Zones)
                {
                    if (zone.Gates?.Count == 0) continue;
                    foreach (Gate gate in zone.Gates)
                    {
                        Sector destSector = gate.FindDestination(out Cluster destCluster);
                        if (filter != null && !filter(destCluster)) continue;
                        result.Add((sector, gate, destSector));
                    }
                }
            }
            return result;
        }
    }
}