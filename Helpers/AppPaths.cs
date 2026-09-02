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

        public static string ServersFolder => Path.Combine(AppContext.BaseDirectory, "Servers");

        public static string LiveWorldsFile => Path.Combine(ServersFolder, "live_server_status.json");
        public static string StagingWorldsFile => Path.Combine(ServersFolder, "staging_server_status.json");
        public static string TestWorldsFile => Path.Combine(ServersFolder, "test_server_status.json");

        public static string ServerUpGif => Path.Combine(ServersFolder, "Server Up.gif");
        public static string ServerDownGif => Path.Combine(ServersFolder, "Server Down.gif");
    }
}
