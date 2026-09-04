using System;
using System.Collections.Generic;
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

        // Matches a bare PartialFolderNames entry (increment 0), or that same name followed by an
        // underscore and a run of digits - e.g. "Partial Client_1", "Partial Client_12" - so a build
        // that ships its partial-client payload split across several numbered folders is recognized
        // the same as a single unnumbered one. Increment is parsed out so ResolvePartialClientPayloadAsync
        // below can merge multiple matches back together in the right order.
        private static bool TryGetPartialFolderIncrement(string folderName, out int increment)
        {
            foreach (var baseName in PartialFolderNames)
            {
                if (string.Equals(folderName, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    increment = 0;
                    return true;
                }

                string prefix = baseName + "_";
                if (folderName.Length > prefix.Length &&
                    folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(folderName.AsSpan(prefix.Length), out int parsed))
                {
                    increment = parsed;
                    return true;
                }
            }

            increment = 0;
            return false;
        }

        // Some patch archives wrap the real payload in a nested Partial_Client archive (or ship it as
        // one or more already-extracted "Partial"/"Partial Client" folders - possibly numbered, e.g.
        // "Partial Client_1", "Partial Client_2", when a build's partial-client payload is split
        // across several drops) sitting alongside unrelated extras such as a checksums.md5 file. When
        // either is present at the top of the extracted patch, it - and only it - is the actual patch
        // content: everything else at that level is discarded, the archive (if that's what was found)
        // is extracted, and the result is flattened the same way FindContentRoot flattens a New Build
        // archive. This is only reached when the file the user selected wasn't itself named
        // Partial_Client (see IsPartialClientArchive), so if nothing is found here either, there's
        // nothing to apply - abort rather than silently patching with whatever else the archive
        // happened to contain.
        // A matched Partial/"Partial Client" folder can itself carry a nested archive (most often
        // seen as a Partial_Client.zip/.7z sitting right next to other loose files inside a numbered
        // variant like "Partial Client_2") instead of laying its payload out as plain files. Left
        // alone, that raw archive would just get copied into the build folder unopened. Extract every
        // archive found directly inside folderPath straight into folderPath itself - using the same
        // shared entry-extraction the rest of this service already uses - then delete the archive so
        // it isn't copied in as a stray zip. Extracting in place overlays onto whatever loose files
        // already sit there (File.Create truncates on a name collision), the same "patching" overlay
        // semantics used everywhere else in this service - nothing here deletes first.
        private static async Task ExtractNestedArchivesAsync(string folderPath, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            var nestedArchives = Directory.GetFiles(folderPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var archivePath in nestedArchives)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string archiveName = Path.GetFileName(archivePath);
                progress.Report(new UpdateProgress($"Extracting {archiveName}...", 52));

                await ExtractArchiveAsync(archivePath, folderPath, cancellationToken, (done, total) =>
                {
                    progress.Report(new UpdateProgress($"Extracting {archiveName} ({done}/{total})...", 52));
                }).ConfigureAwait(false);

                File.SetAttributes(archivePath, FileAttributes.Normal);
                File.Delete(archivePath);
            }
        }

        private static async Task<string> ResolvePartialClientPayloadAsync(string tempDir, string contentRoot, IProgress<UpdateProgress> progress, CancellationToken cancellationToken, bool allowGenericWrapperFallback = true)
        {
            string? partialArchivePath = Directory.GetFiles(contentRoot)
                .FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), PartialClientBaseName, StringComparison.OrdinalIgnoreCase) &&
                    SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            // Every top-level folder matching a known base name or one of its numbered variants -
            // skipped entirely when a Partial_Client archive was already found above, same as before.
            // Ordered by increment (bare name = 0) so a multi-folder merge below applies them in the
            // right order.
            var partialFolderMatches = new List<(string Path, int Increment)>();
            if (partialArchivePath == null)
            {
                foreach (var dir in Directory.GetDirectories(contentRoot))
                {
                    if (TryGetPartialFolderIncrement(Path.GetFileName(dir), out int increment))
                    {
                        partialFolderMatches.Add((dir, increment));
                    }
                }

                partialFolderMatches.Sort((a, b) => a.Increment.CompareTo(b.Increment));
            }

            List<string> partialFolderPaths = partialFolderMatches.Select(m => m.Path).ToList();

            if (partialArchivePath == null && partialFolderPaths.Count == 0)
            {
                // Nothing named Partial_Client here. Two different situations land here, and only
                // one of them should abort:
                //  - The common case: this is simply a plain, unwrapped patch (e.g. a bare "Bin"
                //    folder with nothing else alongside it, or loose files) that was never going to
                //    have a Partial_Client marker in the first place - there's exactly one sensible
                //    payload (contentRoot itself) and nothing to disambiguate, so just use it as-is.
                //    FlattenKnownWrapperFolders still handles AdminClient/Bin once this is copied
                //    into destinationBuildPath, same as it does for every other patch layout - it
                //    doesn't need contentRoot to still have a literal "Bin" subfolder, since
                //    FindContentRoot's own single-wrapper collapse (see above) already flattens a
                //    bare-Bin-only archive down to Bin's own contents before this method even runs.
                //  - The genuinely ambiguous case: more than one archive sits at this level and none
                //    of them is named Partial_Client, so there's no way to tell which (if any) is the
                //    real payload - only this case aborts.
                // A single unnamed archive is a third possibility - not ambiguous, just not yet
                // opened - so it's unwrapped and re-checked one level in first via a recursive call
                // with allowGenericWrapperFallback:false, so a second unnamed wrapper found there
                // doesn't chain forever (matching the one-level-deep scope
                // ExtractNestedArchivesAsync's own nesting handling uses); if that inner level is
                // itself ambiguous or empty, the same rules above apply to it.
                var otherArchives = Directory.GetFiles(contentRoot)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (allowGenericWrapperFallback && otherArchives.Count == 1)
                {
                    string wrapperArchivePath = otherArchives[0];
                    string wrapperArchiveName = Path.GetFileName(wrapperArchivePath);
                    progress.Report(new UpdateProgress($"Extracting {wrapperArchiveName}...", 51));

                    string wrapperExtractDir = Path.Combine(tempDir, "_PatchWrapperExtract_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(wrapperExtractDir);

                    await ExtractArchiveAsync(wrapperArchivePath, wrapperExtractDir, cancellationToken, (done, total) =>
                    {
                        double pct = 50 + (done / (double)total) * 4; // nested extraction spans 50-54%
                        progress.Report(new UpdateProgress($"Extracting {wrapperArchiveName} ({done}/{total})...", pct));
                    }).ConfigureAwait(false);

                    string wrapperContentRoot = FindContentRoot(wrapperExtractDir);
                    await ExtractNestedArchivesAsync(wrapperContentRoot, progress, cancellationToken).ConfigureAwait(false);

                    return await ResolvePartialClientPayloadAsync(tempDir, wrapperContentRoot, progress, cancellationToken, allowGenericWrapperFallback: false).ConfigureAwait(false);
                }

                if (otherArchives.Count > 1)
                {
                    // Caught by BuildSectionViewModel.RunUpdateAsync's catch (Exception), which sets
                    // StatusText to "Update failed: <message>" and shows it in the error MessageBox
                    // too - same as every other abort condition in this service (missing archive, no
                    // build path, etc.), so this doesn't need its own special-cased handling on the
                    // ViewModel side.
                    throw new InvalidOperationException(
                        $"Found multiple archives ({string.Join(", ", otherArchives.Select(Path.GetFileName))}) in the extracted patch with no {PartialClientBaseName}.zip/.7z or Partial/\"Partial Client\" folder to identify which one is the payload. Patch aborted.");
                }

                // Zero archives (or exactly one, already unwrapped above with nothing Partial_Client-
                // named found one level in either) - a plain, unambiguous patch. Use contentRoot as-is.
                return contentRoot;
            }

            // Discard everything else extracted alongside the payload (checksums.md5, etc.) - only
            // the Partial_Client archive or matched folder(s) survive.
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
                if (partialFolderPaths.Contains(dir))
                {
                    continue;
                }

                Directory.Delete(dir, recursive: true);
            }

            if (partialFolderPaths.Count == 1)
            {
                string singleContentRoot = FindContentRoot(partialFolderPaths[0]);
                await ExtractNestedArchivesAsync(singleContentRoot, progress, cancellationToken).ConfigureAwait(false);
                return singleContentRoot;
            }

            if (partialFolderPaths.Count > 1)
            {
                // More than one matching folder (e.g. "Partial Client" plus "Partial Client_1",
                // "Partial Client_2", ...) - merge all of them into a single combined folder, applied
                // in increment order via the same move-and-merge helper FlattenKnownWrapperFolders
                // uses, so a later increment overwrites an earlier one on filename collision. That's
                // the same "newer wins" reasoning the outer Patch overlay itself already relies on.
                progress.Report(new UpdateProgress($"Merging {partialFolderPaths.Count} partial-client folders...", 51));
                string mergedDir = Path.Combine(tempDir, "_PartialClientMerged_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(mergedDir);

                foreach (var folderPath in partialFolderPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string folderContentRoot = FindContentRoot(folderPath);
                    // Extract any archive this folder carries in place, before it gets merged in, so
                    // its contents (not the raw zip/7z itself) are what lands in mergedDir below.
                    await ExtractNestedArchivesAsync(folderContentRoot, progress, cancellationToken).ConfigureAwait(false);
                    MoveDirectoryContents(folderContentRoot, mergedDir, cancellationToken);
                }

                return mergedDir;
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

            string extractedContentRoot = FindContentRoot(partialExtractDir);
            // Covers the same case as the folder branches above, just for a Partial_Client.zip/.7z
            // that unpacks straight into another archive rather than plain files.
            await ExtractNestedArchivesAsync(extractedContentRoot, progress, cancellationToken).ConfigureAwait(false);
            return extractedContentRoot;
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
