using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CommandCenter.ViewModel
{
    public class ServerStatusViewModel : ViewModelBase
    {
        public ServerStatusViewModel(Action closeAction)
        {
            Live = new ServerGroupViewModel("Live", AppPaths.LiveWorldsFile);
            Staging = new ServerGroupViewModel("Staging", AppPaths.StagingWorldsFile);
            Test = new ServerGroupViewModel("Test", AppPaths.TestWorldsFile);
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
    }
}
