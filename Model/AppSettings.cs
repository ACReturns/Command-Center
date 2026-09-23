using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommandCenter.Model
{
    // LEGACY (pre-tabs) per-section shape. Nothing in the app writes or reads these directly
    // anymore - they're kept only so a settings.json saved by an older version still
    // deserializes without throwing, and SettingsService.Load migrates them into AppSettings.Tabs
    // (below) the first time such a file is loaded. Do not build new features on this class.
    public class SectionSettings : INotifyPropertyChanged
    {
        private string _buildPath = string.Empty;
        private string _versionNumber = string.Empty;

        public string BuildPath
        {
            get => _buildPath;
            set
            {
                if (_buildPath != value)
                {
                    _buildPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string VersionNumber
        {
            get => _versionNumber;
            set
            {
                if (_versionNumber != value)
                {
                    _versionNumber = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // LEGACY - see SectionSettings above. Superseded by TabSettings (IsPermanent = false).
    public class ExtraSectionSettings : SectionSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public SectionCategory Category { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    // Which of the Server Status tab's groups are expanded - persisted so the user's
    // collapse/expand choice survives an app restart. Live and Staging default collapsed
    // (there are a lot of worlds in both); Test defaults expanded since it's small.
    public class ServerStatusSettings
    {
        public bool LiveExpanded { get; set; } = false;
        public bool StagingExpanded { get; set; } = false;
        public bool TestExpanded { get; set; } = true;

        // User-added groups from Server Status' "Add New Server" (each backed by its own json
        // copied into the Servers folder). Order here is display order - always after the 3
        // built-in groups. See CustomServerGroupSettings.
        public List<CustomServerGroupSettings> CustomGroups { get; set; } = new();
    }

    // One user-added Server Status group. FileName is just the file name (not a full path) -
    // always resolved against AppPaths.ServersFolder, same as the 3 built-in groups, so it stays
    // valid even if the app is reinstalled to a different folder.
    public class CustomServerGroupSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool IsExpanded { get; set; } = true;
    }

    public class AppSettings
    {
        // Every top-level tab (GMS/CMS/Live/Server Status/Settings and any extra), in display
        // order - the current, live model. See TabSettings and SettingsService's migration.
        public List<TabSettings> Tabs { get; set; } = new();

        // Server Status tab's per-group expanded/collapsed state - see ServerStatusSettings.
        public ServerStatusSettings ServerStatus { get; set; } = new();

        // User opt-in for the app to relaunch itself elevated (UAC "runas") on the next launch -
        // see App.xaml.cs's OnStartup override. Defaults to false (asInvoker, the project's
        // existing default - there's no app.manifest requesting elevation) so a settings.json
        // saved before this feature existed just deserializes this to false, matching prior
        // behavior exactly.
        public bool RunAsAdministrator { get; set; } = false;

        // Google Sheets URL the Maintenance Version Verification tab pulls Ver/Tag/Checksum rows
        // from - see MaintenanceDefaults.SheetUrl and MaintenanceVerificationViewModel.SheetUrl,
        // which falls back to the same default if this is ever blank. An existing settings.json
        // without this key just deserializes it to the default.
        public string MaintenanceSheetUrl { get; set; } = MaintenanceDefaults.SheetUrl;

        // Whether the Maintenance Version Verification tab's release-version groups (v268, v269, ...)
        // are laid out newest-family-first (true) or oldest-family-first / sheet order (false, the
        // original behavior). Toggled from a checkbox on the tab itself and saved immediately - see
        // MaintenanceVerificationViewModel.NewestFirst. An existing settings.json without this key
        // just deserializes it to false, matching prior behavior exactly.
        public bool MaintenanceNewestFirst { get; set; } = false;

        // LEGACY - see SectionSettings above. Present only for backward-compatible
        // deserialization of a settings.json saved before tabs existed; SettingsService.Load
        // resets these to empty immediately after migrating them into Tabs once.
        public SectionSettings Gms { get; set; } = new();
        public SectionSettings Cms { get; set; } = new();
        public SectionSettings Live { get; set; } = new();
        public List<ExtraSectionSettings> ExtraSections { get; set; } = new();
    }
}
