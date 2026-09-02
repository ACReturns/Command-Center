using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommandCenter.Model;
using CommandCenter.View;
using Microsoft.Win32;

// ExecutableScanner (Helpers/ExecutableScanner.cs) lives in the top-level CommandCenter namespace,
// same as AppPaths/InverseBooleanToVisibilityConverter - not CommandCenter.Helpers - hence this
// using rather than "using CommandCenter.Helpers;".
using CommandCenter;

namespace CommandCenter.ViewModel
{
    // A scratch, editable copy of one tab's settings, used only while the Settings tab is open -
    // see SettingsViewModel.DraftTabs. Nothing here touches the live TabSettings/TabInfo/
    // BuildSectionViewModel objects MainWindow actually renders until SettingsViewModel.Save()
    // applies it. That isolation is what makes "discard unsaved changes" trivial: discarding just
    // means throwing this away and re-cloning fresh from the live TabSettings, since the live
    // side was never touched in the first place.
    public class DraftTabViewModel : ViewModelBase
    {
        private string _title;
        private bool _isVisible;
        private string _buildPath;
        private string _versionNumber;
        private bool _supportsPushedToLive;
        private bool _isMarkedForDeletion;
        private bool _isServersExpanded;
        private bool _isExecutablesExpanded;

        private DraftTabViewModel(Guid id, TabKind kind, SectionCategory category, bool isPermanent, bool isNew,
            string title, bool isVisible, string buildPath, string versionNumber, IEnumerable<TabServerEntry> servers,
            IEnumerable<TabExecutableEntry> executables, bool supportsPushedToLive, string? customIconPath)
        {
            Id = id;
            Kind = kind;
            Category = category;
            IsPermanent = isPermanent;
            IsNew = isNew;
            _title = title;
            _isVisible = isVisible;
            _buildPath = buildPath;
            _versionNumber = versionNumber;
            _supportsPushedToLive = supportsPushedToLive;
            CustomIconPath = customIconPath;

            BrowseCommand = new RelayCommand(_ => Browse());
            DeleteCommand = new RelayCommand(_ => { IsMarkedForDeletion = true; RaiseChanged(); }, _ => CanDelete);
            RestoreCommand = new RelayCommand(_ => { IsMarkedForDeletion = false; RaiseChanged(); }, _ => IsMarkedForDeletion);
            AddCustomServerCommand = new RelayCommand(_ => AddCustomServer());
            ChooseIconCommand = new RelayCommand(_ => ChooseIcon(), _ => CanCustomizeIcon);
            ResetIconCommand = new RelayCommand(_ => ResetIcon(), _ => CanCustomizeIcon && IsCustomIcon);

            // Collapsed by default only once there's something to hide - a tab that already has
            // servers configured (GMS/CMS/Live, or an extra tab someone already set up) doesn't
            // need this open every time Settings loads, but a brand-new tab with nothing yet should
            // start open so "+ Add Custom Server" is immediately visible rather than tucked away.
            var serverList = servers as IReadOnlyCollection<TabServerEntry> ?? servers.ToList();
            _isServersExpanded = serverList.Count == 0;

            foreach (var entry in serverList)
            {
                var draft = DraftServerViewModel.FromEntry(entry);
                draft.Changed += OnServerChanged;
                Servers.Add(draft);
            }

            // Same collapsed-only-once-populated reasoning as Servers above.
            var executableList = executables as IReadOnlyCollection<TabExecutableEntry> ?? executables.ToList();
            _isExecutablesExpanded = executableList.Count == 0;

            foreach (var entry in executableList)
            {
                var draft = DraftExecutableViewModel.FromEntry(entry);
                draft.Changed += OnExecutableChanged;
                Executables.Add(draft);
            }

            // Pick up any .exe added to (or removed from) the build folder since this tab's
            // Executables list was last saved - see RescanExecutables. Runs even for a tab with no
            // persisted entries yet (a settings.json saved before this feature existed) so an
            // already-configured BuildPath is reflected the first time Settings is opened after
            // updating, without the user having to re-Browse to the same folder.
            RescanExecutables();
        }

