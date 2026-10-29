using System.IO;

namespace CommandCenter.ViewModel
{
    // One row in the Build Repo tab's "Local Files" list (BuildRepoViewModel.LocalFiles) - a .zip/.7z
    // already on this PC, applied to the target tab's build in list order. Order is changed by
    // dragging rows around (BuildRepoView code-behind); the row's ✕ removes it. Purely transient
    // input for the run about to happen - never persisted to settings.json.
    public sealed class LocalExtractFileViewModel : ViewModelBase
    {
        private int _position;
        private string _modeText = string.Empty;

        public LocalExtractFileViewModel(string path)
        {
            FullPath = path;
            try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
        }

        public string FullPath { get; }
        public string FileName => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
        public long SizeBytes { get; }

        /// <summary>1-based apply order, shown at the start of the row.</summary>
        public int Position { get => _position; set => SetProperty(ref _position, value); }

        /// <summary>"New Build" or "Patch" - how this file will be applied, given its place in the list.</summary>
        public string ModeText { get => _modeText; set => SetProperty(ref _modeText, value); }
    }
}
