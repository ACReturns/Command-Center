using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommandCenter.Model
{
    // Per-section build configuration (GMS / CMS / Live). Implements INotifyPropertyChanged
    // so the Settings tab and each build tab can share the same instance and stay in sync
    // without any manual refresh plumbing.
    public class SectionSettings : INotifyPropertyChanged
    {
        private string _buildPath = string.Empty;
        private string _versionNumber = string.Empty;

        public string BuildPath
        {
            get => _buildPath;
            set
            {
                if (_buildPath != value)
                {
                    _buildPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string VersionNumber
        {
            get => _versionNumber;
            set
            {
                if (_versionNumber != value)
                {
                    _versionNumber = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class AppSettings
    {
        public SectionSettings Gms { get; set; } = new();
        public SectionSettings Cms { get; set; } = new();
        public SectionSettings Live { get; set; } = new();
    }
}
