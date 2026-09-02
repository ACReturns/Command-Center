using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommandCenter.Model
{
    // One .exe discovered in a BuildSection tab's BuildPath folder (see TabSettings.Executables).
    // Replaces the old fixed LaunchServerCatalog.Executables list ("MapleStoryA.exe"/"MapleStory.exe"
    // only) - now every tab's Select Executable dropdown is built from whatever .exe files actually
    // sit in that tab's own build folder, discovered by Helpers.ExecutableScanner and reconciled
    // into this list by DraftTabViewModel every time Settings is opened or a new folder is picked.
    //
    // There's no BuiltIn/Custom split like TabServerEntry - every entry here was found on disk, not
    // typed in by hand, so the only thing the user controls is IsEnabled (via Settings' "Available
    // Executables" checkboxes). FileName is the identity DraftTabViewModel's rescan matches on
    // (case-insensitively, same as Windows file names) to decide whether a discovered .exe is new
    // or already known.
    public class TabExecutableEntry : INotifyPropertyChanged
    {
        private string _fileName = string.Empty;
        private bool _isEnabled = true;

        // Just the file name (e.g. "MapleStory.exe"), never a full path - always resolved against
        // this tab's own BuildPath at launch time, same reasoning as CustomServerGroupSettings.FileName.
        public string FileName
        {
            get => _fileName;
            set { if (_fileName != value) { _fileName = value; OnPropertyChanged(); } }
        }

        // Whether this .exe shows up in this tab's Select Executable dropdown right now - the
        // checkbox in Settings' "Available Executables" list. Defaults to true so a freshly
        // discovered .exe is immediately usable without an extra step, same default TabServerEntry
        // uses for a newly-seeded built-in server.
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
