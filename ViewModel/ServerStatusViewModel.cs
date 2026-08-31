using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    public class ServerStatusViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;

        public ServerStatusViewModel(AppSettings appSettings, SettingsService settingsService, Action closeAction)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;

            var groupSettings = appSettings.ServerStatus;
            Live = new ServerGroupViewModel("Live", AppPaths.LiveWorldsFile, groupSettings.LiveExpanded);
            Staging = new ServerGroupViewModel("Staging", AppPaths.StagingWorldsFile, groupSettings.StagingExpanded);
            Test = new ServerGroupViewModel("Test", AppPaths.TestWorldsFile, groupSettings.TestExpanded);
            Groups = new[] { Live, Staging, Test };

            RefreshAllCommand = new AsyncRelayCommand(_ => Task.WhenAll(Live.RefreshAsync(), Staging.RefreshAsync(), Test.RefreshAsync()));
            CloseCommand = new RelayCommand(_ => closeAction());

            // Give the user an at-a-glance status as soon as the app starts, without requiring a click.
            RefreshAllCommand.Execute(null);
        }

        public ServerGroupViewModel Live { get; }
        public ServerGroupViewModel Staging { get; }
        public ServerGroupViewModel Test { get; }
        public IReadOnlyList<ServerGroupViewModel> Groups { get; }

        public AsyncRelayCommand RefreshAllCommand { get; }
        public RelayCommand CloseCommand { get; }

        // Legend icons at the top of the view - same gifs used per-world, bound once here so the
        // XAML doesn't need to know the app's file layout.
        public string ServerUpGifPath => AppPaths.ServerUpGif;
        public string ServerDownGifPath => AppPaths.ServerDownGif;

        // Called by MainViewModel.SelectedTab when the user navigates away from the Server Status
        // tab - persists whichever groups are currently expanded/collapsed so it's remembered next
        // launch. Not written on every toggle; only once, on leaving the tab.
        public void SaveExpandedState()
        {
            _appSettings.ServerStatus.LiveExpanded = Live.IsExpanded;
            _appSettings.ServerStatus.StagingExpanded = Staging.IsExpanded;
            _appSettings.ServerStatus.TestExpanded = Test.IsExpanded;
            _settingsService.Save(_appSettings);
        }
    }
}