        public static DraftTabViewModel FromSettings(TabSettings settings) => new(
            settings.Id, settings.Kind, settings.Category, settings.IsPermanent, isNew: false,
            title: settings.Title, isVisible: settings.IsVisible, buildPath: settings.BuildPath, versionNumber: settings.VersionNumber,
            servers: settings.Servers ?? Enumerable.Empty<TabServerEntry>(),
            executables: settings.Executables,
            supportsPushedToLive: settings.SupportsPushedToLive,
            customIconPath: settings.CustomIconPath);

        // Always a BuildSection tab - Server Status/Settings are singletons, never created via
        // "+ Add Tab". Starts with no servers - same as any other brand-new General tab (see
        // LaunchServerCatalog.BuiltInEntries) - but "+ Add Custom Server" is available immediately,
        // which is exactly what a fresh tab previously couldn't offer at all. Starts with no
        // executables too - there's nothing to scan yet until Browse picks a build folder.
        public static DraftTabViewModel CreateNew(SectionCategory category, string title) => new(
            Guid.NewGuid(), TabKind.BuildSection, category, isPermanent: false, isNew: true,
            title: title, isVisible: true, buildPath: string.Empty, versionNumber: string.Empty,
            servers: Enumerable.Empty<TabServerEntry>(), executables: Enumerable.Empty<TabExecutableEntry>(),
            supportsPushedToLive: false, customIconPath: null);

        public Guid Id { get; }
        public TabKind Kind { get; }
        public SectionCategory Category { get; }

        // GMS/CMS/Live/Server Status/Settings - can be renamed, but never deleted or hidden
        // (hidden only applies to Settings specifically - see CanHide).
        public bool IsPermanent { get; }

        // Created this Settings session, doesn't exist in the live AppSettings.Tabs yet.
        public bool IsNew { get; }

        public bool IsBuildSection => Kind == TabKind.BuildSection;

        // Settings can never be hidden - it's the user's only way back into Settings, so taking
        // it away would lock them out. Every other tab (permanent or not) can be hidden.
        public bool CanHide => Kind != TabKind.Settings;

        public bool IsMarkedForDeletion
        {
            get => _isMarkedForDeletion;
            private set
            {
                if (SetProperty(ref _isMarkedForDeletion, value))
                {
                    OnPropertyChanged(nameof(CanDelete));
                }
            }
        }

        public bool CanDelete => !IsPermanent && !IsMarkedForDeletion;

