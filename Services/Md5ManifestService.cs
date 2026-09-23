using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Model;

namespace CommandCenter.Services
{
    /// <summary>
    /// Reads ExactFile-style .md5 manifests ("hash *relative\path") and verifies a build folder against one.
    ///
    /// Flow (per Don's requirement): copy the cached .md5 INTO the build folder, verify from there
    /// (paths in the manifest are relative to the build root), then delete the copy - always, even on
    /// failure or cancel. The copy itself is excluded from the Extras list.
    /// </summary>
    public static class Md5ManifestService
    {
        public const string ManifestFileName = "checksums.md5";

        private const int BufferSize = 1 << 20;             // 1 MB reads
        private const long ProgressChunkBytes = 64L << 20;  // mid-file progress every 64 MB (big .wz files)

        private static readonly Regex LinePattern =
            new(@"^([0-9a-fA-F]{32})\s+\*?(.+?)\s*$", RegexOptions.Compiled);

        public static List<Md5ManifestEntry> Parse(string manifestPath)
        {
            var entries = new List<Md5ManifestEntry>();

            foreach (var raw in File.ReadLines(manifestPath, Encoding.UTF8))
            {
                var line = raw.TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue; // ExactFile header lines

                var m = LinePattern.Match(line);
                if (!m.Success) continue;

                var relative = m.Groups[2].Value.Replace('/', '\\').TrimStart('\\');
                entries.Add(new Md5ManifestEntry(m.Groups[1].Value.ToLowerInvariant(), relative));
            }

            return entries;
        }

        /// <summary>
        /// Runs on a background thread. Cancellation returns a report with Cancelled = true and whatever
        /// results were collected, rather than throwing. Setup failures (build folder unwritable, empty manifest) throw.
        /// </summary>
        public static Task<ChecksumVerifyReport> VerifyAsync(
            string cachedManifestPath,
            string buildPath,
            IProgress<ChecksumVerifyProgress>? progress,
            CancellationToken cancellationToken)
            => Task.Run(() => Verify(cachedManifestPath, buildPath, progress, cancellationToken));

        private static ChecksumVerifyReport Verify(
            string cachedManifestPath,
            string buildPath,
            IProgress<ChecksumVerifyProgress>? progress,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var root = Path.GetFullPath(buildPath).TrimEnd('\\') + "\\";
            var copiedManifest = Path.Combine(root, ManifestFileName);

            var results = new List<ChecksumFileResult>();
            var extras = new List<string>();
            var cancelled = false;
            var manifestCount = 0;

            File.Copy(cachedManifestPath, copiedManifest, overwrite: true);

            try
            {
                // Verify from the copy that now sits inside the build folder.
                var entries = Parse(copiedManifest);
                manifestCount = entries.Count;
                if (entries.Count == 0)
                    throw new InvalidDataException("The .md5 file has no checksum entries.");

                long totalBytes = 0;
                foreach (var entry in entries)
                {
                    var path = Resolve(root, entry.RelativePath);
                    if (path == null) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists) totalBytes += info.Length;
                    }
                    catch
                    {
                        // unreadable metadata - it'll surface as an Error row during hashing
                    }
                }

                long doneBytes = 0;
                long lastReportedBytes = 0;
                var buffer = new byte[BufferSize];

                double Percent(int filesDone) =>
                    totalBytes > 0
                        ? Math.Min(100.0, doneBytes * 100.0 / totalBytes)
                        : filesDone * 100.0 / entries.Count;

                for (var i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var entry = entries[i];
                    var filesDoneSoFar = i;
                    var fullPath = Resolve(root, entry.RelativePath);
                    ChecksumFileResult result;

                    if (fullPath == null)
                    {
                        result = new ChecksumFileResult(entry.RelativePath, ChecksumOutcome.Error, entry.ExpectedHash, null,
                            "path points outside the build folder");
                    }
                    else if (!File.Exists(fullPath))
                    {
                        result = new ChecksumFileResult(entry.RelativePath, ChecksumOutcome.Missing, entry.ExpectedHash, null, null);
                    }
                    else
                    {
                        try
                        {
                            var actual = HashFile(fullPath, buffer, bytesRead =>
                            {
                                doneBytes += bytesRead;
                                if (doneBytes - lastReportedBytes >= ProgressChunkBytes)
                                {
                                    lastReportedBytes = doneBytes;
                                    progress?.Report(new ChecksumVerifyProgress(filesDoneSoFar, entries.Count, Percent(filesDoneSoFar), null));
                                }
                            }, ct);

                            result = actual == entry.ExpectedHash
                                ? new ChecksumFileResult(entry.RelativePath, ChecksumOutcome.Match, entry.ExpectedHash, actual, null)
                                : new ChecksumFileResult(entry.RelativePath, ChecksumOutcome.Mismatch, entry.ExpectedHash, actual, null);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result = new ChecksumFileResult(entry.RelativePath, ChecksumOutcome.Error, entry.ExpectedHash, null, ex.Message);
                        }
                    }

                    results.Add(result);
                    progress?.Report(new ChecksumVerifyProgress(i + 1, entries.Count, Percent(i + 1), FormatLogLine(result)));
                }

                extras = FindExtras(root, entries, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                try { File.Delete(copiedManifest); } catch { /* best effort - never block the report on cleanup */ }
            }

            return new ChecksumVerifyReport(results, extras, manifestCount, stopwatch.Elapsed, cancelled);
        }

        public static string FormatLogLine(ChecksumFileResult r) => r.Outcome switch
        {
            ChecksumOutcome.Match => $"OK        {r.RelativePath}",
            ChecksumOutcome.Mismatch => $"MISMATCH  {r.RelativePath}  expected {r.ExpectedHash}  got {r.ActualHash}",
            ChecksumOutcome.Missing => $"MISSING   {r.RelativePath}",
            _ => $"ERROR     {r.RelativePath}  ({r.Detail})"
        };

        /// <summary>Returns null if the manifest path would escape the build folder (e.g. "..\..\x").</summary>
        private static string? Resolve(string root, string relativePath)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, relativePath));
            }
            catch
            {
                return null;
            }

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static string HashFile(string path, byte[] buffer, Action<int> onRead, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.SequentialScan);

            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                onRead(read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>Files in the build folder the manifest doesn't list. Informational only - never a failure.</summary>
        private static List<string> FindExtras(string root, IEnumerable<Md5ManifestEntry> entries, CancellationToken ct)
        {
            var expected = new HashSet<string>(entries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
            var extras = new List<string>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file);
                if (relative.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue; // our own temporary copy
                if (!expected.Contains(relative)) extras.Add(relative);
            }

            extras.Sort(StringComparer.OrdinalIgnoreCase);
            return extras;
        }
    }
}
