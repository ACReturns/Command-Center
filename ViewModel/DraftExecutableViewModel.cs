using System;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // A scratch, editable copy of one TabExecutableEntry - the executable-list equivalent of
    // DraftServerViewModel, and used the same way: lives only while the Settings tab is open (see
    // DraftTabViewModel.Executables), touches nothing on the live TabSettings.Executables until
    // SettingsViewModel.Save() commits it.
    //
    // Unlike DraftServerViewModel there's no Edit/Delete here - every entry is something
    // DraftTabViewModel.RescanExecutables actually found sitting in the tab's build folder, not
    // something typed in by hand, so the only thing the user can do to one is flip IsEnabled.
    public class DraftExecutableViewModel : ViewModelBase
    {
        private bool _isEnabled;

        private DraftExecutableViewModel(string fileName, bool isEnabled)
        {
            FileName = fileName;
            _isEnabled = isEnabled;
        }

        public static DraftExecutableViewModel FromEntry(TabExecutableEntry entry) => new(entry.FileName, entry.IsEnabled);

        // A .exe RescanExecutables just found in the build folder that wasn't already in the
        // persisted list - starts enabled, same default TabExecutableEntry.IsEnabled uses.
        public static DraftExecutableViewModel CreateDiscovered(string fileName) => new(fileName, isEnabled: true);

        public string FileName { get; }

        // The checkbox in Settings' "Available Executables" list - whether this .exe shows up in
        // this tab's Select Executable dropdown.
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (SetProperty(ref _isEnabled, value)) RaiseChanged(); }
        }

        // Fired only on a user-driven IsEnabled toggle, not on RescanExecutables adding/removing
        // rows to match what's actually on disk - see DraftTabViewModel.RescanExecutables, which
        // deliberately doesn't call RaiseChanged itself so reopening Settings on an unchanged
        // folder never shows a false "Unsaved changes".
        public event Action? Changed;
        private void RaiseChanged() => Changed?.Invoke();

        // Applied by SettingsViewModel.Save() into the live TabSettings.Executables list.
        public TabExecutableEntry ToEntry() => new() { FileName = FileName, IsEnabled = IsEnabled };
    }
}
