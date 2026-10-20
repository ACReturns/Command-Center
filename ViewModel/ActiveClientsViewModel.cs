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
            BringToFrontCommand = new RelayCommand(BringClientToFront);
        }

        // A client that exits on its own within this long of launching gets an explanation popup
        // instead of just silently vanishing from the list.
        private static readonly TimeSpan EarlyExitThreshold = TimeSpan.FromSeconds(15);

        public ObservableCollection<ActiveClientViewModel> Clients { get; } = new();

        public RelayCommand CloseCommand { get; }
        public RelayCommand BringToFrontCommand { get; }

        // Fired once Clients drops from 1 back to 0 - every tracked client has either exited on its
        // own or been closed from here. MainViewModel listens for this to remove the tab itself;
        // see its own comment for why that has to be event-driven rather than polled.
        public event EventHandler? TrackingBecameEmpty;

        public void Add(LaunchedClientInfo info)
        {
            var client = new ActiveClientViewModel(info);
            client.Exited += Client_Exited;
            Clients.Add(client);

            // Only after Exited is subscribed - see ActiveClientViewModel.StartTracking.
            client.StartTracking();
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

            if (!client.WasClosedByUser && client.Runtime < EarlyExitThreshold)
            {
                // Queued rather than shown inline so the row/tab removal above finishes rendering
                // first instead of sitting frozen behind a modal dialog.
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowEarlyExitNotice(client)));
            }
        }

        private static void ShowEarlyExitNotice(ActiveClientViewModel client)
        {
            double seconds = Math.Max(0, client.Runtime.TotalSeconds);
            string codeText = client.ExitCode is int code
                ? $"Exit code: {code} (0x{unchecked((uint)code):X8})"
                : "Exit code: unavailable";

            string message =
                $"{client.ExecutableName} closed on its own {seconds:0.#} seconds after launching.\n\n" +
                $"Launched from: {client.TabTitle}\n" +
                $"Server: {client.ServerDisplayName}\n" +
                $"Version: {client.VersionNumber}\n" +
                $"{codeText}\n\n" +
                $"Likely reason: {DescribeExitCode(client.ExitCode)}";

            MessageBox.Show(Application.Current?.MainWindow, message, "Client Closed Unexpectedly",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Best-effort translation of the exit code into something readable. The common Windows
        // NTSTATUS crash codes are unambiguous; anything else is the client's own code, so the
        // reason is the usual suspects for a client that bails right at startup.
        private static string DescribeExitCode(int? exitCode)
        {
            if (exitCode is not int code)
            {
                return "Windows didn't report an exit code. Try launching the executable directly from the build folder to see any error it shows.";
            }

            switch (unchecked((uint)code))
            {
                case 0:
                    return "The client shut itself down normally (exit code 0). Usually this means it couldn't reach the selected server, " +
                           "another copy was already running, or it handed off to a different process (launcher/patcher) that also closed.";
                case 1:
                    return "The client reported a generic startup failure. Check that the build finished extracting/patching completely " +
                           "and that the selected server is up (Server Status tab).";
                case 0xC0000005:
                    return "The client crashed (access violation). Often a corrupted or mismatched build - try re-verifying or re-installing the build.";
                case 0xC0000135:
                    return "A required DLL is missing from the build folder or system (e.g. a Visual C++ / DirectX runtime). The build may be incomplete.";
                case 0xC000007B:
                    return "A DLL in the build is corrupted or the wrong architecture (32-bit vs 64-bit). The build may be incomplete or mixed between versions.";
                case 0xC0000142:
                    return "A DLL failed to initialize at startup. Usually a missing runtime or a corrupted file in the build.";
                case 0xC0000409:
                    return "The client hit a fatal internal error and terminated itself (stack buffer overrun / fail-fast).";
                case 0xC0000374:
                    return "The client crashed due to heap corruption.";
                case 0xC00000FD:
                    return "The client crashed with a stack overflow.";
                case 0xC000013A:
                case 0x40010004:
                    return "The client was terminated externally (closed from Task Manager, another tool, or Windows shutting it down).";
                case 0xC0000022:
                    return "Access was denied. The client may need to be run as administrator, or antivirus blocked it.";
                case 0xE0434352:
                    return "The client threw an unhandled .NET exception.";
                default:
                    return "The client exited with its own error code. Check that the build is complete and the selected server is reachable, " +
                           "then try launching again.";
            }
        }

        // "Bring to Front" button on each row - restores/activates the client's window and pulls it
        // onto Command Center's monitor if it's on another screen or off-screen.
        private void BringClientToFront(object? parameter)
        {
            if (parameter is not ActiveClientViewModel client)
            {
                return;
            }

            try
            {
                if (client.BringToFront() == WindowActivator.Result.NoWindowFound)
                {
                    MessageBox.Show(Application.Current?.MainWindow,
                        $"{client.ExecutableName} (PID {client.Pid}) is running but doesn't have a visible window yet. " +
                        "It may still be loading - try again in a few seconds.",
                        "Bring to Front", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Couldn't bring {client.ExecutableName} to the front: {ex.Message}",
                    "Bring to Front", MessageBoxButton.OK, MessageBoxImage.Error);
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
