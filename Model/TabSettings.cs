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
        private bool _supportsPushedToLive;
        private bool _debugCommandListEnabled;
        private string? _customIconPath;
        private List<TabServerEntry>? _servers = null;
        private List<TabExecutableEntry> _executables = new();
        private string _lastSelectedExecutable = string.Empty;

        public Guid Id { get; set; } = Guid.NewGuid();
        public TabKind Kind { get; set; }

        // Meaningful only when Kind == BuildSection: which fixed server catalog (and, for Live,
        // Pushed to Live support) this tab gets - see LaunchServerCatalog. Executable options are
        // no longer tied to Category; every tab discovers its own from BuildPath - see Executables.
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

        // BuildSection tabs only, and only ever set for a tab that was NEW when this was toggled -
        // see DraftTabViewModel.CanTogglePushedToLive. Read once at construction by
        // MainViewModel.CreateBuildSectionViewModel into BuildSectionViewModel's own (readonly)
        // SupportsPushedToLive - the permanent Live tab always has this forced true regardless of
        // what's persisted (see SettingsService), since that one's Pushed to Live support has never
        // been optional.
        public bool SupportsPushedToLive
        {
            get => _supportsPushedToLive;
            set { if (_supportsPushedToLive != value) { _supportsPushedToLive = value; OnPropertyChanged(); } }
        }

        // BuildSection tabs only - the "Enable Debug Command List" checkbox in the Launch panel
        // (see BuildSectionView.xaml / BuildSectionViewModel.DebugCommandListEnabled). Written
        // directly by BuildSectionViewModel, outside of Settings' Save flow, same as
        // LastSelectedExecutable below - it's a launch-time toggle, not something staged through
        // DraftTabViewModel. Only controls whether the Launch panel shows the debug command list
        // editor and whether Launch copies cmd_uidebug.txt into the build folder - the saved
        // entries themselves live on disk under AppPaths.DebugCommandsFileFor, not here, so
        // toggling this off and back on never loses anything. See
        // Services/DebugCommandListService.cs.
        public bool DebugCommandListEnabled
        {
            get => _debugCommandListEnabled;
            set { if (_debugCommandListEnabled != value) { _debugCommandListEnabled = value; OnPropertyChanged(); } }
        }

        // Absolute path to a user-picked custom tab icon (see Settings' "Change Icon" ->
        // ChooseIconDialog), or null to fall back to the Kind/Category default - see
        // TabIconCatalog.IconFor. Always null for the 5 permanent tabs (DraftTabViewModel.
        // CanCustomizeIcon disables the option for them). The file itself lives under
        // AppPaths.TabIconsFolder, named after this tab's Id, rather than pointing at wherever the
        // user originally browsed to - see ChooseIconDialog.TryValidateAndCopy.
        public string? CustomIconPath
        {
            get => _customIconPath;
            set { if (_customIconPath != value) { _customIconPath = value; OnPropertyChanged(); } }
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

        // BuildSection tabs only - every .exe DraftTabViewModel.RescanExecutables has found sitting
        // in BuildPath, enabled/disabled via Settings' "Available Executables" checkboxes (see
        // TabExecutableEntry). Replaces the old fixed LaunchServerCatalog.Executables list - unlike
        // Servers there's no legacy migration needed here (this field never existed with different
        // data before), so it just defaults to an empty list; the first time Settings is opened on
        // a tab with a BuildPath already set, RescanExecutables populates it from whatever .exe
        // files are actually there.
        public List<TabExecutableEntry> Executables
        {
            get => _executables;
            set { if (_executables != value) { _executables = value; OnPropertyChanged(); } }
        }

        // BuildSection tabs only - the file name (matched against Executables, case-insensitively)
        // BuildSectionViewModel had SelectedExecutable set to the last time the user actually picked
        // one, so the Select Executable dropdown can come back to the same choice the next time the
        // app starts instead of always resetting to the first enabled entry. Written directly by
        // BuildSectionViewModel outside of Settings' Save flow (same pattern VersionNumber already
        // uses for a version applied after a build/patch/push) rather than staged through
        // DraftTabViewModel - remembering the last launch choice isn't really a "setting" the user
        // edits, so there's nothing worth surfacing on the Settings screen for it.
        public string LastSelectedExecutable
        {
            get => _lastSelectedExecutable;
            set { if (_lastSelectedExecutable != value) { _lastSelectedExecutable = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
