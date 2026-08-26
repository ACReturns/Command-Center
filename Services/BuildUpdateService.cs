using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace CommandCenter.Services
{
    public enum UpdateMode
    {
        NewBuild,
        Patch
    }

    public record UpdateProgress(string Status, double PercentComplete);

    // Extracts a build/patch archive (.zip or .7z) and lays it into a build folder.
    // NewBuild wipes the destination first; Patch overlays onto it.
    //
    // Everything here (opening the archive, walking its entries, clearing/copying the build folder)
    // is disk work that can take anywhere from a second to several minutes depending on build size.
    // The whole pipeline runs inside a single Task.Run so it executes on a background thread-pool
    // thread from start to finish - the UI thread (and with it the window's own message pump, which
    // is what lets you drag/resize the window or click into another tab) is never blocked while an
    // extraction is in flight. IProgress<T> marshals status updates back to the UI thread on its
    // own, so callers just bind to it normally.
    public static class BuildUpdateService
    {
        private static readonly string[] SupportedExtensions = { ".zip", ".7z" };

        public static Task RunAsync(string sourceArchivePath, string destinationBuildPath, UpdateMode mode, IProgress<UpdateProgress> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceArchivePath) || !File.Exists(sourceArchivePath))
            {
                throw new FileNotFoundException("Select a valid build/patch archive (.zip or .7z) file first.", sourceArchivePath);
            }

            string extension = Path.GetExtension(sourceArchivePath);
            if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Unsupported archive type '{extension}'. Use a .zip or .7z file.");
            }

            if (string.IsNullOrWhiteSpace(destinationBuildPath))
            {
                throw new InvalidOperationException("No build path is configured for this section. Set it in the Settings tab first.");
            }

            // The checks above are instant and fine to run on the caller's thread. Everything past
            // this point touches disk and is handed off to the background.
            return RunOnBackgroundThreadAsync(sourceArchivePath, destinationBuildPath, mode, progress, cancellationToken);
        }

        private static async Task RunOnBackgroundThreadAsync(string sourceArchivePath, string destinationBuildPath, UpdateMode mode, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "CommandCenter_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                try
                {
                    progress.Report(new UpdateProgress("Extracting archive...", 5));
                    await ExtractWithProgressAsync(sourceArchivePath, tempDir, progress, cancellationToken).ConfigureAwait(false);

                    string contentRoot = FindContentRoot(tempDir);

                    Directory.CreateDirectory(destinationBuildPath);

                    if (mode == UpdateMode.NewBuild)
                    {
                        progress.Report(new UpdateProgress("Removing previous build...", 52));
                        ClearDirectory(destinationBuildPath, cancellationToken);
                    }

                    CopyDirectoryWithProgress(contentRoot, destinationBuildPath, progress, cancellationToken);

                    progress.Report(new UpdateProgress("Done", 100));
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Collapses redundant single-folder wrappers (e.g. Patch.zip -> Patch\build\... -> the real
        // content) so an extra nesting level a developer accidentally adds to an archive never breaks
        // the transfer - it keeps drilling down until it finds a folder with more than one item in it.
        private static string FindContentRoot(string dir)
        {
            string current = dir;

            while (true)
            {
                var files = Directory.GetFiles(current);
                var dirs = Directory.GetDirectories(current);

                if (files.Length == 0 && dirs.Length == 1)
                {
                    current = dirs[0];
                    continue;
                }

                break;
            }

            return current;
        }

        // Opens the archive via SharpCompress, which auto-detects the format (zip or 7z) from the
        // file's contents, so both extensions extract through this same path. Runs on the
        // background thread Task.Run started above, so the header/central-directory read and the
        // per-entry decompression never touch the UI thread.
        private static async Task ExtractWithProgressAsync(string archivePath, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
            int total = entries.Count == 0 ? 1 : entries.Count;
            int done = 0;
            string destRoot = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destPath = Path.GetFullPath(Path.Combine(destinationDir, entry.Key!));
                if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Archive contains an entry outside the extraction folder.");
                }

                string? entryDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(entryDir))
                {
                    Directory.CreateDirectory(entryDir);
                }

                using (var entryStream = entry.OpenEntryStream())
                using (var fileStream = File.Create(destPath))
                {
                    await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                done++;
                double pct = 5 + (done / (double)total) * 45; // extraction spans 5-50%
                progress.Report(new UpdateProgress($"Extracting ({done}/{total})...", pct));
            }
        }

        // Synchronous on purpose: this already runs on the background thread Task.Run started
        // above, so there's no UI thread left to protect and no reason to pay for another hop
        // through the thread pool for every single file.
        private static void CopyDirectoryWithProgress(string sourceDir, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            int total = allFiles.Length == 0 ? 1 : allFiles.Length;
            int done = 0;

            foreach (var filePath in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(sourceDir, filePath);
                string destPath = Path.Combine(destinationDir, relative);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(filePath, destPath, overwrite: true);

                done++;
                double pct = 55 + (done / (double)total) * 45; // copy spans 55-100%
                progress.Report(new UpdateProgress($"Copying build files ({done}/{total})...", pct));
            }
        }

        private static void ClearDirectory(string dir, CancellationToken cancellationToken)
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(subDir, recursive: true);
            }
        }
    }
}
