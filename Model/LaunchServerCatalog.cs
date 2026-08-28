using System;
using System.Collections.Generic;

namespace CommandCenter.Model
{
    // Fixed sets of QA/live connection targets offered when launching a GMS/CMS/Live Service
    // build, plus the fixed set of client executables all three sections can launch. Each
    // LaunchArgument string is passed to the client executable exactly as-is at launch time.
    public static class LaunchServerCatalog
    {
        public static IReadOnlyList<string> Executables { get; } = new List<string>
        {
            "MapleStoryA.exe",
            "MapleStory.exe",
        };

        // GMS: everything except Test 4 - Test 4 is reserved for Live Service Builds.
        public static IReadOnlyList<LaunchServerOption> GmsServers { get; } = new List<LaunchServerOption>
        {
            new("Test 1", "GameLaunching 34.217.160.238 8484"),
            new("Test 2", "GameLaunching 52.43.197.199 8484"),
            new("Test 3", "GameLaunching 54.148.16.230 8484"),
            new("Test 6", "GameLaunching 52.89.167.110 8484"),
            new("Staging (NA)", "GameLaunching 44.234.170.29 8484"),
            new("Staging (EU)", "GameLaunching 3.77.198.24 8484"),
            new("Staging (World Merge)", "GameLaunching 44.234.182.79 8484"),
        };

        // CMS: its own catalog, independent of GMS's.
        public static IReadOnlyList<LaunchServerOption> CmsServers { get; } = new List<LaunchServerOption>
        {
            new("Test 1", "ipport 10.9.2.132 8484"),
        };

        // Live Service Builds: Test 4 plus all Staging options.
        public static IReadOnlyList<LaunchServerOption> LiveServers { get; } = new List<LaunchServerOption>
        {
            new("Test 4", "GameLaunching 54.148.59.7 8484"),
            new("Staging (NA)", "GameLaunching 44.234.170.29 8484"),
            new("Staging (EU)", "GameLaunching 3.77.198.24 8484"),
            new("Staging (World Merge)", "GameLaunching 44.234.182.79 8484"),
        };

        // An extra (user-added) section behaves exactly like its parent category: same server
        // catalog, same client executables. This is how that's looked up by category.
        public static IReadOnlyList<LaunchServerOption> ServersFor(SectionCategory category) => category switch
        {
            SectionCategory.Gms => GmsServers,
            SectionCategory.Cms => CmsServers,
            SectionCategory.Live => LiveServers,
            _ => Array.Empty<LaunchServerOption>()
        };

        public static string DisplayName(SectionCategory category) => category switch
        {
            SectionCategory.Gms => "GMS",
            SectionCategory.Cms => "CMS",
            SectionCategory.Live => "Live Service",
            _ => category.ToString()
        };
    }
}
