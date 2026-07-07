using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using X4SectorCreator.Objects;

namespace X4SectorCreator
{
    internal static class ClusterProcessor
    {
        public static void Group(
            IEnumerable<Cluster> items,
            Action<Cluster> process,
            Func<Cluster, List<Point>> selector,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (items is null) throw new ArgumentNullException(nameof(items));
            if (process is null) throw new ArgumentNullException(nameof(process));
            if (selector is null) throw new ArgumentNullException(nameof(selector));

            var seed = items.FirstOrDefault();
            var remaining = new HashSet<Cluster>(items);
            var posIndex = items.ToDictionary(c => c.Position);
            int total = remaining.Count;
            if (total <= 0) return;

            int processed = 0;

            void Report()
            {
                int percent = Math.Clamp(processed * 100 / total, 0, 100);
                progress?.Report(percent);
            }

            void Iterate(Cluster current)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!remaining.Remove(current)) return;

                process(current);
                processed++;
                Report();

                var neighbors = selector(current);
                if (neighbors == null) return;

                var neighborPoints = neighbors as IList<Point> ?? neighbors.ToList();
                foreach (var pt in neighborPoints)
                {
                    if (positionIndex.TryGetValue(pt, out var neighborCluster) &&
                        remaining.Contains(neighborCluster))
                    {
                        Iterate(neighborCluster);
                    }
                }
            }

            if (remaining.Contains(seed))
            {
                Iterate(seed);
            }

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextSeed = remaining.First();
                Iterate(nextSeed);
            }

            progress?.Report(100);
        }
    }
}