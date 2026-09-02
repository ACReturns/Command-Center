using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommandCenter.Model;
using CommandCenter.View;
using Microsoft.Win32;

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
        private bool _isMarkedForDeletion;
        private bool _isServersExpanded;

        private DraftTabViewModel(Guid id, TabKind kind, SectionCategory category, bool isPermanent, bool isNew,
            string title, bool isVisible, string buildPath, string versionNumber, IEnumerable<TabServerEntry> servers)
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

            BrowseCommand = new RelayCommand(_ => Browse());
            DeleteCommand = new RelayCommand(_ => { IsMarkedForDeletion = true; RaiseChanged(); }, _ => CanDelete);
            RestoreCommand = new RelayCommand(_ => { IsMarkedForDeletion = false; RaiseChanged(); }, _ => IsMarkedForDeletion);
            AddCustomServerCommand = new RelayCommand(_ => AddCustomServer());

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
        }

        public static DraftTabViewModel FromSettings(TabSettings settings) => new(
            settings.Id, settings.Kind, settings.Category, settings.IsPermanent, isNew: false,
            title: settings.Title, isVisible: settings.IsVisible, buildPath: settings.BuildPath, versionNumber: settings.VersionNumber,
            servers: settings.Servers ?? Enumerable.Empty<TabServerEntry>());

        // Always a BuildSection tab - Server Status/Settings are singletons, never created via
        // "+ Add Tab". Starts with no servers - same as any other brand-new General tab (see
        // LaunchServerCatalog.BuiltInEntries) - but "+ Add Custom Server" is available immediately,
        // which is exactly what a fresh tab previously couldn't offer at all.
        public static DraftTabViewModel CreateNew(SectionCategory category, string title) => new(
            Guid.NewGuid(), TabKind.BuildSection, category, isPermanent: false, isNew: true,
            title: title, isVisible: true, buildPath: string.Empty, versionNumber: string.Empty,
            servers: Enumerable.Empty<TabServerEntry>());

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
            set { if (SetProperty(ref _buildPath, value)) RaiseChanged(); }
        }

        public string VersionNumber
        {
            get => _versionNumber;
            set { if (SetProperty(ref _versionNumber, value)) RaiseChanged(); }
        }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand RestoreCommand { get; }

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

        // Fired on any edit (including delete/restore/move) so SettingsViewModel can flip
        // IsDirty - reordering itself is driven from SettingsViewModel directly (it owns the
        // list), which sets IsDirty on its own rather than through this event.
        public event Action? Changed;
        private void RaiseChanged() => Changed?.Invoke();

        // A server-level edit (including delete/restore/toggle) bubbles up the same way a
        // tab-level edit does - see RaiseChanged above.
        private void OnServerChanged() => RaiseChanged();

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
            }
        }
    }
}
