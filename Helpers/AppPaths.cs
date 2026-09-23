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

        // WebView2's user data folder for the OpTool tab (see View/OpToolView.xaml.cs). Its
        // default would be next to the exe, which isn't writable from Program Files. The OpTool
        // session runs InPrivate, so no cookies/logins persist here - just browser housekeeping.
        public static string WebView2DataFolder => Path.Combine(AppDataFolder, "WebView2");

        public static string ServersFolder => Path.Combine(AppContext.BaseDirectory, "Servers");

        public static string LiveWorldsFile => Path.Combine(ServersFolder, "live_server_status.json");
        public static string StagingWorldsFile => Path.Combine(ServersFolder, "staging_server_status.json");
        public static string TestWorldsFile => Path.Combine(ServersFolder, "test_server_status.json");

        // Where the two Server Status gifs land in the OUTPUT folder - img\*.gif is copied there
        // by the csproj's <None Include="img\*.gif" CopyToOutputDirectory="PreserveNewest" /> item,
        // the exact same "loose file copied next to the exe" mechanism the Servers\*.json files
        // above already use, just from img\ instead of Servers\. Originally these lived under the
        // Servers folder itself; moved into img/ (2026-09-22) alongside the tab icons since that's
        // where the user now keeps them on disk, but they are NOT embedded pack resources like
        // img\*.ico is (that route was tried first and didn't pan out - WpfAnimatedGif's
        // AnimatedSource couldn't reliably load them from an embedded pack-resource stream, even
        // after percent-encoding the space in each file name - so this stays a real absolute
        // filesystem path, same as every other AppPaths.*File/*Folder member).
        public static string ServerUpGif => Path.Combine(AppContext.BaseDirectory, "img", "Server Up.gif");
        public static string ServerDownGif => Path.Combine(AppContext.BaseDirectory, "img", "Server Down.gif");
    }
}
