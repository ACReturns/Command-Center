using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CommandCenter.Model;

namespace CommandCenter.Services
{
    /// <summary>
    /// Turns the version sheet's CSV export (columns: Ver, Tag, Checksum) into ChecksumSheetRows.
    ///
    /// Skip rules, based on the sheet's actual layout:
    ///  - header row ("Ver, Tag, Checksum")          -> Ver isn't a version
    ///  - banner row ("CleanUp Branches in Arena...") -> Ver isn't a version
    ///  - family rows ("v268", "v269" ...)            -> no dot in the version
    ///  - orphan checksum link with no Ver/Tag        -> Ver empty
    /// Duplicate versions (v271.2.0 appears twice) are kept - rows are keyed by Tag, not Ver.
    /// Tag text is kept exactly as written; naming is inconsistent (pub_268 vs pub268) so nothing is parsed out of it.
    /// </summary>
    public static class ChecksumSheetParser
    {
        private static readonly Regex VersionPattern = new(@"^v?\d+(\.\d+)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Handles both /drive/folders/ID and /drive/u/0/folders/ID
        private static readonly Regex FolderIdPattern = new(@"/folders/([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);
        private static readonly Regex FileIdPattern = new(@"/file/d/([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);
        private static readonly Regex IdParamPattern = new(@"[?&]id=([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);

        private static readonly Regex SheetIdPattern = new(@"/spreadsheets/d/([A-Za-z0-9_-]+)", RegexOptions.Compiled);
        private static readonly Regex GidPattern = new(@"[#?&]gid=(\d+)", RegexOptions.Compiled);

        public static List<ChecksumSheetRow> Parse(string csv)
        {
            var rows = new List<ChecksumSheetRow>();
            var lineNumber = 0;

            foreach (var fields in ReadCsv(csv.TrimStart('\uFEFF')))
            {
                lineNumber++;
                if (fields.Count < 2) continue;

                var version = fields[0].Trim();
                var tag = fields[1].Trim();
                var link = fields.Count > 2 ? fields[2].Trim() : string.Empty;

                if (!VersionPattern.IsMatch(version) || tag.Length == 0) continue;

                string? folderId = Match(FolderIdPattern, link);
                string? fileId = folderId == null ? Match(FileIdPattern, link) ?? Match(IdParamPattern, link) : null;

                rows.Add(new ChecksumSheetRow(
                    version,
                    tag,
                    link.Length == 0 ? null : link,
                    folderId,
                    fileId,
                    lineNumber));
            }

            return rows;
        }

        /// <summary>"v271.3.0" and "271.3.0" compare equal. Tab version numbers are entered in the same order as the sheet.</summary>
        public static string NormalizeVersion(string? version) =>
            (version ?? string.Empty).Trim().TrimStart('v', 'V');

        public static bool TryParseSheetUrl(string? url, out string sheetId, out string gid)
        {
            sheetId = string.Empty;
            gid = "0";
            if (string.IsNullOrWhiteSpace(url)) return false;

            var id = SheetIdPattern.Match(url);
            if (!id.Success) return false;

            sheetId = id.Groups[1].Value;
            var g = GidPattern.Match(url);
            if (g.Success) gid = g.Groups[1].Value;
            return true;
        }

        public static string CsvExportUrl(string sheetId, string gid) =>
            $"https://docs.google.com/spreadsheets/d/{sheetId}/export?format=csv&gid={gid}";

        private static string? Match(Regex pattern, string text)
        {
            var m = pattern.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Minimal RFC 4180 reader (quoted fields, escaped quotes, CRLF).</summary>
        private static IEnumerable<List<string>> ReadCsv(string text)
        {
            var field = new StringBuilder();
            var row = new List<string>();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        yield return row;
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }
    }
}