        public string Title
        {
            get => _title;
            set { if (SetProperty(ref _title, value)) RaiseChanged(); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { if (SetProperty(ref _isVisible, value)) RaiseChanged(); }
        }

        public string BuildPath
        {
            get => _buildPath;
            set { if (SetProperty(ref _buildPath, value)) { OnPropertyChanged(nameof(HasBuildPath)); RaiseChanged(); } }
        }

        // Whether BuildPath currently points at a real folder - gates the "Available Executables"
        // list/placeholder text in SettingsView, same reasoning as BuildSectionViewModel.HasBuildPath.
        public bool HasBuildPath => !string.IsNullOrWhiteSpace(BuildPath) && Directory.Exists(BuildPath);

        public string VersionNumber
        {
            get => _versionNumber;
            set { if (SetProperty(ref _versionNumber, value)) RaiseChanged(); }
        }

        // "Enable Pushed to Live" - scoped to brand-new tabs only (IsNew), not every extra, so an
        // already-saved tab's Pushed to Live availability can never be flipped after the fact
        // without deleting and recreating it. That's a deliberate simplification: BuildSectionViewModel
        // only ever reads SupportsPushedToLive once, at construction (see MainViewModel.
        // CreateBuildSectionViewModel) - a tab that survives a Save keeps the same live
        // BuildSectionViewModel instance rather than getting a new one, so toggling this on an
        // existing tab would silently do nothing until the app restarted. Restricting the checkbox
        // to IsNew sidesteps that entirely, since a brand-new tab always gets a freshly constructed
        // BuildSectionViewModel the moment it's first saved (MainViewModel.OnSettingsTabsCommitted).
        public bool CanTogglePushedToLive => IsBuildSection && IsNew;

        public bool SupportsPushedToLive
        {
            get => _supportsPushedToLive;
            set { if (SetProperty(ref _supportsPushedToLive, value)) RaiseChanged(); }
        }

        // "Change Icon" / "Use Default" - disabled entirely for the 5 permanent tabs, which keep
        // whatever icon was implemented for them. Every non-permanent tab is always a BuildSection
        // tab in practice (see CreateNew above), but this is gated on IsPermanent directly - same
        // reasoning as CanHide - so it stays correct even if that invariant ever changes.
        public bool CanCustomizeIcon => !IsPermanent;

        public string? CustomIconPath { get; private set; }
        public bool IsCustomIcon => CustomIconPath != null;

        // What SettingsView actually shows next to "Change Icon" - falls back to this tab's own
        // Kind/Category default (the same lookup TabInfo.IconSource uses for the live tab strip)
        // until/unless a custom icon has been picked. See TabIconCatalog.
        public string IconPreviewSource => CustomIconPath ?? TabIconCatalog.DefaultIconFor(Kind, Category);

        // The text SettingsView shows next to "Change Icon"/"Use Default" - e.g. "App Icon" for a
        // brand-new tab's default, "Maple" if that preset was explicitly picked, or "Custom image"
        // for an uploaded one. Making the current selection visible here (rather than a static
        // "Tab icon" caption) is what tells the user what's already applied without having to open
        // ChooseIconDialog to check.
        public string IconPreviewLabel => TabIconCatalog.LabelFor(IconPreviewSource);

        public RelayCommand BrowseCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand RestoreCommand { get; }
        public RelayCommand ChooseIconCommand { get; }
        public RelayCommand ResetIconCommand { get; }

        // This tab's own draft server list - see DraftServerViewModel and SettingsViewModel.Save,
        // which is what actually commits it into the live TabSettings.Servers. Only meaningful for
        // a BuildSection tab (IsBuildSection); stays empty for ServerStatus/Settings, same as
        // BuildPath/VersionNumber above.
        public ObservableCollection<DraftServerViewModel> Servers { get; } = new();
        public RelayCommand AddCustomServerCommand { get; }

        // Purely a view-state toggle for the "Available Servers" Expander in SettingsView - not
        // persisted, and expanding/collapsing it is not itself an unsaved change (no RaiseChanged
        // here), same reasoning as ServerGroupViewModel.IsExpanded not touching IsDirty anywhere.
        public bool IsServersExpanded
        {
            get => _isServersExpanded;
            set => SetProperty(ref _isServersExpanded, value);
        }

        // This tab's own draft executable list - every .exe RescanExecutables has found sitting in
        // BuildPath, with whatever enabled/disabled state Settings' "Available Executables"
        // checkboxes have set. See DraftExecutableViewModel and SettingsViewModel.Save, which
        // commits this into the live TabSettings.Executables. Only meaningful for a BuildSection
        // tab, same as Servers/BuildPath/VersionNumber above.
        public ObservableCollection<DraftExecutableViewModel> Executables { get; } = new();

        // Same view-state-only toggle as IsServersExpanded, for the "Available Executables"
        // Expander.
        public bool IsExecutablesExpanded
        {
            get => _isExecutablesExpanded;
            set => SetProperty(ref _isExecutablesExpanded, value);
        }

        // Fired on any edit (including delete/restore/move) so SettingsViewModel can flip
        // IsDirty - reordering itself is driven from SettingsViewModel directly (it owns the
        // list), which sets IsDirty on its own rather than through this event.
        public event Action? Changed;
        private void RaiseChanged() => Changed?.Invoke();

        // A server-level edit (including delete/restore/toggle) bubbles up the same way a
        // tab-level edit does - see RaiseChanged above.
        private void OnServerChanged() => RaiseChanged();

        // An executable's enabled/disabled checkbox being flipped by hand bubbles up the same way -
        // see DraftExecutableViewModel.Changed for why RescanExecutables itself never triggers this.
        private void OnExecutableChanged() => RaiseChanged();

        // Reconciles Executables against whatever .exe files ExecutableScanner actually finds in
        // BuildPath right now: anything already known keeps its IsEnabled exactly as the user left
        // it; anything newly found is added (enabled by default - see
        // DraftExecutableViewModel.CreateDiscovered); anything previously known that's no longer in
        // the folder (moved, deleted, or BuildPath was just changed to somewhere else entirely) is
        // dropped, since a stale entry for a file that isn't there has nothing to launch. Matching
        // is by file name, case-insensitively (Windows file names aren't case-sensitive).
        //
        // Deliberately doesn't call RaiseChanged - this just mirrors reality on disk, not a setting
        // the user changed, so reopening Settings (or re-picking the same folder) on an unchanged
        // build directory never flips on a false "Unsaved changes". Called once from the
        // constructor and again every time Browse() picks a new folder.
        private void RescanExecutables()
        {
            var found = ExecutableScanner.ScanExecutables(BuildPath);
            var foundSet = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);

            for (int i = Executables.Count - 1; i >= 0; i--)
            {
                if (!foundSet.Contains(Executables[i].FileName))
                {
                    Executables[i].Changed -= OnExecutableChanged;
                    Executables.RemoveAt(i);
                }
            }

            var alreadyKnown = new HashSet<string>(Executables.Select(e => e.FileName), StringComparer.OrdinalIgnoreCase);

            foreach (var fileName in found)
            {
                if (alreadyKnown.Contains(fileName))
                {
                    continue;
                }

                var draft = DraftExecutableViewModel.CreateDiscovered(fileName);
                draft.Changed += OnExecutableChanged;
                Executables.Add(draft);
            }
        }

