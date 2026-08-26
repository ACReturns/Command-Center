using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private int _selectedTabIndex;

        public MainViewModel()
        {
            var settingsService = new SettingsService();
            var appSettings = settingsService.Load();

            Gms = new BuildSectionViewModel("GMS", appSettings.Gms, appSettings, settingsService, LaunchServerCatalog.GmsServers);
            Cms = new BuildSectionViewModel("CMS", appSettings.Cms, appSettings, settingsService, LaunchServerCatalog.CmsServers);
            Live = new BuildSectionViewModel("Live Service", appSettings.Live, appSettings, settingsService, LaunchServerCatalog.LiveServers);
            Settings = new SettingsViewModel(appSettings, settingsService);
            ServerStatus = new ServerStatusViewModel(() => SelectedTabIndex = 0);
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
    }
}
