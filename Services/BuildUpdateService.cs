using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace CommandCenter.Services
{
    public enum UpdateMode
    {
        NewBuild,
        Patch
    }

    public record UpdateProgress(string Status, double PercentComplete);

    // Extracts a build/patch zip and lays it into a build folder.
    // NewBuild wipes the destination first; Patch overlays onto it.
    public static class BuildUpdateService
    {
        public static async Task RunAsync(string sourceZipPath, string destinationBuildPath, UpdateMode mode, IProgress<UpdateProgress> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
            {
                throw new FileNotFoundException("Select a valid build/patch zip file first.", sourceZipPath);
            }

            if (string.IsNullOrWhiteSpace(destinationBuildPath))
            {
                throw new InvalidOperationException("No build path is configured for this section. Set it in the Settings tab first.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "CommandCenter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                progress.Report(new UpdateProgress("Extracting archive...", 5));
                await ExtractWithProgressAsync(sourceZipPath, tempDir, progress, cancellationToken);

                string contentRoot = FindContentRoot(tempDir);

                Directory.CreateDirectory(destinationBuildPath);

                if (mode == UpdateMode.NewBuild)
                {
                    progress.Report(new UpdateProgress("Removing previous build...", 52));
                    ClearDirectory(destinationBuildPath);
                }

                await CopyDirectoryWithProgressAsync(contentRoot, destinationBuildPath, progress, cancellationToken);

                progress.Report(new UpdateProgress("Done", 100));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
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

        private static async Task ExtractWithProgressAsync(string zipPath, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries;
            int total = entries.Count == 0 ? 1 : entries.Count;
            int done = 0;
            string destRoot = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
                if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Archive contains an entry outside the extraction folder.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    string? entryDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(entryDir))
                    {
                        Directory.CreateDirectory(entryDir);
                    }

                    await Task.Run(() => entry.ExtractToFile(destPath, overwrite: true), cancellationToken);
                }

                done++;
                double pct = 5 + (done / (double)total) * 45; // extraction spans 5-50%
                progress.Report(new UpdateProgress($"Extracting ({done}/{total})...", pct));
            }
        }

        private static async Task CopyDirectoryWithProgressAsync(string sourceDir, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
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

                await Task.Run(() => File.Copy(filePath, destPath, overwrite: true), cancellationToken);

                done++;
                double pct = 55 + (done / (double)total) * 45; // copy spans 55-100%
                progress.Report(new UpdateProgress($"Copying build files ({done}/{total})...", pct));
            }
        }

        private static void ClearDirectory(string dir)
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                Directory.Delete(subDir, recursive: true);
            }
        }
    }
}
