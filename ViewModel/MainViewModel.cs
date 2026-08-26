using CommandCenter.Model;
using CommandCenter.Services;

namespace CommandCenter.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private int _selectedTabIndex;
        private DiskSpaceStatus? _storageStatus;

        public MainViewModel()
        {
            var settingsService = new SettingsService();
            var appSettings = settingsService.Load();

            Gms = new BuildSectionViewModel("GMS", appSettings.Gms, appSettings, settingsService, LaunchServerCatalog.GmsServers);
            Cms = new BuildSectionViewModel("CMS", appSettings.Cms, appSettings, settingsService, LaunchServerCatalog.CmsServers);
            Live = new BuildSectionViewModel("Live Service", appSettings.Live, appSettings, settingsService, LaunchServerCatalog.LiveServers);
            Settings = new SettingsViewModel(appSettings, settingsService);
            ServerStatus = new ServerStatusViewModel(() => SelectedTabIndex = 0);

            // Any section's completed build/patch refreshes the storage tracker; whichever one
            // finishes most recently is what the single tracker at the bottom of the window shows.
            Gms.DiskSpaceStatusChanged += (_, status) => StorageStatus = status;
            Cms.DiskSpaceStatusChanged += (_, status) => StorageStatus = status;
            Live.DiskSpaceStatusChanged += (_, status) => StorageStatus = status;

            // Startup check so the tracker has a real number before any build ever runs, rather
            // than sitting blank until the first extraction. Uses whichever section already has a
            // configured build path, checked in GMS -> CMS -> Live order.
            BuildSectionViewModel? startupSection =
                Gms.HasBuildPath ? Gms :
                Cms.HasBuildPath ? Cms :
                Live.HasBuildPath ? Live : null;

            if (startupSection != null)
            {
                StorageStatus = DiskSpaceService.CheckDiskSpace(startupSection.CurrentBuildPath);
            }
        }

        public BuildSectionViewModel Gms { get; }
        public BuildSectionViewModel Cms { get; }
        public BuildSectionViewModel Live { get; }
        public SettingsViewModel Settings { get; }
        public ServerStatusViewModel ServerStatus { get; }

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
    }
}
