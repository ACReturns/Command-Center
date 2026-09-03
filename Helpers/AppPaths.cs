using System;
using System.IO;

namespace CommandCenter
{
    public static class AppPaths
    {
        // Same %AppData%\CommandCenter folder settings.json already lives in (see
        // SettingsService) - not derived from it directly to avoid a dependency from here into
        // Model, but deliberately kept in sync with it.
        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommandCenter");

        // Where a custom tab icon (Settings' "Change Icon") gets copied to, named after the owning
        // tab's Id - see ChooseIconDialog.TryValidateAndCopy / TabSettings.CustomIconPath. Created
        // on first use, not at startup - most installs will never have a custom icon.
        public static string TabIconsFolder => Path.Combine(AppDataFolder, "TabIcons");

        // Name the debug command list always has once it's sitting in a build folder - see
        // Services/DebugCommandListService.CopyToBuildFolder / BuildSectionViewModel.Launch. The
        // saved copy under DebugCommandsFolder keeps this exact same name too (just nested one
        // level deeper, per tab), so "copy into the build folder" is a straight file copy with no
        // rename involved.
        public const string DebugCommandListFileName = "cmd_uidebug.txt";

        // Root folder for every tab's saved debug command list - one subfolder per tab, named
        // after its Id (same "named after the owning tab's Id" convention as TabIconsFolder above),
        // holding that tab's own cmd_uidebug.txt. Created on first use, not at startup - see
        // Services/DebugCommandListService.Save.
        public static string DebugCommandsFolder => Path.Combine(AppDataFolder, "DebugCommands");

        // Where tabId's saved debug command list lives on disk - see
        // Services/DebugCommandListService and TabSettings.DebugCommandListEnabled.
        public static string DebugCommandsFileFor(Guid tabId) =>
            Path.Combine(DebugCommandsFolder, tabId.ToString(), DebugCommandListFileName);

        public static string ServersFolder => Path.Combine(AppContext.BaseDirectory, "Servers");

        public static string LiveWorldsFile => Path.Combine(ServersFolder, "live_server_status.json");
        public static string StagingWorldsFile => Path.Combine(ServersFolder, "staging_server_status.json");
        public static string TestWorldsFile => Path.Combine(ServersFolder, "test_server_status.json");

        public static string ServerUpGif => Path.Combine(ServersFolder, "Server Up.gif");
        public static string ServerDownGif => Path.Combine(ServersFolder, "Server Down.gif");
    }
}
