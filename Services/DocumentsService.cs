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
    public static class DocumentsService
    {
        // Folder name for the "no build name set yet" fallback, e.g. "GMS Documents".
        public static string FallbackFolderName(string sectionTitle) => $"{sectionTitle} Documents";

        // Folder name once a build/version name has been set, e.g. "1.2.3 Documents".
        public static string VersionedFolderName(string versionNumber) => $"{versionNumber} Documents";

        // The documents folder for a section: a sibling of its build folder, named per
        // FallbackFolderName/VersionedFolderName above. Null when there's no build path to sit
        // beside yet.
        public static string? FolderPathFor(string buildPath, string folderName)
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
            string baseDir = string.IsNullOrEmpty(parent) ? trimmed : parent;
            return Path.Combine(baseDir, folderName);
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
    }
}
