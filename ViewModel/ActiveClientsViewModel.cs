using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // Content ViewModel for the ephemeral "Active Clients" tab - see MainViewModel.
    // EnsureActiveClientsTab/OnActiveClientsTrackingEmpty for how the tab itself gets created and
    // torn down around this. This class, and the TabInfo/TabSettings MainViewModel wraps it in,
    // never touch _appSettings.Tabs - nothing here is ever persisted or shows up in Settings, and a
    // fresh app start always begins with no tracked clients, which is correct: a PID from a
    // previous run means nothing once the Command Center instance that launched it is gone.
    public class ActiveClientsViewModel : ViewModelBase
    {
        public ActiveClientsViewModel()
        {
            CloseCommand = new RelayCommand(CloseClient);
        }

        public ObservableCollection<ActiveClientViewModel> Clients { get; } = new();

        public RelayCommand CloseCommand { get; }

        // Fired once Clients drops from 1 back to 0 - every tracked client has either exited on its
        // own or been closed from here. MainViewModel listens for this to remove the tab itself;
        // see its own comment for why that has to be event-driven rather than polled.
        public event EventHandler? TrackingBecameEmpty;

        public void Add(LaunchedClientInfo info)
        {
            var client = new ActiveClientViewModel(info);
            client.Exited += Client_Exited;
            Clients.Add(client);
        }

        private void Client_Exited(object? sender, EventArgs e)
        {
            if (sender is not ActiveClientViewModel client)
            {
                return;
            }

            client.Exited -= Client_Exited;
            Clients.Remove(client);

            if (Clients.Count == 0)
            {
                TrackingBecameEmpty?.Invoke(this, EventArgs.Empty);
            }
        }

        // The Close button on each row in ActiveClientsView. Confirms before doing anything
        // destructive (same MessageBox.Show(YesNo, Warning) pattern ServerStatusViewModel.
        // DeleteServer and BuildSectionViewModel's own confirmations already use), then hands off
        // to ActiveClientViewModel.Close() - actual removal from Clients happens afterward, off of
        // that client's own Exited event (Client_Exited above), same path a client that quit on its
        // own takes, so there's only one place that ever removes a row.
        private void CloseClient(object? parameter)
        {
            if (parameter is not ActiveClientViewModel client)
            {
                return;
            }

            var confirm = MessageBox.Show(Application.Current?.MainWindow,
                $"End this session? This will close {client.ExecutableName} (PID {client.Pid}), launched from \"{client.TabTitle}\" on {client.ServerDisplayName}.",
                "Close Client", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                client.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Couldn't close {client.ExecutableName} (PID {client.Pid}): {ex.Message}",
                    "Close Client", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
