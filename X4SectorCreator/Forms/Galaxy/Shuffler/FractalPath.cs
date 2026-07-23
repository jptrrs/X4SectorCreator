namespace X4SectorCreator.Forms.Galaxy.Shuffler
{
    using System;

    public enum Direction
    {
        Undefined = 0,
        Right = 1,
        Down = 2,
        Left = 3,
        Up = 4,
    }

    //This is just a bunch of specialized extensions.
    internal static class FractalPath
    {
        public static string DownstreamAddress(this string path, Direction dir)
        {
            if (dir == Direction.Undefined) throw new ArgumentException("Cannot use an Undefined direction.");
            if (path == "0") path = "";
            return path + ((int)dir).ToString();
        }

        public static string GetParentAddress(this string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length == 1) return "0";
            return path.Substring(0, path.Length - 1);
        }

        public static string GetAddressAtDepth(this string path, int targetDepth)
        {
            if (string.IsNullOrEmpty(path) || targetDepth <= 0) return "0"; // Returns the root address
            if (targetDepth >= path.Length) return path;
            return path.Substring(0, targetDepth);
        }

        public static Direction GetDirection(this string path)
        {
            if (string.IsNullOrEmpty(path)) return Direction.Undefined;
            int lastDigit = path[path.Length - 1] - '0'; //this subtraction is a hack to quickly convert a numeric char into an actual int.
            return (Direction)lastDigit;
        }

        public static Direction GetMainBranch(this string path)
        {
            if (string.IsNullOrEmpty(path)) return Direction.Undefined;
            int firstDigit = path[0] - '0'; //this subtraction is a hack to quickly convert a numeric char into an actual int.
            return (Direction)firstDigit;
        }

        public static Direction OppositeDir(this Direction given)
        {
            if (given == Direction.Undefined) return given;
            return (Direction)(((int)given + 2) % 4);
        }

        internal static int ClockwiseStepsTo(this Direction from, Direction to)
        {
            return ((int)to - (int)from + 4) % 4;
        }
    }
}
