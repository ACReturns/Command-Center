using System.Diagnostics;

namespace CommandCenter.Model
{
    // A snapshot of one client launched from a BuildSectionViewModel.Launch call, captured at the
    // instant it started - see BuildSectionViewModel.ClientLaunched. Everything here besides
    // Process/Pid is a plain string copy of what Launch saw at that moment (source tab title,
    // chosen server, current version), not a live reference back to that tab's TabSettings -
    // renaming the tab or changing its server/version afterward doesn't retroactively update an
    // already-running row in the Active Clients tab. See ActiveClientViewModel, which wraps this
    // for display, and ActiveClientsViewModel, which owns the tracked list.
    //
    // Deliberately does NOT attempt to talk back to the running client (no Output.log tailing, no
    // stdin/console command injection) - that was tried three different ways for a previous version
    // of this feature and none of them worked; see project memory's active_clients_tab.md. This is
    // tracking + the ability to end the process, nothing more.
    public class LaunchedClientInfo
    {
        public LaunchedClientInfo(Process process, string tabTitle, string serverDisplayName, string versionNumber, string executableName)
        {
            Process = process;
            Pid = process.Id;
            TabTitle = tabTitle;
            ServerDisplayName = serverDisplayName;
            VersionNumber = versionNumber;
            ExecutableName = executableName;
        }

        public Process Process { get; }

        // Captured separately from Process.Id rather than read live - Process.Id throws once the
        // process has exited, and ActiveClientViewModel still needs a PID to show/log after that.
        public int Pid { get; }

        public string TabTitle { get; }
        public string ServerDisplayName { get; }
        public string VersionNumber { get; }
        public string ExecutableName { get; }
    }
}
