using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    // three sections; only the wrapped SectionSettings instance, title, and server catalog
    // differ. Every section launches by picking one of two fixed client executables plus a
    // server from that section's own fixed catalog (see LaunchServerCatalog).
    public class BuildSectionViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _appSettings;
        private readonly SectionSettings _settings;

        private SectionMode _selectedMode = SectionMode.Launch;
        private string _sourceArchivePath = string.Empty;
        private string _pendingVersion = string.Empty;
        private bool _isBusy;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string? _selectedExecutable;
        private LaunchServerOption? _selectedServerOption;
        private CancellationTokenSource? _updateCancellation;

        public BuildSectionViewModel(string sectionTitle, SectionSettings settings, AppSettings appSettings, SettingsService settingsService, IReadOnlyList<LaunchServerOption> serverOptions)
        {
            SectionTitle = sectionTitle;
            _settings = settings;
            _appSettings = appSettings;
            _settingsService = settingsService;
            ServerOptions = serverOptions;

            _settings.PropertyChanged += Settings_PropertyChanged;

            BrowseSourceCommand = new RelayCommand(_ => BrowseSource());
            RunUpdateCommand = new AsyncRelayCommand(_ => RunUpdateAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceArchivePath) && HasBuildPath);
            CancelUpdateCommand = new RelayCommand(_ => _updateCancellation?.Cancel(), _ => IsBusy && _updateCancellation != null);
            LaunchCommand = new RelayCommand(_ => Launch(), _ => !IsBusy && HasBuildPath && SelectedExecutable != null && SelectedServerOption != null);

            _selectedExecutable = ExecutableOptions.FirstOrDefault();
            _selectedServerOption = ServerOptions.FirstOrDefault();
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

        public string SourceArchivePath
        {
            get => _sourceArchivePath;
            set => SetProperty(ref _sourceArchivePath, value);
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

        // Same 2 client executables for every section (GMS / CMS / Live Service).
        public IReadOnlyList<string> ExecutableOptions => LaunchServerCatalog.Executables;

        // This section's own fixed server catalog (GMS, CMS, and Live each get their own).
        public IReadOnlyList<LaunchServerOption> ServerOptions { get; }

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
            HasBuildPath && !string.IsNullOrEmpty(SelectedExecutable) &&
            !File.Exists(Path.Combine(CurrentBuildPath, SelectedExecutable));

        public RelayCommand BrowseSourceCommand { get; }
        public AsyncRelayCommand RunUpdateCommand { get; }
        public RelayCommand CancelUpdateCommand { get; }
        public RelayCommand LaunchCommand { get; }

        // Raised right after a build/patch finishes extracting, with this section's build-drive
        // free-space status (or null if it couldn't be checked). MainViewModel subscribes to this
        // on every section to drive the storage tracker at the bottom of the window.
        public event EventHandler<DiskSpaceStatus?>? DiskSpaceStatusChanged;

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(CurrentBuildPath));
            OnPropertyChanged(nameof(VersionNumber));
            OnPropertyChanged(nameof(HasBuildPath));
            OnPropertyChanged(nameof(IsSelectedExecutableMissing));
        }

        private void BrowseSource()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select {SectionTitle} build/patch archive",
                Filter = "Build/Patch Archives (*.zip;*.7z)|*.zip;*.7z|Zip Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SourceArchivePath = dialog.FileName;
            }
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

            // BuildUpdateService now runs the whole extraction/copy on a background thread, so the
            // window stays movable and every tab (including this section's own) stays usable while
            // this runs. The token lets the user back out of a long-running one via CancelUpdateCommand.
            _updateCancellation = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await BuildUpdateService.RunAsync(SourceArchivePath, CurrentBuildPath, mode, progress, _updateCancellation.Token);

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} updated successfully.";

                // Refresh the storage tracker with this section's build drive now that the extraction landed.
                DiskSpaceStatusChanged?.Invoke(this, DiskSpaceService.CheckDiskSpace(CurrentBuildPath));
            }
            catch (OperationCanceledException)
            {
                StatusText = $"{SectionTitle} update cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }

        private void Launch()
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
    }
}
