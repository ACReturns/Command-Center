using System.Collections.Generic;

namespace CommandCenter.Model
{
    // Fixed set of QA connection targets offered when launching a GMS/CMS build. The argument
    // string is passed to the client executable exactly as-is at launch time.
    public static class LaunchServerCatalog
    {
        public static IReadOnlyList<LaunchServerOption> Servers { get; } = new List<LaunchServerOption>
        {
            new("Test 1", "GameLaunching 44.234.170.29 8484"),
            new("Test 2", "GameLaunching 44.234.182.79 8484"),
            new("Test 3", "GameLaunching 54.148.16.230 8484"),
            new("Test 4", "GameLaunching 54.148.59.7 8484"),
            new("Test 6", "GameLaunching 52.89.167.110 8484"),
            new("Staging (NA)", "GameLaunching 44.234.170.29 8484"),
            new("Staging (EU)", "GameLaunching 3.77.198.24 8484"),
            new("Staging (World Merge)", "GameLaunching 44.234.182.79 8484"),
        };
    }
}
