namespace X4SectorCreator.Objects
{
    internal readonly struct cPoint : IComparable<cPoint>, IComparable<Point>
    {
        public int X { get; }
        public int Y { get; }

        public cPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int CompareTo(cPoint other)
        {
            var cmp = X.CompareTo(other.X);
            return cmp != 0 ? cmp : Y.CompareTo(other.Y);
        }

        public int CompareTo(Point other)
        {
            var cmp = X.CompareTo(other.X);
            return cmp != 0 ? cmp : Y.CompareTo(other.Y);
        }

        public bool Equals(cPoint other) => X == other.X && Y == other.Y;
        public bool Equals(Point other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) =>
            obj switch
            {
                cPoint p => Equals(p),
                Point p => Equals(p),
                _ => false
            };

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public static bool operator <(cPoint left, cPoint right) => left.CompareTo(right) < 0;
        public static bool operator >(cPoint left, cPoint right) => left.CompareTo(right) > 0;

        public static implicit operator Point(cPoint p) => new Point(p.X, p.Y);
        public static implicit operator cPoint(Point p) => new cPoint(p.X, p.Y);
    }
}