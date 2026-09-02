using System;
using System.Collections.Generic;
using System.Linq;

namespace CommandCenter.Model
{
    // The "known good" built-in server registry - what seeds a Gms/Cms/Live tab's Servers list the
    // first time it's created (SettingsService.BuildDefaultPermanentTabs) or migrated from a
    // settings.json saved before per-tab servers existed (SettingsService.MigrateServersIfNeeded).
    // From that point on the registry is just a starting point: every tab owns its own persisted,
    // editable copy (TabSettings.Servers) - toggling, editing, or adding a custom entry on one tab
    // never touches these defaults or any other tab. General (every "+ Add Tab" tab) has no
    // built-in entries; it starts empty and relies entirely on custom servers added by hand.
    //
    // BuiltInEntries always builds a fresh set of TabServerEntry instances rather than handing out
    // a shared list - the specs below are immutable data, not the mutable objects tabs go on to
    // edit, so there's no risk of one tab's IsEnabled/edit leaking into another tab (or into a
    // future re-seed).
    public static class LaunchServerCatalog
    {
        private sealed record BuiltInSpec(string DisplayName, LaunchMode Mode, string Host, string Port);

        // GMS: everything except Test 4 - Test 4 is reserved for Live Service Builds.
        private static readonly IReadOnlyList<BuiltInSpec> GmsSpecs = new List<BuiltInSpec>
        {
            new("Test 1", LaunchMode.GameLaunching, "34.217.160.238", "8484"),
            new("Test 2", LaunchMode.GameLaunching, "52.43.197.199", "8484"),
            new("Test 3", LaunchMode.GameLaunching, "54.148.16.230", "8484"),
            new("Test 5", LaunchMode.GameLaunching, "52.35.176.83", "8484"),
            new("Test 6", LaunchMode.GameLaunching, "52.89.167.110", "8484"),
            new("Staging (NA)", LaunchMode.GameLaunching, "44.234.170.29", "8484"),
            new("Staging (EU)", LaunchMode.GameLaunching, "3.77.198.24", "8484"),
            new("Staging (World Merge)", LaunchMode.GameLaunching, "44.234.182.79", "8484"),
        };

        // CMS: its own catalog, independent of GMS's.
        private static readonly IReadOnlyList<BuiltInSpec> CmsSpecs = new List<BuiltInSpec>
        {
            new("Test 1", LaunchMode.IpPort, "10.9.2.132", "8484"),
        };

        // Live Service Builds: Test 4 plus all Staging options.
        private static readonly IReadOnlyList<BuiltInSpec> LiveSpecs = new List<BuiltInSpec>
        {
            new("Test 4", LaunchMode.GameLaunching, "54.148.59.7", "8484"),
            new("Staging (NA)", LaunchMode.GameLaunching, "44.234.170.29", "8484"),
            new("Staging (EU)", LaunchMode.GameLaunching, "3.77.198.24", "8484"),
            new("Staging (World Merge)", LaunchMode.GameLaunching, "44.234.182.79", "8484"),
        };

        // A fresh copy of this category's built-in registry, every entry enabled by default. See
        // the class comment for why this always allocates new instances rather than reusing any.
        public static IReadOnlyList<TabServerEntry> BuiltInEntries(SectionCategory category) =>
            SpecsFor(category).Select(ToEntry).ToList();

        private static IReadOnlyList<BuiltInSpec> SpecsFor(SectionCategory category) => category switch
        {
            SectionCategory.Gms => GmsSpecs,
            SectionCategory.Cms => CmsSpecs,
            SectionCategory.Live => LiveSpecs,
            SectionCategory.General => Array.Empty<BuiltInSpec>(),
            _ => Array.Empty<BuiltInSpec>()
        };

        private static TabServerEntry ToEntry(BuiltInSpec spec) => new()
        {
            DisplayName = spec.DisplayName,
            Mode = spec.Mode,
            Host = spec.Host,
            Port = spec.Port,
            Source = ServerEntrySource.BuiltIn,
            IsEnabled = true
        };

        public static string DisplayName(SectionCategory category) => category switch
        {
            SectionCategory.Gms => "GMS",
            SectionCategory.Cms => "CMS",
            SectionCategory.Live => "Live Service",
            SectionCategory.General => "Extra",
            _ => category.ToString()
        };
    }
}
