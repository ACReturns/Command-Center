using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandCenter.Model;

namespace CommandCenter.Services
{
    // Manages each build section's "Documents" folder - a small set of reference files (patch
    // notes, checklists, screenshots, whatever the user wants attached to a build) that Command
    // Center keeps next to whichever build a section currently points at.
    //
    // Deliberately created as a SIBLING of the section's build folder, never inside it - e.g. if
    // GMS's build path is D:\Builds\GMS\CurrentBuild, its documents land in
    // D:\Builds\GMS\<name> Documents. That placement is what makes "keep the documents with the
    // build" automatic: BuildUpdateService's ClearDirectory (New Build) and PushToLiveService's
    // ClearDirectory (Pushed to Live) only ever touch the build folder itself, so a sibling folder
    // is never in their blast radius - the documents just sit there, untouched, through every
    // build/patch/push. See BuildSectionViewModel.SyncDocumentsFolder for the
    // create/rename/watch lifecycle that drives this from the settings side.
    //
    // Folder naming is keyed off the version FAMILY, not the exact version - see VersionFamily -
    // so "271.0.2 Documents" becomes "271 Documents" and stays that way through every later
    // 271.x bump; only a genuinely new family (e.g. "272.0.1") gets its own new folder.
    public static class DocumentsService
    {
        // Folder name for the "no build name set yet" fallback, e.g. "GMS Documents".
        public static string FallbackFolderName(string sectionTitle) => $"{sectionTitle} Documents";

        // The "version family" a build/version name belongs to - everything before the first
        // delimiter, e.g. "271" for both "271.0.2" and "271.0.3". Two versions in the same family
        // share one Documents folder instead of getting a new one for every single version bump;
        // a version with no delimiter at all (or an empty string) is its own family. Falls back to
        // the whole string when there's no '.' to split on.
        public static string VersionFamily(string versionNumber)
        {
            if (string.IsNullOrWhiteSpace(versionNumber))
            {
                return string.Empty;
            }

            string trimmed = versionNumber.Trim();
            int delimiterIndex = trimmed.IndexOf('.');
            return delimiterIndex > 0 ? trimmed[..delimiterIndex] : trimmed;
        }

        // Folder name once a build/version name has been set, keyed off the version FAMILY (see
        // VersionFamily above) rather than the exact version - e.g. "271 Documents" for both
        // "271.0.2" and "271.0.3", so the folder stays put and just keeps accumulating documents
        // as the version climbs within that family. A genuinely new family (e.g. "272.0.1") maps
        // to a different folder name ("272 Documents"), which BuildSectionViewModel.SyncDocumentsFolder
        // treats as a fresh folder rather than renaming the old family's into it - see that method
        // for why.
        public static string VersionedFolderName(string versionNumber) => $"{VersionFamily(versionNumber)} Documents";

        private static string? BaseDirFor(string buildPath)
        {
            if (string.IsNullOrWhiteSpace(buildPath))
            {
                return null;
            }

            string trimmed = buildPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmed);

            // A build path sitting at a drive root (e.g. "D:\") has no parent to be a sibling of -
            // fall back to nesting the documents folder inside the build path itself rather than
            // throwing. (Everywhere else, this stays a true sibling, outside the build folder.)
            return string.IsNullOrEmpty(parent) ? trimmed : parent;
        }

        // The documents folder for a section: a sibling of its build folder, named per
        // FallbackFolderName/VersionedFolderName above. Null when there's no build path to sit
        // beside yet.
        public static string? FolderPathFor(string buildPath, string folderName)
        {
            string? baseDir = BaseDirFor(buildPath);
            return baseDir == null ? null : Path.Combine(baseDir, folderName);
        }

