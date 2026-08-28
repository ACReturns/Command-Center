using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommandCenter.Model;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    // The only place a section's build path can be changed. Build tabs display it read-only.
    public class SettingsViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private readonly Func<SectionCategory, ExtraSectionSettings> _addExtraSection;
        private readonly Action<ExtraSectionSettings> _removeExtraSection;
        private string _statusText = string.Empty;
        private DispatcherTimer? _statusClearTimer;

        public SettingsViewModel(AppSettings appSettings, SettingsService settingsService,
            Func<SectionCategory, ExtraSectionSettings> addExtraSection, Action<ExtraSectionSettings> removeExtraSection)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;
            _addExtraSection = addExtraSection;
            _removeExtraSection = removeExtraSection;

            BrowseGmsPathCommand = new RelayCommand(_ => BrowsePath(Gms, "GMS"));
            BrowseCmsPathCommand = new RelayCommand(_ => BrowsePath(Cms, "CMS"));
            BrowseLivePathCommand = new RelayCommand(_ => BrowsePath(Live, "Live Service"));

            AddExtraSectionCommand = new RelayCommand(param => AddExtraSection((SectionCategory)param!));
            SaveCommand = new RelayCommand(_ => Save());

            foreach (var extra in _appSettings.ExtraSections)
            {
                ExtraSections.Add(new ExtraSectionRowViewModel(extra, RemoveExtraSection));
            }
        }

        public SectionSettings Gms => _appSettings.Gms;
        public SectionSettings Cms => _appSettings.Cms;
        public SectionSettings Live => _appSettings.Live;

        // Additional build paths beyond the 3 permanent ones above - each of these can be
        // deleted from here (and only from here; GMS/CMS/Live above never can be).
        public ObservableCollection<ExtraSectionRowViewModel> ExtraSections { get; } = new();

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (SetProperty(ref _statusText, value))
                {
                    RestartStatusClearTimer();
                }
            }
        }

        // Keeps the "Saved at ..." message on screen for 10 seconds so it's readable, then clears
        // itself so it doesn't linger indefinitely.
        private void RestartStatusClearTimer()
        {
            _statusClearTimer?.Stop();

            if (string.IsNullOrEmpty(_statusText))
            {
                return;
            }

            if (_statusClearTimer == null)
            {
                _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _statusClearTimer.Tick += (_, _) =>
                {
                    _statusClearTimer!.Stop();
                    StatusText = string.Empty;
                };
            }

            _statusClearTimer.Start();
        }

        public RelayCommand BrowseGmsPathCommand { get; }
        public RelayCommand BrowseCmsPathCommand { get; }
        public RelayCommand BrowseLivePathCommand { get; }
        public RelayCommand AddExtraSectionCommand { get; }
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

        private void AddExtraSection(SectionCategory category)
        {
            var extra = _addExtraSection(category);
            ExtraSections.Add(new ExtraSectionRowViewModel(extra, RemoveExtraSection));
        }

        private void RemoveExtraSection(ExtraSectionRowViewModel row)
        {
            var confirm = MessageBox.Show(
                $"Delete \"{row.Settings.Label}\"?\n\nThis removes it from Settings and from its tab. This can't be undone.",
                "Delete Build Path",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            _removeExtraSection(row.Settings);
            ExtraSections.Remove(row);
        }

        private void Save()
        {
            _settingsService.Save(_appSettings);
            StatusText = $"Saved at {DateTime.Now:t}.";
        }
    }

    // One row in Settings for a user-added build path: wraps its ExtraSectionSettings with a
    // folder-browse command and a delete command. This is the only place deletion is offered -
    // the matching row in its tab has no delete option of its own.
    public class ExtraSectionRowViewModel : ViewModelBase
    {
        public ExtraSectionRowViewModel(ExtraSectionSettings settings, Action<ExtraSectionRowViewModel> onDelete)
        {
            Settings = settings;
            BrowseCommand = new RelayCommand(_ => BrowsePath());
            DeleteCommand = new RelayCommand(_ => onDelete(this));
        }

        public ExtraSectionSettings Settings { get; }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand DeleteCommand { get; }

        private void BrowsePath()
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Select {Settings.Label} build folder",
                InitialDirectory = string.IsNullOrWhiteSpace(Settings.BuildPath) || !Directory.Exists(Settings.BuildPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : Settings.BuildPath
            };

            if (dialog.ShowDialog() == true)
            {
                Settings.BuildPath = dialog.FolderName;
            }
        }
    }
}
