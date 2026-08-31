using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommandCenter.Model
{
    // LEGACY (pre-tabs) per-section shape. Nothing in the app writes or reads these directly
    // anymore - they're kept only so a settings.json saved by an older version still
    // deserializes without throwing, and SettingsService.Load migrates them into AppSettings.Tabs
    // (below) the first time such a file is loaded. Do not build new features on this class.
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

    // LEGACY - see SectionSettings above. Superseded by TabSettings (IsPermanent = false).
    public class ExtraSectionSettings : SectionSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public SectionCategory Category { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class AppSettings
    {
        // Every top-level tab (GMS/CMS/Live/Server Status/Settings and any extra), in display
        // order - the current, live model. See TabSettings and SettingsService's migration.
        public List<TabSettings> Tabs { get; set; } = new();

        // LEGACY - see SectionSettings above. Present only for backward-compatible
        // deserialization of a settings.json saved before tabs existed; SettingsService.Load
        // resets these to empty immediately after migrating them into Tabs once.
        public SectionSettings Gms { get; set; } = new();
        public SectionSettings Cms { get; set; } = new();
        public SectionSettings Live { get; set; } = new();
        public List<ExtraSectionSettings> ExtraSections { get; set; } = new();
    }
}
