using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommandCenter.Model
{
    // One top-level tab's persisted state. GMS/CMS/Live/Server Status/Settings always exist
    // (IsPermanent = true - can't be deleted, though their Title can still be renamed); any tab
    // created via Settings' "+ Add Tab" is IsPermanent = false and can be deleted. Implements
    // INotifyPropertyChanged so BuildSectionViewModel (for BuildPath/VersionNumber/Title) and
    // TabInfo (for Title/IsVisible/Order, driving the tab strip) can both react live once a
    // change has actually been committed via Settings' Save - see SettingsViewModel/
    // DraftTabViewModel for the staging that happens before that.
    public class TabSettings : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private bool _isVisible = true;
        private int _order;
        private string _buildPath = string.Empty;
        private string _versionNumber = string.Empty;
        private List<TabServerEntry>? _servers = null;

        public Guid Id { get; set; } = Guid.NewGuid();
        public TabKind Kind { get; set; }

        // Meaningful only when Kind == BuildSection: which fixed server catalog/executable set
        // (and, for Live, Pushed to Live support) this tab gets - see LaunchServerCatalog.
        public SectionCategory Category { get; set; }

        // GMS/CMS/Live/Server Status/Settings - always exist, never deletable, but still
        // renameable. Anything created via "+ Add Tab" is deletable.
        public bool IsPermanent { get; set; }

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }

        // Whether this tab shows up in the tab strip at all. Settings itself is never allowed to
        // be false - see SettingsViewModel.Save, which forces it back to true regardless of what
        // the draft says, so the user can never lock themselves out of Settings.
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        public int Order
        {
            get => _order;
            set { if (_order != value) { _order = value; OnPropertyChanged(); } }
        }

        // BuildSection tabs only - unused (stays empty) for ServerStatus/Settings.
        public string BuildPath
        {
            get => _buildPath;
            set { if (_buildPath != value) { _buildPath = value; OnPropertyChanged(); } }
        }

        public string VersionNumber
        {
            get => _versionNumber;
            set { if (_versionNumber != value) { _versionNumber = value; OnPropertyChanged(); } }
        }

        // BuildSection tabs only - every entry offered in this tab's launch dropdown (see
        // BuildSectionViewModel.ServerOptions), built-in and custom side by side (see
        // TabServerEntry.Source). null only ever appears in a settings.json saved before this
        // field existed - SettingsService.MigrateServersIfNeeded backfills it (seeding Gms/Cms/Live
        // tabs from LaunchServerCatalog's built-in registry) the first time such a file loads, so
        // by the time anything else reads this it's always a real, possibly-empty list. Kept
        // nullable rather than defaulting to empty specifically so that migration step can tell
        // "never migrated" apart from "user removed every server on purpose".
        public List<TabServerEntry>? Servers
        {
            get => _servers;
            set { if (_servers != value) { _servers = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
