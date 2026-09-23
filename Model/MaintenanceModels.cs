using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CommandCenter.Model
{
    /// <summary>Defaults + on-disk locations for the Maintenance Version Verification tab.</summary>
    public static class MaintenanceDefaults
    {
        public const string SheetUrl =
            "https://docs.google.com/spreadsheets/d/1WUOe5bcadT2BDAJLrfANcig5dHqMMpN_XiDWecncmug/edit?gid=0#gid=0";

        /// <summary>%AppData%\CommandCenter - same root the Debug Command List already uses.</summary>
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommandCenter");

        /// <summary>%AppData%\CommandCenter\Checksums - every downloaded .md5 lives here, one subfolder per Tag.</summary>
        public static string ChecksumsFolder => Path.Combine(Root, "Checksums");

        /// <summary>Last successful pull of the sheet, so the table shows instantly on launch.</summary>
        public static string SheetCacheFile => Path.Combine(ChecksumsFolder, "sheet_cache.csv");

        /// <summary>WebView2 profile (cookies = the saved Google sign-in). LocalAppData, per WebView2 guidance.</summary>
        public static string WebViewDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandCenter", "WebView2");

        /// <summary>
        /// Every Drive .md5 is named checksums.md5, so each Tag still gets its own subfolder (v271.2.0
        /// can appear twice under different tags - see ChecksumSheetRow) - but the file itself is named
        /// after its Version (e.g. "checksum (271.3.0).md5") so the Checksums folder is easy to browse
        /// and catalog by eye without having to open every tag subfolder.
        /// </summary>
        public static string ManifestPathFor(string tag, string version)
        {
            var safeTag = MakeSafeFileNamePart(tag, "untagged");
            var safeVersion = MakeSafeFileNamePart(version, "unversioned");
            return Path.Combine(ChecksumsFolder, safeTag, $"checksum ({safeVersion}).md5");
        }

        private static string MakeSafeFileNamePart(string value, string fallback)
        {
            var safe = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            return safe.Length == 0 ? fallback : safe;
        }
    }

    /// <summary>
    /// One real version row from the sheet. Family header rows (v268), the banner row and
    /// orphan links with no Ver/Tag are filtered out by ChecksumSheetParser before this exists.
    /// </summary>
    public sealed record ChecksumSheetRow(
        string Version,        // as written in the sheet, e.g. "v271.3.0"
        string Tag,            // internal upload tag, e.g. "pub271_3_0_491dc31bef7ca4b30a513f50b0d7fcf2"
        string? ChecksumUrl,   // raw text of the Checksum column
        string? DriveFolderId, // normal case: link is a Drive folder containing checksums.md5
        string? DriveFileId,   // future-proofing: link points straight at a file
        int SheetRowNumber)
    {
        public bool HasChecksumLink => DriveFolderId != null || DriveFileId != null;

        /// <summary>"v271" from "v271.3.0" - mirrors the sheet's blue family header rows.</summary>
        public string Family
        {
            get
            {
                var dot = Version.IndexOf('.');
                return dot > 0 ? Version.Substring(0, dot) : Version;
            }
        }
    }

    public sealed record Md5ManifestEntry(string ExpectedHash, string RelativePath);

    /// <summary>Declaration order doubles as sort priority in the results table (problems first).</summary>
    public enum ChecksumOutcome { Mismatch, Missing, Error, Match }

    public sealed record ChecksumFileResult(
        string RelativePath,
        ChecksumOutcome Outcome,
        string ExpectedHash,
        string? ActualHash,
        string? Detail)
    {
        public string Note => Outcome switch
        {
            ChecksumOutcome.Mismatch => $"got {ActualHash}",
            ChecksumOutcome.Missing => "not found in build folder",
            ChecksumOutcome.Error => Detail ?? "could not be read",
            _ => string.Empty
        };
    }

    public sealed record ChecksumVerifyProgress(int FilesDone, int FilesTotal, double Percent, string? LogLine);

    public sealed class ChecksumVerifyReport
    {
        public ChecksumVerifyReport(
            IReadOnlyList<ChecksumFileResult> results,
            IReadOnlyList<string> extras,
            int manifestCount,
            TimeSpan elapsed,
            bool cancelled)
        {
            Results = results;
            Extras = extras;
            ManifestCount = manifestCount;
            Elapsed = elapsed;
            Cancelled = cancelled;
        }

        public IReadOnlyList<ChecksumFileResult> Results { get; }
        public IReadOnlyList<string> Extras { get; }
        public int ManifestCount { get; }
        public TimeSpan Elapsed { get; }
        public bool Cancelled { get; }

        public int MatchCount => Results.Count(r => r.Outcome == ChecksumOutcome.Match);
        public int MismatchCount => Results.Count(r => r.Outcome == ChecksumOutcome.Mismatch);
        public int MissingCount => Results.Count(r => r.Outcome == ChecksumOutcome.Missing);
        public int ErrorCount => Results.Count(r => r.Outcome == ChecksumOutcome.Error);

        /// <summary>Extras never fail a run - they're informational only.</summary>
        public bool Passed =>
            !Cancelled && MismatchCount == 0 && MissingCount == 0 && ErrorCount == 0 && Results.Count == ManifestCount;
    }
}
