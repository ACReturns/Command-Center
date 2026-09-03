using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
// AppPaths (Helpers/AppPaths.cs) lives in the top-level CommandCenter namespace, not
// CommandCenter.Services - same reasoning/precedent as ChooseIconDialog.xaml.cs's own
// "using CommandCenter;" for its TabIconsFolder references.
using CommandCenter;

namespace CommandCenter.Services
{
    // Manages a BuildSection tab's "debug command list" - the entries edited via the Launch
    // panel's "Enable Debug Command List" area (see BuildSectionView.xaml / BuildSectionViewModel).
    // Saved under %AppData%\CommandCenter\DebugCommands\<tabId>\cmd_uidebug.txt (see
    // AppPaths.DebugCommandsFileFor), one command per line, independently of settings.json - same
    // "lives in AppData, named after the tab's Id" convention TabIconsFolder already uses for
    // custom tab icons. At launch time (BuildSectionViewModel.Launch), CopyToBuildFolder drops this
    // same file straight into the tab's Current Build folder so whatever the client executable
    // expects to find there is always this tab's latest saved list.
    public static class DebugCommandListService
    {
        // Every line in tabId's saved list, in order - empty if nothing's been saved yet (a
        // brand-new tab, or one that's never had the debug list enabled).
        public static List<string> Load(Guid tabId)
        {
            string path = AppPaths.DebugCommandsFileFor(tabId);

            if (!File.Exists(path))
            {
                return new List<string>();
            }

            try
            {
                return File.ReadAllLines(path).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        // Overwrites tabId's saved list with exactly what's passed in - called after every
        // Add/Edit/Delete in the Launch panel so the file on disk never drifts from what's shown
        // in the UI.
        public static void Save(Guid tabId, IEnumerable<string> commands)
        {
            string path = AppPaths.DebugCommandsFileFor(tabId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, commands);
        }

        // Copies tabId's saved list into buildPath as cmd_uidebug.txt, overwriting whatever's
        // already there - see BuildSectionViewModel.Launch, called right before the client
        // executable starts. No-ops (returns false) when there's nothing saved yet or buildPath
        // isn't a real folder, so Launch never drops a stray/empty file into the build folder just
        // because the toggle happens to be on.
        public static bool CopyToBuildFolder(Guid tabId, string buildPath)
        {
            string source = AppPaths.DebugCommandsFileFor(tabId);

            if (!File.Exists(source) || string.IsNullOrWhiteSpace(buildPath) || !Directory.Exists(buildPath))
            {
                return false;
            }

            try
            {
                File.Copy(source, Path.Combine(buildPath, AppPaths.DebugCommandListFileName), overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Best-effort delete of tabId's saved list folder - mirrors MainViewModel.TeardownTab's
        // cleanup of a deleted tab's custom icon, so nothing accumulates under AppData for a tab
        // that no longer exists. Never reached for the 5 permanent tabs, which can't be deleted.
        public static void DeleteFolder(Guid tabId)
        {
            string folder = Path.Combine(AppPaths.DebugCommandsFolder, tabId.ToString());

            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort only - same reasoning as TeardownTab's icon cleanup.
            }
        }
    }
}
