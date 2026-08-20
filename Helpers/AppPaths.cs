using System;
using System.IO;

namespace CommandCenter
{
    public static class AppPaths
    {
        public static string ServersFolder => Path.Combine(AppContext.BaseDirectory, "Servers");

        public static string LiveWorldsFile => Path.Combine(ServersFolder, "live_server_status.json");
        public static string StagingWorldsFile => Path.Combine(ServersFolder, "staging_server_status.json");
        public static string TestWorldsFile => Path.Combine(ServersFolder, "test_server_status.json");

        public static string ServerUpGif => Path.Combine(ServersFolder, "Server Up.gif");
        public static string ServerDownGif => Path.Combine(ServersFolder, "Server Down.gif");
    }
}
