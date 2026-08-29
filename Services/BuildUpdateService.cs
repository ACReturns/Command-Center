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
                    else if (!IsPartialClientArchive(sourceArchivePath))
                    {
                        // Patch never touches the destination build folder up front - it only ever
                        // overwrites matching filenames via the copy below. Some patch archives wrap
                        // the real payload in a nested Partial_Client.zip (or ship it as an already-
                        // extracted "Partial"/"Partial Client" folder) alongside unrelated extras like
                        // a checksums.md5 - resolve down to just that payload before copying. Skipped
                        // when the file the user selected already IS Partial_Client.zip/.7z (see
                        // IsPartialClientArchive) - its extracted, already-flattened content is the
                        // payload as-is, so hunting it for another nested Partial_Client would be wrong.
                        contentRoot = await ResolvePartialClientPayloadAsync(tempDir, contentRoot, progress, cancellationToken).ConfigureAwait(false);
                    }

                    CopyDirectoryWithProgress(contentRoot, destinationBuildPath, progress, cancellationToken);

                    progress.Report(new UpdateProgress("Finalizing build folder...", 97));
                    FlattenKnownWrapperFolders(destinationBuildPath, cancellationToken);

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
        // per-entry decompression never touch the UI thread. Maps progress onto the 5-50% band used
        // for the main archive; ResolvePartialClientPayloadAsync below calls the shared
        // ExtractArchiveAsync directly with its own narrower band for a nested Partial_Client archive.
        private static Task ExtractWithProgressAsync(string archivePath, string destinationDir, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            return ExtractArchiveAsync(archivePath, destinationDir, cancellationToken, (done, total) =>
            {
                double pct = 5 + (done / (double)total) * 45; // extraction spans 5-50%
                progress.Report(new UpdateProgress($"Extracting ({done}/{total})...", pct));
            });
        }

        private static async Task ExtractArchiveAsync(string archivePath, string destinationDir, CancellationToken cancellationToken, Action<int, int> onEntryExtracted)
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
                onEntryExtracted(done, total);
            }
        }

        // Names to look for at the top of a patch archive's extracted content (after the same
        // FindContentRoot collapsing a New Build gets). The zip/7z case is matched by base name
        // against every SupportedExtensions entry, so a "Partial_Client.7z" is honored too, the same
        // way the rest of this service treats both archive formats interchangeably. The folder case
        // covers the handful of separator/casing variants a build folder might reasonably use.
        private const string PartialClientBaseName = "Partial_Client";
        private static readonly string[] PartialFolderNames = { "Partial", "Partial Client", "PartialClient", "Partial_Client" };

        // True when the archive the user picked in the Patch tab is itself named Partial_Client
        // (.zip or .7z) - i.e. they already browsed straight to the payload rather than to an outer
        // patch archive that merely contains one. Same base-name-against-SupportedExtensions match
        // ResolvePartialClientPayloadAsync uses below, just applied to the top-level selection.
        private static bool IsPartialClientArchive(string archivePath) =>
            string.Equals(Path.GetFileNameWithoutExtension(archivePath), PartialClientBaseName, StringComparison.OrdinalIgnoreCase) &&
            SupportedExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase);

        // Some patch archives wrap the real payload in a nested Partial_Client archive (or ship it as
        // an already-extracted "Partial"/"Partial Client" folder) sitting alongside unrelated extras
        // such as a checksums.md5 file. When either is present at the top of the extracted patch,
        // it - and only it - is the actual patch content: everything else at that level is discarded,
        // the archive (if that's what was found) is extracted, and the result is flattened the same
        // way FindContentRoot flattens a New Build archive. This is only reached when the file the
        // user selected wasn't itself named Partial_Client (see IsPartialClientArchive), so if
        // neither is present here either, there's nothing to apply - abort rather than silently
        // patching with whatever else the archive happened to contain.
        private static async Task<string> ResolvePartialClientPayloadAsync(string tempDir, string contentRoot, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            string? partialArchivePath = Directory.GetFiles(contentRoot)
                .FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), PartialClientBaseName, StringComparison.OrdinalIgnoreCase) &&
                    SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            string? partialFolderPath = partialArchivePath == null
                ? Directory.GetDirectories(contentRoot)
                    .FirstOrDefault(d => PartialFolderNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                : null;

            if (partialArchivePath == null && partialFolderPath == null)
            {
                // Caught by BuildSectionViewModel.RunUpdateAsync's catch (Exception), which sets
                // StatusText to "Update failed: <message>" and shows it in the error MessageBox too -
                // same as every other abort condition in this service (missing archive, no build
                // path, etc.), so this doesn't need its own special-cased handling on the ViewModel side.
                throw new InvalidOperationException(
                    $"Couldn't find a {PartialClientBaseName}.zip/.7z file or a Partial/\"Partial Client\" folder in the extracted patch archive. Patch aborted.");
            }

            // Discard everything else extracted alongside the payload (checksums.md5, etc.) - only
            // the Partial_Client archive or folder itself survives.
            foreach (var file in Directory.GetFiles(contentRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file == partialArchivePath)
                {
                    continue;
                }

                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (var dir in Directory.GetDirectories(contentRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dir == partialFolderPath)
                {
                    continue;
                }

                Directory.Delete(dir, recursive: true);
            }

            if (partialFolderPath != null)
            {
                return FindContentRoot(partialFolderPath);
            }

            progress.Report(new UpdateProgress("Extracting Partial_Client archive...", 51));

            // Extracted inside tempDir (rather than a sibling temp folder) so it's cleaned up by the
            // same best-effort Directory.Delete(tempDir, ...) in the outer finally block regardless
            // of how the rest of the update turns out.
            string partialExtractDir = Path.Combine(tempDir, "_PartialClientExtract_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(partialExtractDir);

            await ExtractArchiveAsync(partialArchivePath!, partialExtractDir, cancellationToken, (done, total) =>
            {
                double pct = 50 + (done / (double)total) * 4; // nested extraction spans 50-54%
                progress.Report(new UpdateProgress($"Extracting Partial_Client archive ({done}/{total})...", pct));
            }).ConfigureAwait(false);

            return FindContentRoot(partialExtractDir);
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

        // Some build archives ship "AdminClient" and "Bin" as wrapper folders one level down instead
        // of laying their contents flat at the top level. A build sitting behind those wrappers isn't
        // launchable as-is, so after the copy above lands them under destinationBuildPath, pull each
        // wrapper's contents up into destinationBuildPath itself and remove the now-empty wrapper.
        // Runs unconditionally (both NewBuild and Patch) since either mode can extract an archive with
        // this layout, and it's a no-op when a wrapper folder isn't present.
        private static readonly string[] WrapperFoldersToFlatten = { "AdminClient", "Bin" };

        private static void FlattenKnownWrapperFolders(string destinationBuildPath, CancellationToken cancellationToken)
        {
            foreach (var wrapperName in WrapperFoldersToFlatten)
            {
                string wrapperPath = Path.Combine(destinationBuildPath, wrapperName);
                if (!Directory.Exists(wrapperPath))
                {
                    continue;
                }

                MoveDirectoryContents(wrapperPath, destinationBuildPath, cancellationToken);

                try
                {
                    Directory.Delete(wrapperPath, recursive: true);
                }
                catch
                {
                    // Best-effort: if something (AV scanner, an open handle) is still holding the now-
                    // empty wrapper folder, leave it rather than fail the whole update over it.
                }
            }
        }

        // Moves every file and subfolder out of sourceDir and into destinationDir (both already on
        // the same volume, since sourceDir is itself a subfolder of destinationDir at this point, so
        // Directory.Move/File.Move are cheap renames rather than copies). If destinationDir already
        // has an item with the same name - e.g. the build itself also has a top-level "Bin" folder in
        // addition to the wrapper - merges into it recursively instead of overwriting it outright.
        private static void MoveDirectoryContents(string sourceDir, string destinationDir, CancellationToken cancellationToken)
        {
            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destPath = Path.Combine(destinationDir, Path.GetFileName(filePath));
                if (File.Exists(destPath))
                {
                    File.SetAttributes(destPath, FileAttributes.Normal);
                    File.Delete(destPath);
                }

                File.Move(filePath, destPath);
            }

            foreach (var subDirPath in Directory.GetDirectories(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destSubDirPath = Path.Combine(destinationDir, Path.GetFileName(subDirPath));
                if (!Directory.Exists(destSubDirPath))
                {
                    Directory.Move(subDirPath, destSubDirPath);
                }
                else
                {
                    MoveDirectoryContents(subDirPath, destSubDirPath, cancellationToken);
                    Directory.Delete(subDirPath, recursive: true);
                }
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
