using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Where buildings are, and roughly how long it takes to walk between them.
    /// Positions are pixel coordinates read off the registrar's GPC building map
    /// (data/buildings.csv), so distances are estimates, good enough to tell a
    /// two-minute walk from a twelve-minute one.
    /// </summary>
    public class Campus
    {
        // On the map, E 21st St to Dean Keeton is about 425 pixels, which is about
        // 800 meters on the ground.
        private const double MetersPerPixel = 1.9;

        // Paths are not straight lines.
        private const double DetourFactor = 1.3;

        private const double WalkMetersPerMinute = 80;

        private readonly Dictionary<string, (double X, double Y)> _positions =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Buildings => _positions.Keys;

        public bool Knows(string building) => _positions.ContainsKey(building);

        /// <summary>
        /// Names people write on the preference form instead of the building code.
        /// </summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Welch"] = "WEL",
            ["Patton"] = "RLP",
            ["Patton Hall"] = "RLP",
            ["Bellmont"] = "BEL",
            ["Belmont"] = "BEL",
            ["Jester"] = "JES",
            ["Gates"] = "GDC",
            ["Gates Dell"] = "GDC",
            ["Sanchez"] = "SZB",
            ["Burdine"] = "BUR",
            ["Waggener"] = "WAG",
            ["Garrison"] = "GAR",
            ["McCombs"] = "CBA",
            ["University Teaching Center"] = "UTC",
            ["Teaching Center"] = "UTC",
            ["Cockrell"] = "ECJ",
            ["Parlin"] = "PAR",
            ["Mezes"] = "MEZ",
            ["Calhoun"] = "CAL",
            ["Benedict"] = "BEN",
            ["Painter"] = "PAI",
            ["Pharmacy"] = "PHR",
            ["Art building"] = "ART",
        };

        public static Campus Load(string path) => FromCsvText(DataFile.ReadText(path));

        public static Campus FromCsvText(string csvText)
        {
            var campus = new Campus();
            var lines = Csv.Lines(csvText).ToArray();
            if (lines.Length == 0)
                return campus;

            var headers = Csv.ParseLine(lines[0]).Select(h => h.Trim()).ToList();
            int b = headers.IndexOf("building"), n = headers.IndexOf("name"),
                x = headers.IndexOf("map_x"), y = headers.IndexOf("map_y");

            foreach (var line in lines.Skip(1))
            {
                var f = Csv.ParseLine(line);
                if (f.Count <= Math.Max(x, y)) continue;
                if (!double.TryParse(f[x], out double px) || !double.TryParse(f[y], out double py)) continue;

                campus._positions[f[b].Trim()] = (px, py);
                campus.Names[f[b].Trim()] = f[n].Trim();
            }

            return campus;
        }

        /// <summary>Estimated walk in minutes, or null if either building is not on the map.</summary>
        public double? WalkMinutes(string from, string to)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (!_positions.TryGetValue(from, out var a) || !_positions.TryGetValue(to, out var b))
                return null;

            double pixels = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            return pixels * MetersPerPixel * DetourFactor / WalkMetersPerMinute;
        }

        /// <summary>
        /// Building codes mentioned in free text, whether written as a code ("RLP")
        /// or a name ("Patton Hall"), in the order they appear.
        /// </summary>
        public List<(string Code, int Position)> FindBuildings(string text)
        {
            var found = new List<(string, int)>();

            foreach (var code in _positions.Keys)
            {
                foreach (var pos in WordPositions(text, code, caseSensitive: true))
                    found.Add((code, pos));
            }

            foreach (var (alias, code) in Aliases)
            {
                foreach (var pos in WordPositions(text, alias, caseSensitive: false))
                    found.Add((code, pos));
            }

            return found.OrderBy(f => f.Item2).Distinct().ToList();
        }

        private static IEnumerable<int> WordPositions(string text, string word, bool caseSensitive)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int i = 0;
            while ((i = text.IndexOf(word, i, cmp)) >= 0)
            {
                bool startOk = i == 0 || !char.IsLetter(text[i - 1]);
                int end = i + word.Length;
                bool endOk = end >= text.Length || !char.IsLetter(text[end]);
                if (startOk && endOk)
                    yield return i;
                i = end;
            }
        }
    }
}
