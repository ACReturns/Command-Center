using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandCenter.Services
{
    /// <summary>
    /// What changed in a release, pulled from the Maple Central Confluence page's
    /// "Build Release / Version Updates" section. Only the "Added" and "Updates" headers are read;
    /// a header whose only content is N/A is dropped, and if both are empty there are no notes.
    /// </summary>
    public sealed record ReleaseNotes(IReadOnlyList<string> Added, IReadOnlyList<string> Updates)
    {
        public bool IsEmpty => Added.Count == 0 && Updates.Count == 0;

        /// <summary>Plain-text block for the "Update Available" dialog.</summary>
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            void Section(string title, IReadOnlyList<string> items)
            {
                if (items.Count == 0) return;
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(title);
                foreach (var item in items) sb.AppendLine($"  •  {item}");
            }
            Section("Added:", Added);
            Section("Updates:", Updates);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Turns the page's rendered HTML (Confluence REST body.view) into ReleaseNotes.
    ///
    /// Layouts handled, because the page is hand-edited:
    ///  - The section title as a heading (h1-h6), a bold line or a table header cell, matched loosely
    ///    ("Build Release/ Version Updates", "Build Release / Version Update", ...).
    ///  - "Added" / "Updates" as headings, bold lines, table header cells or "Added:" label lines,
    ///    with the content as bullets, paragraphs or table cells under them - or inline
    ///    ("Added: new tab icons").
    ///  - Several versions listed inside the section: if the new build's code (e.g. ADW-MC-07)
    ///    appears there, only that version's block is read; otherwise the first Added/Updates found.
    /// The section ends at the next heading of the same or higher level as its title.
    /// </summary>
    public static class ReleaseNotesParser
    {
        private enum LineKind { Heading, ListItem, TableHeader, Text }

        private sealed record Line(LineKind Kind, int Level, string Text, bool IsBold);

        private const char Marker = '\u0001';   // start of a block-type marker
        private const char MarkerEnd = '\u0002';
        private const char BoldOpen = '\u0003';
        private const char BoldClose = '\u0004';

        private static readonly Regex BuildCodePattern = new(@"\b[A-Z]{2,}-[A-Z]{2,}-\d+\b", RegexOptions.IgnoreCase);

        public static ReleaseNotes? Parse(string? html, string? buildCode)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            var lines = Flatten(html);

            // 1) The "Build Release / Version Updates" section.
            int start = lines.FindIndex(l => IsSectionTitle(l.Text));
            if (start < 0) return null;

            int? titleLevel = lines[start].Kind == LineKind.Heading ? lines[start].Level : null;
            int end = lines.Count;
            if (titleLevel is int lvl)
            {
                for (int i = start + 1; i < lines.Count; i++)
                {
                    if (lines[i].Kind == LineKind.Heading && lines[i].Level <= lvl) { end = i; break; }
                }
            }

            var section = lines.GetRange(start + 1, end - start - 1);

            // 2) Narrow to the new build's own block when the section lists several versions.
            if (!string.IsNullOrWhiteSpace(buildCode))
            {
                int codeAt = section.FindIndex(l => l.Text.Contains(buildCode, StringComparison.OrdinalIgnoreCase));
                if (codeAt >= 0)
                {
                    int codeEnd = section.Count;
                    for (int i = codeAt + 1; i < section.Count; i++)
                    {
                        var other = BuildCodePattern.Match(section[i].Text);
                        if (IsHeaderLike(section[i]) && other.Success &&
                            !other.Value.Equals(buildCode, StringComparison.OrdinalIgnoreCase))
                        {
                            codeEnd = i;
                            break;
                        }
                    }
                    section = section.GetRange(codeAt, codeEnd - codeAt);
                }
            }

            // 3) Collect what sits under "Added" and "Updates".
            var added = new List<string>();
            var updates = new List<string>();
            List<string>? current = null;
            bool addedDone = false, updatesDone = false;

            foreach (var line in section)
            {
                // A bullet that happens to start with "Update - ..." is content, not a header.
                var (label, inlineRest) = line.Kind == LineKind.ListItem ? ((string?)null, (string?)null) : SplitLabel(line.Text);

                if (label != null)
                {
                    bool isAdded = label == "added";
                    // Same header seen again after it already collected something = the next
                    // (older/other) version's block, so that header is finished.
                    if (isAdded && added.Count > 0) addedDone = true;
                    if (!isAdded && updates.Count > 0) updatesDone = true;

                    current = isAdded ? (addedDone ? null : added) : (updatesDone ? null : updates);
                    if (current != null && inlineRest != null) AddItem(current, inlineRest);
                    continue;
                }

                if (IsHeaderLike(line))
                {
                    // Any other header ("Fixed", "Known Issues", the next version...) ends the block.
                    current = null;
                    continue;
                }

                if (current != null) AddItem(current, line.Text);
            }

            var notes = new ReleaseNotes(added.Where(i => !IsNotApplicable(i)).ToList(),
                                         updates.Where(i => !IsNotApplicable(i)).ToList());
            return notes.IsEmpty ? null : notes;
        }

        // ---------------------------------------------------------------- helpers

        private static void AddItem(List<string> list, string text)
        {
            var t = text.Trim().TrimStart('•', '-', '*', '·').Trim();
            if (t.Length > 0) list.Add(t);
        }

        private static bool IsNotApplicable(string text)
        {
            var t = Regex.Replace(text.ToLowerInvariant(), @"[\s\.\-–—/]", string.Empty);
            return t is "na" or "none" or "" or "nothing";
        }

        private static bool IsSectionTitle(string text)
        {
            var t = Regex.Replace(text.ToLowerInvariant(), "[^a-z]", string.Empty);
            return t.Contains("buildrelease") && t.Contains("versionupdate");
        }

        // "Added", "Added:", "Updates -", "Added: new thing" -> ("added", "new thing")
        private static (string? label, string? rest) SplitLabel(string text)
        {
            var m = Regex.Match(text, @"^\s*(added|updates|updated|update)\s*(?:[:\-–—]\s*(.*))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return (null, null);
            var rest = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            return (m.Groups[1].Value.ToLowerInvariant(), string.IsNullOrEmpty(rest) ? null : rest);
        }

        private static bool IsHeaderLike(Line l) =>
            l.Kind is LineKind.Heading or LineKind.TableHeader ||
            (l.Kind == LineKind.Text && l.IsBold && l.Text.Length <= 80) ||
            (l.Kind == LineKind.Text && l.Text.Length <= 40 && l.Text.TrimEnd().EndsWith(':'));

        /// <summary>HTML -> one Line per block (heading, list item, table cell, paragraph).</summary>
        private static List<Line> Flatten(string html)
        {
            var s = html;
            s = Regex.Replace(s, @"<(script|style)\b[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<h([1-6])\b[^>]*>", m => $"\n{Marker}H{m.Groups[1].Value}{MarkerEnd}", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<li\b[^>]*>", $"\n{Marker}LI{MarkerEnd}", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<th\b[^>]*>", $"\n{Marker}TH{MarkerEnd}", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(strong|b)\b[^>]*>", BoldOpen.ToString(), RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</(strong|b)\s*>", BoldClose.ToString(), RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<br\s*/?>|</?(p|div|tr|td|th|li|ul|ol|table|tbody|thead|blockquote|section|h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", string.Empty);
            s = WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');

            var result = new List<Line>();
            // Confluence wraps bullet/cell/heading text in its own <p> or <span>, which leaves the
            // block marker on an empty line - carry it to the next line that has text.
            LineKind? pendingKind = null;
            int pendingLevel = 0;

            foreach (var raw in s.Split('\n'))
            {
                var text = raw;
                var kind = pendingKind ?? LineKind.Text;
                int level = pendingLevel;

                if (text.StartsWith(Marker))
                {
                    int close = text.IndexOf(MarkerEnd);
                    var tag = close > 0 ? text.Substring(1, close - 1) : string.Empty;
                    text = close > 0 ? text.Substring(close + 1) : text.Substring(1);
                    if (tag.StartsWith('H') && int.TryParse(tag.AsSpan(1), out level)) kind = LineKind.Heading;
                    else if (tag == "LI") kind = LineKind.ListItem;
                    else if (tag == "TH") kind = LineKind.TableHeader;
                }

                // Bold = every visible character sits between bold markers.
                var outsideBold = new StringBuilder();
                int depth = 0;
                bool anyBold = false;
                foreach (var c in text)
                {
                    if (c == BoldOpen) { depth++; anyBold = true; }
                    else if (c == BoldClose) { depth = Math.Max(0, depth - 1); }
                    else if (depth == 0) outsideBold.Append(c);
                }

                var clean = Regex.Replace(text, $"[{Marker}{MarkerEnd}{BoldOpen}{BoldClose}]", string.Empty);
                clean = Regex.Replace(clean, @"[ \t\r]+", " ").Trim();
                if (clean.Length == 0)
                {
                    if (kind != LineKind.Text) { pendingKind = kind; pendingLevel = level; }
                    continue;
                }
                pendingKind = null;
                pendingLevel = 0;

                bool isBold = anyBold && Regex.Replace(outsideBold.ToString(), @"[\s:\-–—]", string.Empty).Length == 0;
                result.Add(new Line(kind, level, clean, isBold));
            }

            return result;
        }
    }
}
