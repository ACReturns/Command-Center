using System;
using System.IO;
using CommandCenter.Model;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    // The only place a section's build path can be changed. Build tabs display it read-only.
    public class SettingsViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private string _statusText = string.Empty;

        public SettingsViewModel(AppSettings appSettings, SettingsService settingsService)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;

            BrowseGmsPathCommand = new RelayCommand(_ => BrowsePath(Gms, "GMS"));
            BrowseCmsPathCommand = new RelayCommand(_ => BrowsePath(Cms, "CMS"));
            BrowseLivePathCommand = new RelayCommand(_ => BrowsePath(Live, "Live Service"));

            SaveCommand = new RelayCommand(_ => Save());
        }

        public SectionSettings Gms => _appSettings.Gms;
        public SectionSettings Cms => _appSettings.Cms;
        public SectionSettings Live => _appSettings.Live;

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public RelayCommand BrowseGmsPathCommand { get; }
        public RelayCommand BrowseCmsPathCommand { get; }
        public RelayCommand BrowseLivePathCommand { get; }
        public RelayCommand SaveCommand { get; }

        private static void BrowsePath(SectionSettings section, string label)
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Select {label} build folder",
                InitialDirectory = string.IsNullOrWhiteSpace(section.BuildPath) || !Directory.Exists(section.BuildPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : section.BuildPath
            };

            if (dialog.ShowDialog() == true)
            {
                section.BuildPath = dialog.FolderName;
            }
        }

        private void Save()
        {
            _settingsService.Save(_appSettings);
            StatusText = $"Saved at {DateTime.Now:t}.";
        }
    }
}
