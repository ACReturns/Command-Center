using System;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    // One row in New Build's "additional files" list (see
    // BuildSectionViewModel.AdditionalArchives/HasAdditionalArchives) - an extra archive applied as
    // a patch, straight on top of the base file in "Build / Patch Archive" above, right after New
    // Build's own extraction lands. Purely transient input for the run about to happen, same as
    // SourceArchivePath itself - never persisted to TabSettings, and the list isn't remembered
    // across app restarts.
    public class AdditionalArchivePathViewModel : ViewModelBase
    {
        private readonly Action<AdditionalArchivePathViewModel> _onRemove;
        private string _path = string.Empty;

        public AdditionalArchivePathViewModel(Action<AdditionalArchivePathViewModel> onRemove)
        {
            _onRemove = onRemove;
            BrowseCommand = new RelayCommand(_ => Browse());
            RemoveCommand = new RelayCommand(_ => _onRemove(this));
        }

        // Read-only in the UI (browsed to via BrowseCommand only, same as SourceArchivePath's own
        // TextBox) - blank until the user picks a file for this row.
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand RemoveCommand { get; }

        private void Browse()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select additional build/patch archive",
                Filter = "Build/Patch Archives (*.zip;*.7z)|*.zip;*.7z|Zip Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                Path = dialog.FileName;
            }
        }
    }
}
