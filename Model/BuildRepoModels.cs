using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CommandCenter.Model
{
    /// <summary>Defaults, URL parsing and archive-ordering rules for the Build Repo tab.</summary>
    public static class BuildRepoDefaults
    {
        /// <summary>Root Drive folder: one subfolder per version (v271.4.0, ...), each holding a "Build" folder.</summary>
        public const string RootFolderUrl = "https://drive.google.com/drive/folders/1MWzzWAd42FLqb8pJQkekKgUakoQrysaO";

        /// <summary>Head node shown at the top of the tree.</summary>
        public const string RootTitle = "MapleStory (GMS)";

        /// <summary>The subfolder inside each version folder whose children are the builds (1TC, 2TC, v271.4.0 (Minor Patch), ...).</summary>
        public const string BuildFolderName = "Build";

        /// <summary>
        /// Name of the staging folder used when "Extract after download" is on. It sits NEXT TO the
        /// target tab's build folder (same drive, so no cross-drive copy), never inside it - a Full_*.zip
        /// runs as a New Build, which wipes the build folder first and would delete the downloads.
        /// </summary>
        public const string StagingFolderName = "_BuildRepoDownloads";

        private static readonly string[] ArchiveExtensions = { ".zip", ".7z" };

        // drive.google.com/drive/folders/<id>, /drive/u/0/folders/<id>, or ?id=<id>
        private static readonly Regex FolderIdPattern = new(@"/folders/([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);
        private static readonly Regex QueryIdPattern = new(@"[?&]id=([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);
        private static readonly Regex BareIdPattern = new(@"^[A-Za-z0-9_-]{10,}$", RegexOptions.Compiled);

        public static bool TryParseFolderId(string? url, out string folderId)
        {
            folderId = string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;
            var text = url.Trim();

            var m = FolderIdPattern.Match(text);
            if (!m.Success) m = QueryIdPattern.Match(text);
            if (m.Success)
            {
                folderId = m.Groups[1].Value;
                return true;
            }

            if (BareIdPattern.IsMatch(text))
            {
                folderId = text;
                return true;
            }

            return false;
        }

        public static bool IsArchive(string path) =>
            ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static bool IsFullArchive(string path) =>
            IsArchive(path) && Path.GetFileName(path).StartsWith("Full_", StringComparison.OrdinalIgnoreCase);

        public static bool IsPartialArchive(string path) =>
            IsArchive(path) && Path.GetFileName(path).StartsWith("Partial_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Version Number written onto the target tab after a successful extract: the dotted number in
        /// the version folder's name without the leading "v" ("v271.4.0" -> "271.4.0"), matching the
        /// format the Maintenance tab uses (ChecksumSheetParser.NormalizeVersion). Falls back to the
        /// trimmed folder name if it has no number in it at all.
        /// </summary>
        public static string VersionNumberFromFolderName(string folderName)
        {
            // Prefer a dotted version ("v271.4.0" -> "271.4.0"), else any number ("272 Files" -> "272").
            var m = Regex.Match(folderName, @"\d+(\.\d+)+");
            if (!m.Success) m = Regex.Match(folderName, @"\d+");
            return m.Success ? m.Value : folderName.Trim().TrimStart('v', 'V').Trim();
        }

        /// <summary>
        /// Version Number stamped on the target tab after a Build Repo extract - stores BOTH the
        /// version and the build it came from, as "&lt;version&gt; (&lt;build&gt;)":
        ///   version folder "272 Files", build "1TC"                  -> "272 (1TC)"
        ///   version folder "v271.4.0", build "1TC"                   -> "271.4.0 (1TC)"
        ///   build "v271.4.0 (Minor Patch)" (already has a version)   -> "271.4.0 (Minor Patch)"
        /// The version part comes first so Maintenance Verification still matches it - its
        /// NormalizeVersion ignores a trailing " (...)" label.
        /// </summary>
        public static string? VersionLabel(string? versionFolderName, string? buildName)
        {
            var build = buildName?.Trim();
            var version = string.IsNullOrWhiteSpace(versionFolderName) ? null : VersionNumberFromFolderName(versionFolderName);

            if (!string.IsNullOrEmpty(build))
            {
                // Build name already carries a dotted version - it's the more specific one, use it as-is.
                if (Regex.IsMatch(build, @"\d+(\.\d+)+"))
                {
                    return build.TrimStart('v', 'V').Trim();
                }

                return string.IsNullOrEmpty(version) ? build : $"{version} ({build})";
            }

            return string.IsNullOrEmpty(version) ? null : version;
        }

        /// <summary>"v271.4.0" -> [271, 4, 0]; used to sort version folders newest-first.</summary>
        public static int[] VersionKey(string name)
        {
            var m = Regex.Match(name, @"\d+(\.\d+)*");
            return m.Success
                ? m.Value.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray()
                : Array.Empty<int>();
        }

        public static int CompareVersionKeys(int[] a, int[] b)
        {
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : -1;
                int y = i < b.Length ? b[i] : -1;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        public static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
            return safe.Length == 0 ? "download" : safe;
        }
    }

    /// <summary>Compares strings so embedded numbers sort numerically ("2TC" before "10TC").</summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new();

        private static readonly Regex Chunk = new(@"\d+|\D+", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var a = Chunk.Matches(x);
            var b = Chunk.Matches(y);
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var ca = a[i].Value;
                var cb = b[i].Value;
                int result;
                if (char.IsDigit(ca[0]) && char.IsDigit(cb[0])
                    && long.TryParse(ca, out var na) && long.TryParse(cb, out var nb))
                {
                    result = na.CompareTo(nb);
                }
                else
                {
                    result = string.Compare(ca, cb, StringComparison.OrdinalIgnoreCase);
                }

                if (result != 0) return result;
            }

            return a.Count.CompareTo(b.Count);
        }
    }
}
