using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Just enough of the .xlsx format to read the department's spreadsheets and
    /// write schedules back out, without pulling in a NuGet package. An xlsx file
    /// is a zip of XML parts: a workbook listing sheets, one XML file per sheet,
    /// and a shared string table that most text cells point into.
    /// </summary>
    public static class Xlsx
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public class Sheet
        {
            public string Name { get; init; } = "";

            /// <summary>Rows as dense string lists. Empty cells are "".</summary>
            public List<List<string>> Rows { get; } = new();

            public string Cell(int row, int col) =>
                row < Rows.Count && col < Rows[row].Count ? Rows[row][col] : "";
        }

        public static List<Sheet> Read(string path)
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }

        public static List<Sheet> Read(Stream stream)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var shared = new List<string>();
            var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (sharedEntry != null)
            {
                var doc = Load(sharedEntry);
                foreach (var si in doc.Root!.Elements(Main + "si"))
                    shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
            }

            var rels = Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!).Root!
                .Elements(PkgRel + "Relationship")
                .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

            var sheets = new List<Sheet>();
            var workbook = Load(zip.GetEntry("xl/workbook.xml")!);

            foreach (var s in workbook.Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
            {
                string target = rels[(string)s.Attribute(Rel + "id")!].TrimStart('/');
                if (!target.StartsWith("xl/")) target = "xl/" + target;

                var entry = zip.GetEntry(target);
                if (entry == null) continue;

                var sheet = new Sheet { Name = (string)s.Attribute("name")! };
                foreach (var row in Load(entry).Descendants(Main + "row"))
                {
                    int rowIndex = int.Parse((string)row.Attribute("r")!) - 1;
                    while (sheet.Rows.Count <= rowIndex) sheet.Rows.Add(new List<string>());
                    var cells = sheet.Rows[rowIndex];

                    foreach (var c in row.Elements(Main + "c"))
                    {
                        int col = ColumnIndex((string)c.Attribute("r")!);
                        while (cells.Count <= col) cells.Add("");
                        cells[col] = CellText(c, shared);
                    }
                }

                sheets.Add(sheet);
            }

            return sheets;
        }

        private static string CellText(XElement c, List<string> shared)
        {
            string? type = (string?)c.Attribute("t");
            string? value = c.Element(Main + "v")?.Value;

            return type switch
            {
                "s" when value != null => shared[int.Parse(value)],
                "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
                _ => value ?? ""
            };
        }

        private static XDocument Load(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            return XDocument.Load(s);
        }

        /// <summary>"C12" becomes 2.</summary>
        public static int ColumnIndex(string cellRef)
        {
            int n = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return n - 1;
        }

        public static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Excel stores clock times as a fraction of a day, so 0.5208333 is 12:30.
        /// Returns null for anything that is not such a number.
        /// </summary>
        public static TimeSpan? DayFraction(string raw)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) ||
                f < 0 || f >= 1)
                return null;

            return TimeSpan.FromMinutes(Math.Round(f * 24 * 60));
        }

        // ---------------------------------------------------------------- writing

        /// <summary>A cell to write: text, plus an optional fill colour as "RRGGBB".</summary>
        public record OutCell(string Text, string? Fill = null, bool Bold = false);

        public class OutSheet
        {
            public string Name { get; init; } = "";
            public List<List<OutCell?>> Rows { get; } = new();
            public List<double> ColumnWidths { get; } = new();

            /// <summary>Colour of the sheet's tab, as "RRGGBB", or null for none.</summary>
            public string? TabColor { get; set; }

            public void Set(int row, int col, OutCell cell)
            {
                while (Rows.Count <= row) Rows.Add(new List<OutCell?>());
                while (Rows[row].Count <= col) Rows[row].Add(null);
                Rows[row][col] = cell;
            }

            public void AddRow(IEnumerable<string> values, bool bold = false) =>
                Rows.Add(values.Select(v => (OutCell?)new OutCell(v, null, bold)).ToList());
        }

        /// <summary>
        /// Writes a workbook with inline strings and a small style table built from
        /// whatever fills and bold flags the cells use.
        /// </summary>
        public static byte[] Write(IEnumerable<OutSheet> sheetsIn)
        {
            var sheets = sheetsIn.ToList();

            // Style 0 is the default. Each distinct (fill, bold) pair gets its own index.
            var styles = new List<(string? Fill, bool Bold)> { (null, false) };
            int StyleOf(OutCell c)
            {
                int i = styles.IndexOf((c.Fill, c.Bold));
                if (i >= 0) return i;
                styles.Add((c.Fill, c.Bold));
                return styles.Count - 1;
            }

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = sheets.Select(s => UniqueSheetName(s.Name, usedNames)).ToList();

                for (int i = 0; i < sheets.Count; i++)
                    Put(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i], StyleOf));

                Put(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    string.Concat(Enumerable.Range(1, sheets.Count).Select(i =>
                        $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                    "</Types>");

                Put(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                Put(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                    string.Concat(names.Select((n, i) =>
                        $"<sheet name=\"{Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                    "</sheets></workbook>");

                Put(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    string.Concat(Enumerable.Range(1, sheets.Count).Select(i =>
                        $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>")) +
                    $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");

                Put(zip, "xl/styles.xml", StylesXml(styles));
            }

            return ms.ToArray();
        }

        private static string SheetXml(OutSheet sheet, Func<OutCell, int> styleOf)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (sheet.TabColor != null)
                sb.Append($"<sheetPr><tabColor rgb=\"FF{sheet.TabColor}\"/></sheetPr>");

            if (sheet.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < sheet.ColumnWidths.Count; i++)
                    sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sheet.ColumnWidths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                var row = sheet.Rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    var cell = row[c];
                    if (cell == null) continue;

                    int style = styleOf(cell);
                    string styleAttr = style == 0 ? "" : $" s=\"{style}\"";
                    string reference = ColumnName(c) + (r + 1);

                    // Always text. Writing numbers would turn room "2.210" into 2.21.
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"{styleAttr}><is><t xml:space=\"preserve\">{Esc(cell.Text)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string StylesXml(List<(string? Fill, bool Bold)> styles)
        {
            var fills = styles.Select(s => s.Fill).Where(f => f != null).Distinct().ToList();

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                      "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>");

            // Excel reserves fill 0 (none) and fill 1 (gray125).
            sb.Append($"<fills count=\"{fills.Count + 2}\"><fill><patternFill patternType=\"none\"/></fill>" +
                      "<fill><patternFill patternType=\"gray125\"/></fill>");
            foreach (var f in fills)
                sb.Append($"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{f}\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            sb.Append("</fills>");

            sb.Append("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

            sb.Append($"<cellXfs count=\"{styles.Count}\">");
            foreach (var (fill, bold) in styles)
            {
                int fillId = fill == null ? 0 : fills.IndexOf(fill) + 2;
                sb.Append($"<xf numFmtId=\"0\" fontId=\"{(bold ? 1 : 0)}\" fillId=\"{fillId}\" borderId=\"0\" xfId=\"0\"" +
                          $"{(fillId > 0 ? " applyFill=\"1\"" : "")}{(bold ? " applyFont=\"1\"" : "")}>" +
                          "<alignment wrapText=\"1\" vertical=\"top\"/></xf>");
            }
            sb.Append("</cellXfs></styleSheet>");
            return sb.ToString();
        }

        private static string UniqueSheetName(string name, HashSet<string> used)
        {
            var clean = new string(name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray());
            if (clean.Length == 0) clean = "Sheet";
            if (clean.Length > 31) clean = clean.Substring(0, 31);

            string candidate = clean;
            for (int i = 2; !used.Add(candidate); i++)
                candidate = clean.Substring(0, Math.Min(clean.Length, 28)) + " " + i;
            return candidate;
        }

        private static void Put(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
    }
}