        private void AddCustomServer()
        {
            if (AddEditServerDialog.PromptForServer("Add Custom Server", string.Empty, LaunchMode.GameLaunching, string.Empty, string.Empty, string.Empty,
                    out string name, out LaunchMode mode, out string host, out string port, out string raw))
            {
                var draft = DraftServerViewModel.CreateCustom(name, mode, host, port, raw);
                draft.Changed += OnServerChanged;
                Servers.Add(draft);
                RaiseChanged();
            }
        }

        // Opens ChooseIconDialog (built-in presets + "Browse for image..." with dimension
        // validation - see TabIconCatalog.RequiredCustomIconSize). Only reachable while
        // CanCustomizeIcon, i.e. never for a permanent tab.
        private void ChooseIcon()
        {
            if (!ChooseIconDialog.PromptForIcon(Application.Current?.MainWindow, Id, IconPreviewSource, out string? chosen) || chosen == null)
            {
                return;
            }

            CustomIconPath = chosen;
            OnPropertyChanged(nameof(CustomIconPath));
            OnPropertyChanged(nameof(IsCustomIcon));
            OnPropertyChanged(nameof(IconPreviewSource));
            OnPropertyChanged(nameof(IconPreviewLabel));
            RaiseChanged();
        }

        private void ResetIcon()
        {
            CustomIconPath = null;
            OnPropertyChanged(nameof(CustomIconPath));
            OnPropertyChanged(nameof(IsCustomIcon));
            OnPropertyChanged(nameof(IconPreviewSource));
            OnPropertyChanged(nameof(IconPreviewLabel));
            RaiseChanged();
        }

        private void Browse()
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Select {Title} build folder",
                InitialDirectory = string.IsNullOrWhiteSpace(BuildPath) || !Directory.Exists(BuildPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : BuildPath
            };

            if (dialog.ShowDialog() == true)
            {
                BuildPath = dialog.FolderName;
                RescanExecutables();
            }
        }
    }
}
