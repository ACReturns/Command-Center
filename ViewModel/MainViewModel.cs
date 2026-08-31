using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommandCenter.Model;
using CommandCenter.Services;

namespace CommandCenter.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;

        // Tab-side BuildSectionViewModel for each extra section, keyed by its ExtraSectionSettings.Id
        // so a delete from Settings can find and remove exactly the right one from its tab.
        private readonly Dictionary<Guid, BuildSectionViewModel> _extraViewModelsById = new();

        // How often the storage tracker re-checks its current drive on its own, so free space
        // changing from outside the app (someone clearing files on that drive in Explorer, another
        // tool writing to it) eventually shows up without anyone touching Command Center. A single
        // DriveInfo.AvailableFreeSpace read is one cheap OS call, not a directory scan, so this
        // interval is chosen for a steady, unobtrusive tracker rather than to spare the system -
        // it could safely run much more often if tighter feedback is ever wanted.
        private static readonly TimeSpan StorageRecheckInterval = TimeSpan.FromHours(1);

        private readonly DispatcherTimer _storageRecheckTimer;

        private int _selectedTabIndex;
        private DiskSpaceStatus? _storageStatus;

        // Build path behind the tracker's current reading (startup pick, or whichever section's
        // build/patch/push completed most recently) - the periodic recheck re-checks this same
        // path so it never jumps the tracker to a different drive on its own.
        private string _lastCheckedBuildPath = string.Empty;

        public MainViewModel()
        {
            _settingsService = new SettingsService();
            _appSettings = _settingsService.Load();

            Gms = new BuildSectionViewModel("GMS", _appSettings.Gms, _appSettings, _settingsService, LaunchServerCatalog.GmsServers, supportsPushedToLive: false);
            Cms = new BuildSectionViewModel("CMS", _appSettings.Cms, _appSettings, _settingsService, LaunchServerCatalog.CmsServers, supportsPushedToLive: false);
            Live = new BuildSectionViewModel("Live Service", _appSettings.Live, _appSettings, _settingsService, LaunchServerCatalog.LiveServers, supportsPushedToLive: true);
            Settings = new SettingsViewModel(_appSettings, _settingsService, AddExtraSection, RemoveExtraSection);
            ServerStatus = new ServerStatusViewModel(() => SelectedTabIndex = 0);

            // Any section's completed build/patch refreshes the storage tracker; whichever one
            // finishes most recently is what the single tracker at the bottom of the window shows.
            Gms.DiskSpaceStatusChanged += OnDiskSpaceStatusChanged;
            Cms.DiskSpaceStatusChanged += OnDiskSpaceStatusChanged;
            Live.DiskSpaceStatusChanged += OnDiskSpaceStatusChanged;

            // Rehydrate any extra sections saved from a previous session into their tab.
            foreach (var extra in _appSettings.ExtraSections.ToList())
            {
                CreateExtraSectionViewModel(extra);
            }

            // Startup check so the tracker has a real number before any build ever runs, rather
            // than sitting blank until the first extraction. Uses whichever section already has a
            // configured build path, checked in GMS -> CMS -> Live order.
            BuildSectionViewModel? startupSection =
                Gms.HasBuildPath ? Gms :
                Cms.HasBuildPath ? Cms :
                Live.HasBuildPath ? Live : null;

            if (startupSection != null)
            {
                _lastCheckedBuildPath = startupSection.CurrentBuildPath;
                StorageStatus = DiskSpaceService.CheckDiskSpace(_lastCheckedBuildPath);
            }

            _storageRecheckTimer = new DispatcherTimer { Interval = StorageRecheckInterval };
            _storageRecheckTimer.Tick += (_, _) => RecheckStorage();
            _storageRecheckTimer.Start();
        }

        public BuildSectionViewModel Gms { get; }
        public BuildSectionViewModel Cms { get; }
        public BuildSectionViewModel Live { get; }
        public SettingsViewModel Settings { get; }
        public ServerStatusViewModel ServerStatus { get; }

        // Extra (user-added) build sections, grouped by which permanent tab they render under as
        // additional rows. Settings can add/remove from these at any time.
        public ObservableCollection<BuildSectionViewModel> GmsExtras { get; } = new();
        public ObservableCollection<BuildSectionViewModel> CmsExtras { get; } = new();
        public ObservableCollection<BuildSectionViewModel> LiveExtras { get; } = new();

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        public DiskSpaceStatus? StorageStatus
        {
            get => _storageStatus;
            set
            {
                if (SetProperty(ref _storageStatus, value))
                {
                    OnPropertyChanged(nameof(StorageTrackerText));
                    OnPropertyChanged(nameof(IsStorageLow));
                }
            }
        }

        public string StorageTrackerText => StorageStatus switch
        {
            null => "Storage: no build path configured yet.",
            { IsLow: true } s => $"Low disk space on drive {s.DriveLabel} — only {s.FreeGigabytes:F1} GB left. Be mindful if you need to pull another build; you'll need to free up space first.",
            { } s => $"Drive {s.DriveLabel}: {s.FreeGigabytes:F1} GB free"
        };

        public bool IsStorageLow => StorageStatus?.IsLow ?? false;

        private void OnDiskSpaceStatusChanged(object? sender, DiskSpaceStatus? status)
        {
            if (sender is BuildSectionViewModel section)
            {
                _lastCheckedBuildPath = section.CurrentBuildPath;
            }

            StorageStatus = status;
        }

        // Timer tick: re-reads free space on whatever drive the tracker is currently showing, so
        // external changes (space freed up or eaten elsewhere) surface without a build having to
        // run. No-ops until something has actually been checked at least once.
        private void RecheckStorage()
        {
            if (string.IsNullOrWhiteSpace(_lastCheckedBuildPath))
            {
                return;
            }

            StorageStatus = DiskSpaceService.CheckDiskSpace(_lastCheckedBuildPath);
        }

        // Adds a new extra build section under the given category: persists it immediately, then
        // wires a full New Build/Patch/Launch row into that category's tab. Called from Settings
        // when "+ Add Build Path" -> a category is picked from the dropdown.
        private ExtraSectionSettings AddExtraSection(SectionCategory category)
        {
            int existingCount = _appSettings.ExtraSections.Count(x => x.Category == category);
            var extra = new ExtraSectionSettings
            {
                Category = category,
                Label = $"{LaunchServerCatalog.DisplayName(category)} Extra {existingCount + 1}"
            };

            _appSettings.ExtraSections.Add(extra);
            _settingsService.Save(_appSettings);

            CreateExtraSectionViewModel(extra);
            return extra;
        }

        // Removes an extra build section: un-persists it and pulls its row out of whichever tab
        // it was in. This is the only way an extra section goes away - its tab row has no delete
        // option of its own.
        private void RemoveExtraSection(ExtraSectionSettings extra)
        {
            _appSettings.ExtraSections.Remove(extra);
            _settingsService.Save(_appSettings);

            if (_extraViewModelsById.Remove(extra.Id, out var vm))
            {
                vm.DiskSpaceStatusChanged -= OnDiskSpaceStatusChanged;

                // The extra section's Documents folder shouldn't outlive the section itself -
                // "we don't want to keep anything from the extra builds."
                vm.StopWatching();
                vm.DeleteDocumentsFolder();

                ExtrasFor(extra.Category).Remove(vm);
            }
        }

        private void CreateExtraSectionViewModel(ExtraSectionSettings extra)
        {
            var vm = new BuildSectionViewModel(extra.Label, extra, _appSettings, _settingsService, LaunchServerCatalog.ServersFor(extra.Category), supportsPushedToLive: extra.Category == SectionCategory.Live);
            vm.DiskSpaceStatusChanged += OnDiskSpaceStatusChanged;
            _extraViewModelsById[extra.Id] = vm;
            ExtrasFor(extra.Category).Add(vm);
        }

        private ObservableCollection<BuildSectionViewModel> ExtrasFor(SectionCategory category) => category switch
        {
            SectionCategory.Gms => GmsExtras,
            SectionCategory.Cms => CmsExtras,
            SectionCategory.Live => LiveExtras,
            _ => GmsExtras
        };
    }
}
