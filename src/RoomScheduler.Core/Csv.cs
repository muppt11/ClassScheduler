using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RoomScheduler
{
    /// <summary>
    /// Minimal CSV helper. Good enough for registrar exports and the extracted
    /// course history, and not a full RFC 4180 parser.
    /// </summary>
    public static class Csv
    {
        /// <summary>
        /// Splits one CSV line into fields, honouring quoted fields that contain
        /// commas and doubled quotes used as an escaped quote.
        /// </summary>
        /// <summary>The lines of a file's text, whatever line endings it uses.</summary>
        public static IEnumerable<string> Lines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Length > 0);

        public static List<string> ParseLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields;
        }
    }
}
