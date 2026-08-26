using System;
using System.Collections.Generic;
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

    // A user-added build path beyond the 3 permanent GMS/CMS/Live sections. Belongs to one of
    // the 3 categories (inherits that category's server catalog + client executables) and can
    // be deleted from Settings - unlike Gms/Cms/Live below, which always exist.
    public class ExtraSectionSettings : SectionSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public SectionCategory Category { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class AppSettings
    {
        public SectionSettings Gms { get; set; } = new();
        public SectionSettings Cms { get; set; } = new();
        public SectionSettings Live { get; set; } = new();

        // User-added build paths beyond the permanent 3, created/removed from the Settings tab.
        public List<ExtraSectionSettings> ExtraSections { get; set; } = new();
    }
}
