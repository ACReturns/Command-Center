using System;
using System.Diagnostics;
using System.Windows;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // One row in the Active Clients tab - wraps a single launched client process, captured at
    // launch time by BuildSectionViewModel.Launch (see LaunchedClientInfo). TabTitle/
    // ServerDisplayName/VersionNumber are a snapshot from the moment the client launched, not a
    // live binding back to the source tab's TabSettings - see LaunchedClientInfo's own comment.
    public class ActiveClientViewModel : ViewModelBase
    {
        private readonly Process _process;

        public ActiveClientViewModel(LaunchedClientInfo info)
        {
            Info = info;
            _process = info.Process;

            // EnableRaisingEvents has to already be true (BuildSectionViewModel.Launch sets it
            // right after Process.Start()) for Exited to ever fire here - see that method's
            // comment on the narrow exit-race this can't fully close.
            _process.Exited += Process_Exited;

            // The process may have already exited in the gap between ClientLaunched being raised
            // and this handler attaching (a fast-crashing client, or just a slow UI thread) -
            // Process.Exited won't fire retroactively for a handler that wasn't subscribed yet, so
            // check once up front too rather than leaving a dead row stuck in the list forever.
            if (_process.HasExited)
            {
                RaiseExited();
            }
        }

        public LaunchedClientInfo Info { get; }

        public string TabTitle => Info.TabTitle;
        public string ServerDisplayName => Info.ServerDisplayName;
        public string VersionNumber => Info.VersionNumber;
        public string ExecutableName => Info.ExecutableName;
        public int Pid => Info.Pid;

        // Raised exactly once per row, whether the client exited on its own or Close() below just
        // killed it - ActiveClientsViewModel removes this row from Clients either way, from the
        // same handler, so there's only one removal path to reason about.
        public event EventHandler? Exited;

        private void Process_Exited(object? sender, EventArgs e) =>
            // Process.Exited fires on a ThreadPool thread; Clients is an ObservableCollection bound
            // to the UI, so the removal this triggers has to happen on the dispatcher thread.
            Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseExited));

        private void RaiseExited()
        {
            _process.Exited -= Process_Exited;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        // Called by ActiveClientsViewModel.CloseCommand after the user confirms. Killing the whole
        // tree (not just the PID we launched) so a client that spawned its own child processes
        // doesn't leave any of them running behind it.
        public void Close()
        {
            if (_process.HasExited)
            {
                // Already gone - Process_Exited already has (or is about to) remove this row.
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited in the gap between the HasExited check above and Kill() itself - fine,
                // same as the HasExited branch, nothing left to do here.
            }
            // Win32Exception (couldn't be killed - already terminating, access denied, etc.) is
            // deliberately left to propagate: ActiveClientsViewModel.CloseCommand catches it and
            // tells the user, rather than this silently doing nothing.
        }
    }
}
