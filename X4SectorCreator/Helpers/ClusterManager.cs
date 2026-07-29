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

        public static Point PivotForRotation(Point pos)
        {
            if (pos.X % 2 != 0 && pos.Y % 2 != 0)
            {
                return new Point(pos.X + 1, pos.Y + 1);
            }
            return pos;
        }

        public static Point RotateOrtho(Point current, Point pivot, int turns)
        {
            if (turns <= 0 || turns > 3) return current; //No rotation
            pivot = PivotForRotation(pivot);
            double dx = current.X - pivot.X;
            double dy = current.Y - pivot.Y;
            (double x, double y) rotated = (0d,0d);
            if (turns == 2) //180° Rotation
            {
                rotated = (pivot.X - dx, pivot.Y - dy);
            }
            else
            {
                //de-stagger every other column
                if (current.X % 2 != 0) dy -= 1;
                if (turns == 1) // 90° Clockwise
                {
                    rotated = (pivot.X + dy / 2, pivot.Y - 2 * dx);
                }
                else //turns = 3, 270° Clockwise
                {
                    rotated = (pivot.X - dy / 2, pivot.Y + 2 * dx);
                }
                //re-stagger every other column
                if (rotated.x % 2 != 0) rotated.y -= 1;
            }
            return new Point((int)Math.Round(rotated.x, MidpointRounding.AwayFromZero), (int)Math.Round(rotated.y, MidpointRounding.AwayFromZero));
        }

        //public static Point RotateOrtho(Point current, double cx, double cy, int turns)
        //{
        //    // Translate point relative to the center origin
        //    double dx = current.X - cx;
        //    double dy = current.Y - cy;

        //    (double rx, double ry) = turns switch
        //    {
        //        1 => (cx + dy / 2, cy - 2 * dx), // 90° Clockwise
        //        2 => (cx - dx, cy - dy), // 180° Rotation (Inversion)
        //        3 => (cx - dy / 2, cy + 2 * dx), // 270° Clockwise
        //        0 => (current.X, current.Y), // No rotation
        //        _ => throw new ArgumentException("turn parameter should be 1, 2 or 3.")
        //    };

        //    return new Point((int)Math.Round(rx, MidpointRounding.AwayFromZero), (int)Math.Round(ry, MidpointRounding.AwayFromZero)).FitToHex();
        //}
    }
}