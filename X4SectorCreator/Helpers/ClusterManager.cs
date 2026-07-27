using X4SectorCreator.Objects;

namespace X4SectorCreator.Helpers
{
    internal static class ClusterManager
    {
        internal static List<(Cluster, Sector, Gate, Sector)> PickDestinations(IEnumerable<Cluster> items, Predicate<Cluster> filter)
        {
            var result = new List<(Cluster, Sector, Gate, Sector)>();
            foreach (Cluster cluster in items)
            {
                result.AddRange(PickDestinationsFromCluster(cluster).Select(x => (cluster, x.Item1, x.Item2, x.Item3)));
            }
            return result;
        }

        internal static List<(Sector, Gate, Sector)> PickDestinationsFromCluster(Cluster cluster, Predicate<Cluster> filter = null)
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

        internal static (int cols, int rows) FrameHexGrid(List<Cluster> allClusters, int margin = 0)
        {
            int cols, rows = 0;

            if (allClusters.Count == 0) // Check if the list is empty
            {
                cols = (margin * 2) + 1;
                rows = ((int)(margin / 2 * 1.5f)) + 1;
            }
            else
            {
                cols = ((Math.Max(Math.Abs(allClusters.Max(a => a.Position.X)), Math.Abs(allClusters.Min(a => a.Position.X))) + margin) * 2) + 1;
                rows = ((int)((Math.Max(Math.Abs(allClusters.Max(b => b.Position.Y)), Math.Abs(allClusters.Min(b => b.Position.Y))) + (margin / 2)) * 1.5f)) + 1;
            }
            return (cols, rows);
        }

        public static Point RotateOrtho(Point current, double cx, double cy, int turns)
        {
            // Translate point relative to the center origin
            double dx = current.X - cx;
            double dy = current.Y - cy;

            (double rx, double ry) = turns switch
            {
                1 => (cx + dy / 2, cy - 2 * dx), // 90° Clockwise
                2 => (cx - dx, cy - dy), // 180° Rotation (Inversion)
                3 => (cx - dy / 2, cy + 2 * dx), // 270° Clockwise
                0 => (current.X, current.Y), // No rotation
                _ => throw new ArgumentException("turn parameter should be 1, 2 or 3.")
            };

            return new Point((int)Math.Round(rx,MidpointRounding.AwayFromZero), (int)Math.Round(ry,MidpointRounding.AwayFromZero)).FitToHex();

            //Aternative with finer control over rounding. It works, but vertical territories can get too spread out.

            ////Round as needed, considering Hex grid.
            //Point result = Point.Empty;
            //bool XneedsRounding = rx % 1 != 0;
            //bool YneedsRounding = ry % 1 != 0;
            //if (!XneedsRounding && !YneedsRounding)
            //{
            //    //In that case, regular fitting logic will do.
            //    result = new Point((int)rx, (int)ry).FitToHex();
            //}
            //else
            //{
            //    if (XneedsRounding && !YneedsRounding)
            //    {
            //        rx = ry % 2 == 0 ? rx = Math.Round(rx) : rx = Math.Round(rx - 1) + 1; // to round it to the nearest odd number
            //        // -7,9 / -5,9
            //    }
            //    else if (YneedsRounding && !XneedsRounding)
            //    {
            //        ry = rx % 2 == 0 ? ry = Math.Round(ry) : ry = Math.Round(ry - 1.0) + 1; // to round it to the nearest odd number
            //    }
            //    else if (XneedsRounding && YneedsRounding)
            //    {
            //        rx = Math.Round(rx);
            //        ry = Math.Round(ry);
            //    }
            //    result = new Point((int)rx, (int)ry);
            //}
            //return result;
        }
    }
}