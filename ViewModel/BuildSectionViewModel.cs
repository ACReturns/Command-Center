using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommandCenter.Model;
using CommandCenter.Services;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    public enum SectionMode
    {
        NewBuild,
        Patch,
        Launch
    }

    // Drives one section's tab (GMS / CMS / Live Service). The same view is reused for all
    // three sections; only the wrapped SectionSettings instance and title differ.
    public class BuildSectionViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _appSettings;
        private readonly SectionSettings _settings;

        private static readonly string[] FixedExecutableOptions = { "MapleStoryA.exe", "MapleStory.exe" };

        private SectionMode _selectedMode = SectionMode.Launch;
        private string _sourceZipPath = string.Empty;
        private string _pendingVersion = string.Empty;
        private bool _isBusy;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string? _selectedLaunchTarget;
        private string? _selectedExecutable;
        private LaunchServerOption? _selectedServerOption;

        public BuildSectionViewModel(string sectionTitle, SectionSettings settings, AppSettings appSettings, SettingsService settingsService, bool useFixedLaunchCatalog)
        {
            SectionTitle = sectionTitle;
            _settings = settings;
            _appSettings = appSettings;
            _settingsService = settingsService;
            UsesFixedLaunchCatalog = useFixedLaunchCatalog;

            _settings.PropertyChanged += Settings_PropertyChanged;

            BrowseSourceCommand = new RelayCommand(_ => BrowseSource());
            RunUpdateCommand = new AsyncRelayCommand(_ => RunUpdateAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceZipPath) && HasBuildPath);
            LaunchCommand = new RelayCommand(_ => Launch(), _ => !IsBusy && HasBuildPath && (UsesFixedLaunchCatalog
                ? SelectedExecutable != null && SelectedServerOption != null
                : !string.IsNullOrWhiteSpace(SelectedLaunchTarget)));
            RefreshLaunchTargetsCommand = new RelayCommand(_ => RefreshLaunchTargets());

            if (UsesFixedLaunchCatalog)
            {
                _selectedExecutable = ExecutableOptions.FirstOrDefault();
                _selectedServerOption = ServerOptions.FirstOrDefault();
            }
            else
            {
                RefreshLaunchTargets();
            }
        }

        public string SectionTitle { get; }

        public string CurrentBuildPath => _settings.BuildPath;
        public string VersionNumber => string.IsNullOrWhiteSpace(_settings.VersionNumber) ? "Not set" : _settings.VersionNumber;
        public bool HasBuildPath => !string.IsNullOrWhiteSpace(_settings.BuildPath) && Directory.Exists(_settings.BuildPath);

        public SectionMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    OnPropertyChanged(nameof(IsNewBuild));
                    OnPropertyChanged(nameof(IsPatch));
                    OnPropertyChanged(nameof(IsLaunch));
                    OnPropertyChanged(nameof(IsUpdatePanelVisible));
                    OnPropertyChanged(nameof(IsLaunchPanelVisible));
                    OnPropertyChanged(nameof(RunButtonLabel));

                    if (value == SectionMode.Launch && !UsesFixedLaunchCatalog)
                    {
                        RefreshLaunchTargets();
                    }
                }
            }
        }

        public bool IsNewBuild
        {
            get => _selectedMode == SectionMode.NewBuild;
            set { if (value) SelectedMode = SectionMode.NewBuild; }
        }

        public bool IsPatch
        {
            get => _selectedMode == SectionMode.Patch;
            set { if (value) SelectedMode = SectionMode.Patch; }
        }

        public bool IsLaunch
        {
            get => _selectedMode == SectionMode.Launch;
            set { if (value) SelectedMode = SectionMode.Launch; }
        }

        public bool IsUpdatePanelVisible => SelectedMode != SectionMode.Launch;
        public bool IsLaunchPanelVisible => SelectedMode == SectionMode.Launch;
        public string RunButtonLabel => SelectedMode == SectionMode.NewBuild ? "Apply New Build" : "Apply Patch";

        public string SourceZipPath
        {
            get => _sourceZipPath;
            set => SetProperty(ref _sourceZipPath, value);
        }

        public string PendingVersion
        {
            get => _pendingVersion;
            set => SetProperty(ref _pendingVersion, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsNotBusy));
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public ObservableCollection<string> LaunchTargets { get; } = new();

        public string? SelectedLaunchTarget
        {
            get => _selectedLaunchTarget;
            set => SetProperty(ref _selectedLaunchTarget, value);
        }

        // True for GMS/CMS: a fixed executable + fixed QA server picker instead of a folder scan.
        public bool UsesFixedLaunchCatalog { get; }

        public IReadOnlyList<string> ExecutableOptions => FixedExecutableOptions;
        public IReadOnlyList<LaunchServerOption> ServerOptions => LaunchServerCatalog.Servers;

        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (SetProperty(ref _selectedExecutable, value))
                {
                    OnPropertyChanged(nameof(IsSelectedExecutableMissing));
                }
            }
        }

        public LaunchServerOption? SelectedServerOption
        {
            get => _selectedServerOption;
            set => SetProperty(ref _selectedServerOption, value);
        }

        public bool IsSelectedExecutableMissing =>
            UsesFixedLaunchCatalog && HasBuildPath && !string.IsNullOrEmpty(SelectedExecutable) &&
            !File.Exists(Path.Combine(CurrentBuildPath, SelectedExecutable));

        public RelayCommand BrowseSourceCommand { get; }
        public AsyncRelayCommand RunUpdateCommand { get; }
        public RelayCommand LaunchCommand { get; }
        public RelayCommand RefreshLaunchTargetsCommand { get; }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(CurrentBuildPath));
            OnPropertyChanged(nameof(VersionNumber));
            OnPropertyChanged(nameof(HasBuildPath));
            OnPropertyChanged(nameof(IsSelectedExecutableMissing));

            if (e.PropertyName == nameof(SectionSettings.BuildPath) && !UsesFixedLaunchCatalog)
            {
                RefreshLaunchTargets();
            }
        }

        private void BrowseSource()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select {SectionTitle} build/patch archive",
                Filter = "Zip Archives (*.zip)|*.zip",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SourceZipPath = dialog.FileName;
            }
        }

        private void RefreshLaunchTargets()
        {
            LaunchTargets.Clear();

            if (string.IsNullOrWhiteSpace(CurrentBuildPath) || !Directory.Exists(CurrentBuildPath))
            {
                return;
            }

            var launchers = Directory.GetFiles(CurrentBuildPath, "*.bat")
                .Concat(Directory.GetFiles(CurrentBuildPath, "*.exe"))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name);

            foreach (var launcher in launchers)
            {
                LaunchTargets.Add(launcher);
            }

            SelectedLaunchTarget = LaunchTargets.FirstOrDefault();
        }

        private async Task RunUpdateAsync()
        {
            var mode = SelectedMode == SectionMode.NewBuild ? UpdateMode.NewBuild : UpdateMode.Patch;

            if (mode == UpdateMode.NewBuild)
            {
                var confirm = MessageBox.Show(
                    $"This will remove the existing {SectionTitle} build at:\n{CurrentBuildPath}\n\nand replace it entirely. Continue?",
                    "Confirm New Build",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            IsBusy = true;
            ProgressPercent = 0;
            StatusText = "Starting...";

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await BuildUpdateService.RunAsync(SourceZipPath, CurrentBuildPath, mode, progress);

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} updated successfully.";

                if (!UsesFixedLaunchCatalog)
                {
                    RefreshLaunchTargets();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Launch()
        {
            if (UsesFixedLaunchCatalog)
            {
                LaunchWithCatalog();
            }
            else
            {
                LaunchGeneric();
            }
        }

        private void LaunchWithCatalog()
        {
            if (string.IsNullOrEmpty(SelectedExecutable) || SelectedServerOption == null)
            {
                return;
            }

            string fullPath = Path.Combine(CurrentBuildPath, SelectedExecutable);

            if (!File.Exists(fullPath))
            {
                StatusText = $"{SelectedExecutable} was not found in the build folder.";
                MessageBox.Show(StatusText, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var process = new Process();
                process.StartInfo.FileName = fullPath;
                process.StartInfo.Arguments = SelectedServerOption.LaunchArgument;
                process.StartInfo.WorkingDirectory = CurrentBuildPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();

                StatusText = $"Launched {SelectedExecutable} -> {SelectedServerOption.DisplayName}.";
            }
            catch (Exception ex)
            {
                StatusText = $"Launch failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LaunchGeneric()
        {
            if (string.IsNullOrWhiteSpace(SelectedLaunchTarget))
            {
                return;
            }

            string fullPath = Path.Combine(CurrentBuildPath, SelectedLaunchTarget);

            try
            {
                var process = new Process();
                process.StartInfo.FileName = fullPath;
                process.StartInfo.WorkingDirectory = CurrentBuildPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();

                StatusText = $"Launched {SelectedLaunchTarget}.";
            }
            catch (Exception ex)
            {
                StatusText = $"Launch failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
