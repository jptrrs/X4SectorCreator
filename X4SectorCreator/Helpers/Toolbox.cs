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
    }
}