        // One-time adoption for folders created before version-family grouping existed: if a
        // family's folder (e.g. "271 Documents") doesn't exist yet but an old exact-version
        // folder that belongs to the same family is still sitting there (e.g. a lingering
        // "271.0.2 Documents" from before this naming scheme), that's the folder to fold in
        // instead of starting empty - existing documents shouldn't appear to vanish just because
        // the naming scheme changed. Picks the most recently modified match if more than one is
        // found. Returns null if nothing matches.
        public static string? FindLegacyFamilyFolder(string buildPath, string family)
        {
            string? baseDir = BaseDirFor(buildPath);
            if (baseDir == null || !Directory.Exists(baseDir))
            {
                return null;
            }

            const string suffix = " Documents";
            string prefix = family + ".";

            return Directory.GetDirectories(baseDir)
                .Where(dir =>
                {
                    string name = Path.GetFileName(dir);
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    string versionPart = name[..^suffix.Length];
                    return versionPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        public static void EnsureFolder(string folderPath) => Directory.CreateDirectory(folderPath);

        // Top-level entries only (files and subfolders alike - a whole folder can be dropped in
        // via AddPaths below, and shows up here as one entry, opened via Explorer).
        public static List<DocumentEntry> ListEntries(string folderPath)
        {
            var entries = new List<DocumentEntry>();

            if (!Directory.Exists(folderPath))
            {
                return entries;
            }

            foreach (var dir in Directory.GetDirectories(folderPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new DocumentEntry(Path.GetFileName(dir), dir, IsDirectory: true));
            }

            foreach (var file in Directory.GetFiles(folderPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new DocumentEntry(Path.GetFileName(file), file, IsDirectory: false));
            }

            return entries;
        }

        // Copies each given path (file or folder) into the documents folder, overwriting a
        // same-named file or merging into a same-named folder. Backs both the "Add File..."
        // button and drag-and-drop from Explorer.
        public static void AddPaths(string folderPath, IEnumerable<string> sourcePaths)
        {
            EnsureFolder(folderPath);

            foreach (var source in sourcePaths)
            {
                string destName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(destName))
                {
                    continue;
                }

                string dest = Path.Combine(folderPath, destName);

                if (Directory.Exists(source))
                {
                    CopyDirectoryRecursive(source, dest);
                }
                else if (File.Exists(source))
                {
                    File.Copy(source, dest, overwrite: true);
                }
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
            }
        }

        // Used when a section's build/version name changes (including right after a New
        // Build/Patch/Pushed to Live applies a pending version number): keeps the documents
        // attached to "the build" by carrying them over from the old name's folder into the new
        // one, instead of leaving them orphaned under a name nothing points at anymore. Copies
        // rather than Directory.Move so this works even if the two names resolve across drives;
        // best-effort - if something can't be copied (locked file, permissions), the old folder
        // is simply left in place rather than losing anything, and a fresh folder still gets
        // created at the new location right after this call.
        public static void RenameFolder(string? oldFolderPath, string newFolderPath)
        {
            if (string.IsNullOrEmpty(oldFolderPath) || !Directory.Exists(oldFolderPath))
            {
                return;
            }

            if (string.Equals(oldFolderPath, newFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                CopyDirectoryRecursive(oldFolderPath, newFolderPath);
                Directory.Delete(oldFolderPath, recursive: true);
            }
            catch
            {
                // Best-effort - see summary above.
            }
        }

        // Used when an extra section is deleted, so its documents don't linger behind as orphaned
        // files nobody can see in the app anymore - "we don't want to keep anything from the
        // extra builds." Best-effort: a locked file shouldn't block the rest of the delete flow.
        public static void DeleteFolder(string? folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            try
            {
                Directory.Delete(folderPath, recursive: true);
            }
            catch
            {
                // Best-effort - see summary above.
            }
        }

        // Deletes ONE entry (a file or a whole subfolder) out of a documents folder - what backs
        // the Delete button/Delete key for a selected item in the Documents list, after
        // BuildSectionViewModel.DeleteDocument has already confirmed with the user. Unlike
        // DeleteFolder/RenameFolder above, this is deliberately NOT wrapped in try/catch: those
        // run automatically as a side effect of a settings change, where silently leaving a
        // locked file behind is the right failure mode, but this is a direct, just-confirmed user
        // action on a single item, so a failure (locked file, permissions) should surface back to
        // the caller as an error rather than being swallowed.
        public static void DeleteEntry(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
