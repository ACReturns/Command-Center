using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CommandCenter.Services
{
    // Moves the entire contents of a source folder into a Live Service build's Current Build
    // directory, clearing the destination first. The source is either a manually browsed folder
    // ("Folder to Push to Live") or another build-section tab's own Current Build folder, picked
    // via one of the PushTargets radio options - either way this service just sees a plain source
    // path and doesn't care which. Backs the "Pushed to Live" mode, which is only offered on Live
    // Service sections (permanent and extra - see BuildSectionViewModel.SupportsPushedToLive).
    //
    // Runs on a background thread throughout, same pattern as BuildUpdateService, so the window
    // and every other tab stay fully usable while a push is in flight. Reuses UpdateProgress so
    // BuildSectionViewModel can drive the same progress bar/status wiring for both operations.
    public static class PushToLiveService
    {
        public static Task RunAsync(string sourceFolderPath, string destinationBuildPath, IProgress<UpdateProgress> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFolderPath) || !Directory.Exists(sourceFolderPath))
            {
                throw new DirectoryNotFoundException("Select a valid folder to push to Live first.");
            }

            if (string.IsNullOrWhiteSpace(destinationBuildPath))
            {
                throw new InvalidOperationException("No build path is configured for this section. Set it in the Settings tab first.");
            }

            string fullSource = Path.GetFullPath(sourceFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDest = Path.GetFullPath(destinationBuildPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected folder is already the Live build folder.");
            }

            if (IsSubPathOf(fullDest, fullSource))
            {
                throw new InvalidOperationException("The Live build folder is inside the selected source folder. Choose a different source folder.");
            }

            if (IsSubPathOf(fullSource, fullDest))
            {
                throw new InvalidOperationException("The selected folder is inside the Live build folder, so clearing the destination first would delete the source. Choose a different source folder.");
            }

            return RunOnBackgroundThreadAsync(fullSource, fullDest, progress, cancellationToken);
        }

        // True if `path` is inside `potentialParent`.
        private static bool IsSubPathOf(string path, string potentialParent)
        {
            string parentWithSep = potentialParent + Path.DirectorySeparatorChar;
            return path.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task RunOnBackgroundThreadAsync(string sourceDir, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(destinationDir);

                progress.Report(new UpdateProgress("Clearing Live build folder...", 5));
                ClearDirectory(destinationDir, cancellationToken);

                progress.Report(new UpdateProgress("Moving files to Live...", 10));
                MoveContentsWithProgress(sourceDir, destinationDir, progress, cancellationToken);

                progress.Report(new UpdateProgress("Cleaning up source folder...", 95));
                RemoveEmptySubdirectories(sourceDir);

                progress.Report(new UpdateProgress("Done", 100));
            }, cancellationToken).ConfigureAwait(false);
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

        private static void MoveContentsWithProgress(string sourceDir, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
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

                // File.Move handles cross-volume moves internally (copy + delete fallback), so this
                // works whether the source folder and the Live build drive are the same drive or not.
                File.Move(filePath, destPath, overwrite: true);

                done++;
                double pct = 10 + (done / (double)total) * 85; // move spans 10-95%
                progress.Report(new UpdateProgress($"Moving files ({done}/{total})...", pct));
            }
        }

        // After every file has been moved out, the source folder is left with an empty tree of
        // subdirectories. Walk it bottom-up and remove them, but keep the top-level source folder
        // itself so the user still has the folder they picked (now empty) afterward.
        private static void RemoveEmptySubdirectories(string rootDir)
        {
            foreach (var dir in Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch
                {
                    // Best-effort cleanup - a locked or non-empty-for-some-reason folder just stays.
                }
            }
        }
    }
}
