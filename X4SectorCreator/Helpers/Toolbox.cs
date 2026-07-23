using System.Collections.Generic;
using X4SectorCreator.Objects;

namespace X4SectorCreator.Helpers
{
    internal static class Toolbox
    {
        internal static IEnumerable<Point> Spread(int limitA, int limitB, Func<(int a, int b), Point> form, Predicate<Point> filter = null, bool filtered = false)
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

        internal static async Task LogAsync(string level, string message, bool lineSkip = false)
        {
            string jump = lineSkip ? "\n" : "";
            string logEntry = $"{jump}[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync("test.log", logEntry);
        }

        /// <summary>
        /// Iterates a collection by processing each item, then selecting a subset based on a defined criteria and optional filter and/or comparsion, then recursively feeding it back to be processed the same way . Ex.: use to group a collection of clusters into territories by selecting neighbours which belong to the same faction and can be reached directly.
        /// </summary>
        /// <param name="items">The collection to be iterated upon. Must be pre-ordered, and that order will guide the propagation.</param>
        /// <param name="process">An action to be applied to all items, the bool acting as a trigger to apply seed instructions for first-level iterations only (i.e.: the branching points).</param>
        /// <param name="selector">The criteria to pick a subset to iterate. Must take the current iterated item and the remaining crowd and return a small selection (ex.: get all neighbors)</param>
        /// <param name="filter">(Optional) A hard rule to pick the items for the selected subset (ex.: if it's close enough).</param>
        /// <param name="comparsion">(Optional) A criteria to pick the items for the selected subset by comparsion with the originator (ex.: if there is a connection between them). </param>
        /// <param name="progress">(Optional) A progress indicator.</param>
        /// <param name="cancellationToken">(Optional) A cancellation token.</param>
        internal static void FlexFloodProcessor<T1>(
            List<T1> items,
            Action<T1, bool> process,
            Func<T1, HashSet<T1>, IEnumerable<T1>> selector,
            Predicate<T1> filter = null,
            Predicate<(T1, T1)> comparsion = null,
            IProgress<int> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (items.Count == 0) return;

            var remaining = new HashSet<T1>(items);
            int total = remaining.Count;
            int processed = 0;

            void Report()
            {
                int percent = Math.Clamp(processed * 100 / total, 0, 100);
                progress?.Report(percent);
            }

            var queue = new Queue<T1>();

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Gets the first unvisited seed matching the original list order
                T1 nextSeed = items.First(remaining.Contains);

                queue.Enqueue(nextSeed);
                remaining.Remove(nextSeed);
                bool isSeed = true;

                // Breadth-First Search (BFS)
                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var current = queue.Dequeue();

                    process(current, isSeed);
                    isSeed = false;

                    processed++;
                    Report();

                    var neighbors = selector(current, remaining);
                    if (neighbors == null) continue;

                    foreach (var item in neighbors)
                    {
                        if (!remaining.Contains(item)) continue;

                        bool filterCheck = filter == null || filter(item);
                        bool comparsionCheck = comparsion == null || comparsion((current, item));

                        if (filterCheck && comparsionCheck)
                        {
                            queue.Enqueue(item);
                            remaining.Remove(item);
                        }
                    }
                }
            }

            progress?.Report(100);
        }
    }
}